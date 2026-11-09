// MAZDA "learn by watching" — page-side recorder.
//
// Injected by the recorder spec into the attached tab (current document via
// frame.evaluate, future documents via page.addInitScript). It is a single
// function expression; the spec calls it with a config object:
//
//   { sessionId, binding, allowedOrigins: string[], maskAll: boolean }
//
// Everything leaves the page through window[binding](evt), a Playwright
// exposeBinding. Event shapes:
//
//   { kind: 'mark',      actionId }                       action boundary (drives network correlation)
//   { kind: 'action',    actionId, data: { type, target, value?, key?, dest?, scroll? } }
//   { kind: 'mutations', actionId, data: { appeared, disappeared, changed, text, settleMs } }
//   { kind: 'ambient',   data: { count } }                mutations outside any action window
//
// Privacy: values of sensitive inputs (passwords, card fields, OTP, anything
// under [data-mz-mask]/[data-private]/[data-sensitive]) never leave the page —
// they are replaced by "•••" here, before the binding is called.
(function mzRecorder(cfg) {
  'use strict';

  try {
    var origins = cfg.allowedOrigins || [];
    if (origins.length && origins.indexOf(location.origin) < 0) return;
  } catch (_) { return; }

  var prev = window.__mzRec;
  if (prev) {
    if (prev.sessionId === cfg.sessionId) return;      // already installed for this session
    try { prev.stop(); } catch (_) { /* stale install from a dead session */ }
  }

  var BINDING = cfg.binding;
  var docId = Math.random().toString(36).slice(2, 8);
  var seqN = 0;
  var listeners = [];
  var timers = [];

  function now() { return performance.now(); }

  function emit(kind, data, actionId) {
    try {
      var f = window[BINDING];
      if (typeof f !== 'function') return;
      f({ kind: kind, actionId: actionId == null ? null : actionId, data: data == null ? null : data, url: location.href });
    } catch (_) { /* binding gone (session ended) — drop silently */ }
  }

  // ── Small utils ───────────────────────────────────────────────────

  function clean(s, max) {
    if (s == null) return '';
    s = String(s).replace(/\s+/g, ' ').trim();
    max = max || 80;
    return s.length > max ? s.slice(0, max) + '…' : s;
  }

  function cssEscape(s) {
    try { return CSS.escape(s); } catch (_) { return String(s).replace(/[^a-zA-Z0-9_-]/g, '\\$&'); }
  }

  function attrSel(name, value) {
    return '[' + name + '="' + String(value).replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"]';
  }

  function rootOf(el) {
    try { var r = el.getRootNode(); return r && r.querySelectorAll ? r : document; } catch (_) { return document; }
  }

  function countSel(sel, el) {
    try { return rootOf(el).querySelectorAll(sel).length; } catch (_) { return -1; }
  }

  function isElement(n) { return !!n && n.nodeType === 1; }

  function parentOf(el) {
    if (!el) return null;
    if (el.parentElement) return el.parentElement;
    var r = el.getRootNode && el.getRootNode();
    return r && r.host ? r.host : null;   // step out of a shadow root
  }

  // ── Sensitivity / masking ─────────────────────────────────────────

  var SENSITIVE_RE = /pass|secret|token|otp|cvv|cvc|card|iban|ssn|pin\b/i;
  var SENSITIVE_AC = /cc-|one-time-code|current-password|new-password/i;

  function isSensitive(el) {
    if (!isElement(el)) return false;
    try {
      if (el.closest && el.closest('[data-mz-mask],[data-private],[data-sensitive]')) return true;
    } catch (_) { }
    var type = (el.getAttribute('type') || '').toLowerCase();
    if (type === 'password') return true;
    if (SENSITIVE_AC.test(el.getAttribute('autocomplete') || '')) return true;
    var hay = [el.getAttribute('name'), el.id, el.getAttribute('aria-label'), el.getAttribute('placeholder')]
      .filter(Boolean).join(' ');
    return SENSITIVE_RE.test(hay);
  }

  // Text the user typed into a rich-text editor surfaces as DOM text, not as
  // an input value — so masking must also cover text inside editable hosts.
  var EDITABLE_SEL = '[contenteditable=""],[contenteditable="true"],[contenteditable="plaintext-only"]';
  function editableHost(el) {
    try { return el.closest ? el.closest(EDITABLE_SEL) : null; } catch (_) { return null; }
  }

  /** True when an element's visible text must not be recorded. */
  function hidesText(el) {
    if (!isElement(el)) return false;
    if (isSensitive(el)) return true;
    var host = editableHost(el);
    return !!host && (cfg.maskAll || isSensitive(host));
  }

  function maskValue(el, v) {
    v = v == null ? '' : String(v);
    if (cfg.maskAll || isSensitive(el)) return { value: '•••', masked: true, length: v.length };
    return { value: v.length > 200 ? v.slice(0, 200) + '…' : v };
  }

  // ── Roles / names ─────────────────────────────────────────────────

  function implicitRole(el) {
    var t = el.tagName.toLowerCase();
    switch (t) {
      case 'button': return 'button';
      case 'a': return el.hasAttribute('href') ? 'link' : null;
      case 'select': return (el.multiple || el.size > 1) ? 'listbox' : 'combobox';
      case 'textarea': return 'textbox';
      case 'option': return 'option';
      case 'li': return 'listitem';
      case 'ul': case 'ol': return 'list';
      case 'table': return 'table';
      case 'tr': return 'row';
      case 'td': return 'cell';
      case 'th': return 'columnheader';
      case 'dialog': return 'dialog';
      case 'nav': return 'navigation';
      case 'img': return el.getAttribute('alt') === '' ? null : 'img';
      case 'h1': case 'h2': case 'h3': case 'h4': case 'h5': case 'h6': return 'heading';
      case 'summary': return 'button';
      case 'input': {
        var ty = (el.getAttribute('type') || 'text').toLowerCase();
        if (ty === 'button' || ty === 'submit' || ty === 'reset' || ty === 'image') return 'button';
        if (ty === 'checkbox') return 'checkbox';
        if (ty === 'radio') return 'radio';
        if (ty === 'range') return 'slider';
        if (ty === 'number') return 'spinbutton';
        if (ty === 'search') return 'searchbox';
        if (ty === 'text' || ty === 'email' || ty === 'tel' || ty === 'url' || ty === '')
          return el.hasAttribute('list') ? 'combobox' : 'textbox';
        return null;
      }
    }
    return null;
  }

  function roleOf(el) {
    var r = (el.getAttribute('role') || '').trim().split(/\s+/)[0];
    return r || implicitRole(el);
  }

  // Roles whose accessible name may come from their text content (ARIA
  // "name from: contents"). For any other role — listbox, status, dialog,
  // textbox… — only author-supplied names count, so a role=…[name=…]
  // selector built from text would match nothing in Playwright.
  var CONTENT_NAME_ROLES = /^(button|cell|checkbox|columnheader|gridcell|heading|link|menuitem|menuitemcheckbox|menuitemradio|option|radio|row|rowheader|switch|tab|tooltip|treeitem)$/;
  var nameFromContent = false;   // side channel set by accName()

  function accName(el) {
    nameFromContent = false;
    var al = el.getAttribute('aria-label');
    if (al && clean(al)) return clean(al);
    var lb = el.getAttribute('aria-labelledby');
    if (lb) {
      var s = lb.split(/\s+/).map(function (id) {
        var n = document.getElementById(id); return n ? n.textContent : '';
      }).join(' ');
      if (clean(s)) return clean(s);
    }
    var tag = el.tagName.toLowerCase();
    if (tag === 'input' || tag === 'textarea' || tag === 'select') {
      if (el.labels && el.labels.length) return clean(el.labels[0].textContent);
      var ty = (el.getAttribute('type') || '').toLowerCase();
      if ((ty === 'submit' || ty === 'button') && el.value) return clean(el.value);
      return clean(el.getAttribute('placeholder') || el.getAttribute('title') || '');
    }
    if (tag === 'img') return clean(el.getAttribute('alt') || '');
    var title = el.getAttribute('title');
    if (hidesText(el)) return title ? clean(title) : '';
    var content = clean(el.innerText || el.textContent || '');
    if (content) { nameFromContent = true; return content; }
    return title ? clean(title) : '';
  }

  // ── Selector candidates ───────────────────────────────────────────

  var TESTID_ATTRS = ['data-testid', 'data-test-id', 'data-test', 'data-cy', 'data-qa'];
  // Ids that are framework-generated and change between renders/sessions.
  var GENERATED_ID = /\d{3,}|^:r|^(mat|mui|rc|ember|react-select|radix|headlessui|downshift|el|v|cdk|ng)-.*\d|[a-f0-9]{8,}/i;
  // Hashed (CSS-modules / CSS-in-JS) and transient state classes.
  // Build-generated classes (CSS-in-JS, CSS modules) change between builds.
  // Their hash parts mix digits or scatter capitals: `sc-bdVaJa`,
  // `css-1x2y3z`, `Button_root__a1B2c`. Hand-written names — BEM
  // (`card__title`, `card--active`), Bootstrap (`btn-primary`), snake_case
  // (`user_name`) — are made of words and stay usable.
  var GENERATED_PREFIX = /^(css|sc|jss|emotion|styled|jsx|svelte|tw)-/;
  function looksGenerated(seg) {
    if (seg.length < 5) return false;
    if (/\d/.test(seg) && /[a-zA-Z]/.test(seg)) return true;
    var caps = (seg.slice(1).match(/[A-Z]/g) || []).length;
    return seg.length <= 8 && caps >= 2;
  }
  var HASHED_CLASS = {
    test: function (c) {
      if (GENERATED_PREFIX.test(c) || /\d{3,}/.test(c)) return true;
      var segs = c.split(/[-_]+/);
      for (var i = 0; i < segs.length; i++) if (looksGenerated(segs[i])) return true;
      return false;
    }
  };
  var STATE_CLASS = /(^|[-_])(active|focus(ed)?|hover(ed)?|open(ed)?|selected|checked|disabled|visible|hidden|show(n)?|expanded|collapsed|current|is-|has-)/i;

  function stableClasses(el) {
    var out = [];
    var list = el.classList ? Array.prototype.slice.call(el.classList) : [];
    for (var i = 0; i < list.length && out.length < 2; i++) {
      var c = list[i];
      if (c.length > 40 || HASHED_CLASS.test(c) || STATE_CLASS.test(c)) continue;
      out.push(c);
    }
    return out;
  }

  function cssPath(el) {
    var parts = [];
    var cur = el;
    for (var depth = 0; cur && isElement(cur) && depth < 5; depth++) {
      var tag = cur.tagName.toLowerCase();
      if (cur.id && !GENERATED_ID.test(cur.id)) { parts.unshift('#' + cssEscape(cur.id)); break; }
      var tid = null;
      for (var k = 0; k < TESTID_ATTRS.length && !tid; k++) {
        var v = cur.getAttribute(TESTID_ATTRS[k]);
        if (v) tid = attrSel(TESTID_ATTRS[k], v);
      }
      if (tid) { parts.unshift(tid); break; }
      if (tag === 'body' || tag === 'html') break;
      var part = tag;
      var p = cur.parentElement;
      if (p) {
        var same = 0, idx = 0;
        for (var c = p.firstElementChild; c; c = c.nextElementSibling) {
          if (c.tagName === cur.tagName) { same++; if (c === cur) idx = same; }
        }
        if (same > 1) part += ':nth-of-type(' + idx + ')';
      }
      parts.unshift(part);
      cur = p;
    }
    return parts.join(' > ');
  }

  // ── DOM text, data attributes, structure ──────────────────────────

  // Text as the DOM holds it (textContent semantics, which is what scripts
  // read), with a space between separate text nodes so "school" and "high"
  // don't fuse into "schoolhigh". NOT innerText: that applies CSS
  // (text-transform: capitalize turns "high" into "High") and a script
  // matching what it saw would then fail against the real DOM.
  function domText(el, max) {
    if (!el) return '';
    var parts = [];
    try {
      var w = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
      var n, total = 0;
      while ((n = w.nextNode()) && total < 400) {
        var t = n.nodeValue.replace(/\s+/g, ' ').trim();
        if (!t) continue;
        var p = n.parentElement;
        if (p && (p.tagName === 'SCRIPT' || p.tagName === 'STYLE')) continue;
        parts.push(t); total += t.length;
      }
    } catch (_) { return clean(el.textContent || '', max); }
    return clean(parts.join(' '), max || 80);
  }

  function textTransform(el) {
    try { var tt = getComputedStyle(el).textTransform; return tt && tt !== 'none' ? tt : null; } catch (_) { return null; }
  }

  // data-* attributes other than test ids — state carriers such as
  // data-priority="high" / data-done="true" / data-state="open".
  var NOISE_DATA = /^data-(v-|reactid|reactroot|testid|test-id|test|cy|qa|mz-)/;
  function dataAttrs(el) {
    var out = null;
    if (!el.attributes) return null;
    for (var i = 0; i < el.attributes.length; i++) {
      var a = el.attributes[i];
      if (a.name.indexOf('data-') !== 0 || NOISE_DATA.test(a.name)) continue;
      if (a.value.length > 60) continue;
      (out = out || {})[a.name] = a.value;
    }
    return out;
  }

  function clsSel(el) {
    return stableClasses(el).map(function (c) { return '.' + cssEscape(c); }).join('');
  }

  // One step of a relative path: tag + stable classes (+ type for inputs).
  function stepOf(el) {
    var t = el.tagName.toLowerCase();
    var s = t + clsSel(el);
    if (t === 'input') s += attrSel('type', (el.getAttribute('type') || 'text').toLowerCase());
    return s;
  }

  // Shortest descendant path from `root` to `el` that is unique inside
  // root, built from steps; positional only as a last resort.
  function relativePath(root, el) {
    if (root === el) return '';
    if (!root.contains(el)) return '';
    var steps = [];
    for (var cur = el; cur && cur !== root && isElement(cur); cur = cur.parentElement) steps.push(cur);
    var sel = stepOf(el);
    var count = function (q) { try { return root.querySelectorAll(q).length; } catch (_) { return -1; } };
    if (count(sel) === 1) return sel;
    for (var i = 1; i < steps.length; i++) {
      sel = stepOf(steps[i]) + ' ' + sel;
      if (count(sel) === 1) return sel;
    }
    // Disambiguate the leaf by position among same-step siblings.
    var leaf = stepOf(el), p = el.parentElement, k = 0, idx = 0;
    for (var c = p ? p.firstElementChild : null; c; c = c.nextElementSibling) {
      if (c.tagName === el.tagName) { k++; if (c === el) idx = k; }
    }
    var parentPath = p && p !== root ? relativePath(root, p) : '';
    var pos = leaf + ':nth-of-type(' + idx + ')';
    return parentPath ? parentPath + ' > ' + pos : pos;
  }

  // A stable selector for a container, if one exists (id, test id, classes).
  function anchorSelector(el, depth) {
    if (!isElement(el) || el === document.body || el === document.documentElement) return null;
    if (el.id && !GENERATED_ID.test(el.id)) return '#' + cssEscape(el.id);
    for (var k = 0; k < TESTID_ATTRS.length; k++) {
      var v = el.getAttribute(TESTID_ATTRS[k]);
      if (v) return attrSel(TESTID_ATTRS[k], v);
    }
    var cs = clsSel(el);
    if (cs) {
      var q = el.tagName.toLowerCase() + cs;
      if (countSel(q, el) === 1) return q;
    }
    if ((depth || 0) < 2) {
      var up = anchorSelector(el.parentElement, (depth || 0) + 1);
      if (up) return up + ' > ' + stepOf(el);
    }
    return null;
  }

  function shapeKey(el) { return el.tagName + '|' + stableClasses(el).sort().join('.'); }

  var LIST_PARENT = /^(UL|OL|TBODY|THEAD|DL|MENU)$/;
  var CONTROL_TAG = /^(BUTTON|A|INPUT|SELECT|TEXTAREA|LABEL)$/;
  var LIST_ROLE = /^(list|listbox|grid|rowgroup|tree|menu|menubar|tablist|radiogroup|feed)$/;

  // Is `el` (or one of its close ancestors) one item of a repeated set? If
  // so, describe the set: a selector matching EVERY item, how many there
  // are, which one this is, and the path from the item to `el`. This is
  // what lets a function address "row i" instead of the row that happened
  // to be clicked.
  function collectionOf(el) {
    var cur = el;
    for (var depth = 0; cur && isElement(cur) && depth < 6; depth++) {
      var p = cur.parentElement;
      if (!p || cur === document.body || p === document.documentElement) break;
      var key = shapeKey(cur), n = 0, idx = -1;
      for (var c = p.firstElementChild; c; c = c.nextElementSibling) {
        if (shapeKey(c) === key) { if (c === cur) idx = n; n++; }
      }
      var listy = LIST_PARENT.test(p.tagName) || LIST_ROLE.test(p.getAttribute('role') || '');
      // Sibling controls (Save / Cancel, Edit / Delete) share a shape but are
      // different controls, not a repeated set — keep climbing to the row.
      var controlSiblings = !listy && CONTROL_TAG.test(cur.tagName);
      if ((n >= 2 || listy) && !controlSiblings && cur.tagName !== 'OPTION') {
        var anchor = anchorSelector(p, 0);
        if (anchor) {
          var item = anchor + ' > ' + stepOf(cur);
          var out = { item: item, count: countSel(item, cur), index: idx };
          if (cur !== el) { var rel = relativePath(cur, el); if (rel) out.inner = rel; }
          return { info: out, node: cur };
        }
      }
      cur = p;
    }
    return null;
  }

  var MEANINGFUL_TAG = /^(INPUT|SELECT|TEXTAREA|BUTTON|LABEL|A|IMG|H1|H2|H3|H4|H5|H6|TIME|PROGRESS|METER|OUTPUT)$/;

  // Compact inventory of an element's meaningful descendants: what a
  // script would read (titles, badges, data-* state, checkbox state) or
  // act on inside it. Paths are relative to `root`.
  function outline(root, maxItems) {
    var out = [];
    var self = { sel: '', tag: root.tagName.toLowerCase() };
    var rd = dataAttrs(root); if (rd) self.data = rd;
    if (rd) out.push(self);
    try {
      var w = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
      var n, visited = 0;
      while ((n = w.nextNode()) && out.length < (maxItems || 15) && visited < 300) {
        visited++;
        if (isOwnNoise(n)) continue;
        var data = dataAttrs(n);
        var role = (n.getAttribute('role') || '').trim();
        var cls = stableClasses(n);
        if (!data && !role && !cls.length && !MEANINGFUL_TAG.test(n.tagName)) continue;
        var item = { sel: relativePath(root, n), tag: n.tagName.toLowerCase() };
        var r = roleOf(n); if (r) item.role = r;
        if (data) item.data = data;
        var t = n.tagName;
        if (t === 'INPUT' || t === 'SELECT' || t === 'TEXTAREA') {
          var nm = accName(n); if (nm) item.name = nm;
          if (n.type === 'checkbox' || n.type === 'radio') item.checked = !!n.checked;
          else if (!isSensitive(n) && !cfg.maskAll) item.value = clean(n.value, 40);
          if (t === 'SELECT' && !cfg.maskAll && !isSensitive(n)) item.options = Array.prototype.slice.call(n.options, 0, 10).map(function (o) { return o.value; });
        } else if (!hidesText(n)) {
          var tx = domText(n, 40); if (tx) item.text = tx;
          var tt = textTransform(n); if (tt && tx) item.textTransform = tt;
        }
        out.push(item);
      }
    } catch (_) { }
    return out;
  }

  // Playwright role / text selectors can't be counted with
  // querySelectorAll; approximate the engine so "best selector" logic sees
  // that role=button[name="Edit"] matches one button PER ROW.
  // Counting role/text matches walks the DOM and reads innerText (forces
  // layout) — on a big table that's real time spent inside the user's click.
  // Each synchronous turn (one pointerdown, one mutation flush) gets a small
  // budget; past it counts are reported as unknown (null), which selector
  // ranking already treats as "not proven unique".
  var COUNT_BUDGET_MS = 25;
  var countDeadline = 0;
  function countingAllowed() {
    var now = performance.now();
    if (!countDeadline) {
      countDeadline = now + COUNT_BUDGET_MS;
      Promise.resolve().then(function () { countDeadline = 0; });
    }
    return now <= countDeadline;
  }

  function countRole(el, role, name) {
    if (!countingAllowed()) return null;
    var saved = nameFromContent;
    try {
      var root = rootOf(el), pool = root.querySelectorAll(el.tagName.toLowerCase() + ',[role="' + role + '"]');
      if (pool.length > 300) return null;
      var n = 0;
      for (var i = 0; i < pool.length; i++) {
        if ((i & 15) === 15 && !countingAllowed()) return null;
        var p = pool[i];
        if (!p.getClientRects().length && p.tagName !== 'INPUT') continue;   // hidden: the engine skips it
        if (roleOf(p) === role && accName(p) === name) n++;
      }
      return n;
    } catch (_) { return null; } finally { nameFromContent = saved; }
  }

  function countText(el, text) {
    if (!countingAllowed()) return null;
    try {
      var pool = rootOf(el).querySelectorAll(el.tagName.toLowerCase());
      if (pool.length > 300) return null;
      var n = 0;
      for (var i = 0; i < pool.length; i++) {
        if ((i & 15) === 15 && !countingAllowed()) return null;
        // Cheap textContent pre-check before domText's innerText (layout).
        var tc = pool[i].textContent;
        if (!tc || tc.length < text.length * 0.5) continue;
        if (domText(pool[i], 80) === text) n++;
      }
      return n;
    } catch (_) { return null; }
  }

  function candidates(el, d) {
    var out = [];
    var seen = {};
    function add(kind, selector, counted) {
      if (!selector || seen[selector]) return;
      seen[selector] = 1;
      out.push({ kind: kind, selector: selector, count: counted ? countSel(selector, el) : null });
    }
    var tag = el.tagName.toLowerCase();
    for (var i = 0; i < TESTID_ATTRS.length; i++) {
      var v = el.getAttribute(TESTID_ATTRS[i]);
      if (v) add('testid', attrSel(TESTID_ATTRS[i], v), true);
    }
    if (el.id && !GENERATED_ID.test(el.id)) add('id', '#' + cssEscape(el.id), true);
    // Playwright selector-engine string: works directly in page.locator().
    // Only when the name is one Playwright's accessible-name computation
    // will actually produce for this role.
    if (d.role && d.name && d.name.length <= 60 && d.name.indexOf('…') < 0 &&
        (!d.nameFromContent || CONTENT_NAME_ROLES.test(d.role)))
    {
      var roleSel = 'role=' + d.role + '[name="' + d.name.replace(/"/g, '\\"') + '"]';
      if (!seen[roleSel]) { seen[roleSel] = 1; out.push({ kind: 'role', selector: roleSel, count: countRole(el, d.role, d.name) }); }
    }
    var attrs = ['name', 'aria-label', 'placeholder', 'title', 'type'];
    for (var j = 0; j < attrs.length; j++) {
      var a = el.getAttribute(attrs[j]);
      if (!a || a.length > 80) continue;
      if (attrs[j] === 'type' && tag !== 'input') continue;
      add('attr', tag + attrSel(attrs[j], a), true);
    }
    if (d.text && d.text.length <= 40 && d.text.indexOf('…') < 0 && !hidesText(el))
    {
      var textSel = 'text="' + d.text.replace(/"/g, '\\"') + '"';
      if (!seen[textSel]) { seen[textSel] = 1; out.push({ kind: 'text', selector: textSel, count: countText(el, d.text) }); }
    }
    var cls = stableClasses(el);
    if (cls.length) add('class', tag + cls.map(function (c) { return '.' + cssEscape(c); }).join(''), true);
    var path = cssPath(el);
    // A bare tag ("div") is unique only by accident of the moment — useless.
    if (path && /[#\[:>.]/.test(path)) add('css', path, true);
    return out.slice(0, 8);
  }

  function isFloating(el) {
    var cur = el;
    for (var i = 0; cur && isElement(cur) && i < 10; i++) {
      if (cur === document.body || cur === document.documentElement) return false;
      try {
        var pos = getComputedStyle(cur).position;
        if (pos === 'fixed') return true;
        if (pos === 'absolute' && cur.parentElement === document.body) return true;
      } catch (_) { }
      if (cur.parentElement === document.body && cur !== el && i > 0) {
        // Portal root appended to <body> by a library (MUI, AntD, Radix…)
        var cls = (cur.className && cur.className.baseVal === undefined) ? String(cur.className) : '';
        if (/portal|popper|overlay|dropdown|popover|menu|tooltip|modal|dialog/i.test(cls)) return true;
      }
      cur = cur.parentElement;
    }
    return false;
  }

  function ancestors(el) {
    var out = [];
    var cur = parentOf(el);
    for (var i = 0; cur && isElement(cur) && i < 5; i++) {
      if (cur === document.body || cur === document.documentElement) break;
      var a = { tag: cur.tagName.toLowerCase() };
      var r = roleOf(cur); if (r) a.role = r;
      var cls = stableClasses(cur); if (cls.length) a.classes = cls;
      for (var k = 0; k < TESTID_ATTRS.length; k++) {
        var v = cur.getAttribute(TESTID_ATTRS[k]);
        if (v) { a.testid = v; break; }
      }
      out.push(a);
      cur = parentOf(cur);
    }
    return out;
  }

  function describe(el, light) {
    if (!isElement(el)) return null;
    var d = { tag: el.tagName.toLowerCase() };
    var r = roleOf(el); if (r) d.role = r;
    var n = accName(el); if (n) d.name = n;
    var fromContent = nameFromContent;
    if (!hidesText(el)) {
      var t = domText(el, 80);
      if (t && t !== d.name) d.text = t;
      var tt = textTransform(el); if (tt && t) d.textTransform = tt;
    }
    var attrs = {};
    ['id', 'name', 'type', 'placeholder', 'href'].forEach(function (k) {
      var v = el.getAttribute(k);
      if (!v) return;
      if (k === 'href') { try { v = new URL(v, location.href).pathname; } catch (_) { } }
      attrs[k] = v.length > 80 ? v.slice(0, 80) : v;
    });
    TESTID_ATTRS.forEach(function (k) { var v = el.getAttribute(k); if (v) attrs[k] = v; });
    if (Object.keys(attrs).length) d.attrs = attrs;
    var data = dataAttrs(el); if (data) d.data = data;
    d.candidates = candidates(el, { role: d.role, name: d.name, text: d.text, nameFromContent: fromContent });
    // A content-derived name on a role that doesn't take one isn't an
    // accessible name — report it as text so nobody builds a role selector from it.
    if (fromContent && d.role && !CONTENT_NAME_ROLES.test(d.role)) { if (!d.text) d.text = d.name; delete d.name; }
    if (isFloating(el)) d.floating = true;
    try { if (el.getRootNode() instanceof ShadowRoot) d.inShadow = true; } catch (_) { }
    if (!light) d.ancestors = ancestors(el);
    if (el.children) d.childCount = el.children.length;
    // Repeated set this element belongs to (row, card, option…), with the
    // set's own inner structure — so a script can address item i.
    var col = collectionOf(el);
    if (col) {
      d.collection = col.info;
      if (light !== 'brief') d.collection.itemOutline = outline(col.node, 12);
    }
    return d;
  }

  // Appeared element: full identity, its collection, and what's inside it.
  function describeAppeared(el) {
    var d = describe(el, 'brief');
    if (!d) return null;
    var o = outline(el, 15);
    if (o.length) d.outline = o;
    return d;
  }

  function briefOf(el) {
    // Small descriptor for detached nodes (removed from the DOM).
    var b = { tag: el.tagName.toLowerCase() };
    var r = el.getAttribute('role'); if (r) b.role = r;
    for (var k = 0; k < TESTID_ATTRS.length; k++) {
      var v = el.getAttribute(TESTID_ATTRS[k]);
      if (v) { b.testid = v; b.selector = attrSel(TESTID_ATTRS[k], v); break; }
    }
    var cls = stableClasses(el);
    if (cls.length) { b.classes = cls; if (!b.selector) b.selector = b.tag + cls.map(function (c) { return '.' + cssEscape(c); }).join(''); }
    if (!hidesText(el)) { var t = domText(el, 60); if (t) b.text = t; }
    var data = dataAttrs(el); if (data) b.data = data;
    return b;
  }

  function bestSelector(el) {
    var d = { role: roleOf(el), name: accName(el), nameFromContent: nameFromContent };
    d.text = domText(el, 80);
    var cs = candidates(el, d);
    // Same policy as EpisodeBuilder.BestCandidate: semantic first, CSS path last.
    for (var i = 0; i < cs.length; i++) if (cs[i].kind !== 'css' && (cs[i].count === 1 || cs[i].count === null)) return cs[i].selector;
    for (var j = 0; j < cs.length; j++) if (cs[j].count === 1) return cs[j].selector;
    return cs.length ? cs[0].selector : el.tagName.toLowerCase();
  }

  // ── Target resolution ─────────────────────────────────────────────

  var INTERACTIVE_SEL = 'button,a[href],input,select,textarea,summary,label,option,[role],[onclick],[tabindex],[contenteditable=""],[contenteditable="true"]';

  function pickTarget(e) {
    var path = e.composedPath ? e.composedPath() : [e.target];
    for (var i = 0; i < path.length && i < 8; i++) {
      var n = path[i];
      if (!isElement(n)) continue;
      try { if (n.matches(INTERACTIVE_SEL)) return n; } catch (_) { }
    }
    // Nothing interactive on the path: only accept a specific element, not
    // a click on page background (body / main / a section wrapper).
    for (var j = 0; j < path.length; j++) {
      var el = path[j];
      if (!isElement(el)) continue;
      if (/^(HTML|BODY|MAIN|SECTION|ARTICLE|HEADER|FOOTER|FORM)$/.test(el.tagName) && !el.id && !stableClasses(el).length) return null;
      return el;
    }
    return null;
  }

  // ── Mutation windows ──────────────────────────────────────────────
  //
  // Every action opens a window; mutations observed while it is open are
  // attributed to that action. The window closes after 300ms of DOM quiet,
  // 3s after it opened, when the next action starts, or on stop.

  var QUIET_MS = 300, MAX_WINDOW_MS = 3000, MAX_RECORDS = 3000;
  var cur = null;           // { id, t0, lastMut, recs: [], timer }
  var ambient = 0;

  var WATCHED_ATTRS = /^(class|hidden|disabled|open|checked|selected|value|style|aria-.+|data-(?!v-|reactid|testid|test-id|test$|cy|qa|mz-).+)$/;

  var observer = new MutationObserver(function (recs) {
    if (!cur) { ambient += recs.length; return; }
    for (var i = 0; i < recs.length && cur.recs.length < MAX_RECORDS; i++) cur.recs.push(recs[i]);
    cur.lastMut = now();
  });

  function openWindow(id) {
    closeWindow();
    cur = { id: id, t0: now(), lastMut: now(), recs: [], timer: 0 };
    var w = cur;
    function tick() {
      if (cur !== w) return;
      var t = now();
      if ((t - w.lastMut >= QUIET_MS && t - w.t0 >= QUIET_MS) || t - w.t0 >= MAX_WINDOW_MS) { closeWindow(); return; }
      w.timer = setTimeout(tick, 100);
    }
    w.timer = setTimeout(tick, 100);
  }

  function closeWindow() {
    if (!cur) return;
    var w = cur;
    cur = null;
    clearTimeout(w.timer);
    // Pick up records the observer has queued but not yet delivered.
    try { var pending = observer.takeRecords(); for (var i = 0; i < pending.length; i++) w.recs.push(pending[i]); } catch (_) { }
    var summary = summarize(w.recs);
    if (!Object.keys(summary).length) return;   // nothing observable happened
    summary.settleMs = Math.round(w.lastMut - w.t0);
    emit('mutations', summary, w.id);
  }

  function isOwnNoise(n) {
    if (!isElement(n)) return false;
    var t = n.tagName;
    return t === 'SCRIPT' || t === 'STYLE' || t === 'LINK' || t === 'META' || t === 'NOSCRIPT' || t === 'TEMPLATE';
  }

  function hasAncestorIn(node, set) {
    var p = node.parentNode;
    while (p) {
      if (set.has(p)) return true;
      p = p.parentNode || (p.host || null);
    }
    return false;
  }

  function summarize(recs) {
    var added = new Set();
    var removed = [];
    var changed = new Map();   // key → { el, attr, from, to }
    var text = [];
    for (var i = 0; i < recs.length; i++) {
      var r = recs[i];
      if (r.type === 'childList') {
        r.addedNodes.forEach(function (n) { if (isElement(n) && !isOwnNoise(n)) added.add(n); });
        r.removedNodes.forEach(function (n) {
          if (!isElement(n) || isOwnNoise(n)) return;
          if (added.has(n)) { added.delete(n); return; }   // mounted and unmounted within the window
          if (removed.length < 40) removed.push(n);
        });
        if (r.addedNodes.length === 1 && r.addedNodes[0].nodeType === 3 && r.removedNodes.length <= 1 && isElement(r.target)) {
          // Text node swapped — React/Vue text updates look like this.
          var before = r.removedNodes.length ? clean(r.removedNodes[0].textContent, 120) : '';
          var after = clean(r.addedNodes[0].textContent, 120);
          if (before !== after && !hidesText(r.target)) text.push({ el: r.target, from: before, to: after });
        }
      } else if (r.type === 'attributes') {
        if (!WATCHED_ATTRS.test(r.attributeName) || !isElement(r.target)) continue;
        var key = r.attributeName;
        var entry = null;
        changed.forEach(function (v) { if (v.el === r.target && v.attr === key) entry = v; });
        var newVal = r.target.getAttribute(key);
        if (entry) entry.to = newVal;
        else if (changed.size < 120) changed.set(changed.size, { el: r.target, attr: key, from: r.oldValue, to: newVal });
      } else if (r.type === 'characterData') {
        var parent = r.target.parentElement;
        if (!parent || hidesText(parent)) continue;
        var nv = clean(r.target.textContent, 120), ov = clean(r.oldValue, 120);
        if (nv !== ov) text.push({ el: parent, from: ov, to: nv });
      }
    }

    var appeared = [];
    var perCollection = {};
    added.forEach(function (n) {
      if (appeared.length >= 25) return;
      if (!n.isConnected || hasAncestorIn(n, added)) return;
      var d = describeAppeared(n);
      if (!d) return;
      // Items of one collection share a structure: outline the first two,
      // keep only identity + text for the rest.
      if (d.collection) {
        var k = d.collection.item;
        perCollection[k] = (perCollection[k] || 0) + 1;
        if (perCollection[k] > 2) { delete d.outline; delete d.candidates; }
        delete d.collection.itemOutline;   // the outline above already is the item's
      }
      try { var rect = n.getBoundingClientRect(); d.visible = rect.width > 0 && rect.height > 0; } catch (_) { }
      appeared.push(d);
    });

    var disappeared = removed.slice(0, 20).map(briefOf);

    var changedOut = [];
    changed.forEach(function (v) {
      if (changedOut.length >= 30) return;
      if (v.from === v.to || !v.el.isConnected) return;
      var item = { selector: bestSelector(v.el), attr: v.attr };
      if (v.attr === 'class') {
        var a = (v.from || '').split(/\s+/).filter(Boolean), b = (v.to || '').split(/\s+/).filter(Boolean);
        var plus = b.filter(function (c) { return a.indexOf(c) < 0 && !HASHED_CLASS.test(c); });
        var minus = a.filter(function (c) { return b.indexOf(c) < 0 && !HASHED_CLASS.test(c); });
        if (!plus.length && !minus.length) return;
        if (plus.length) item.added = plus.slice(0, 5);
        if (minus.length) item.removed = minus.slice(0, 5);
      } else if (v.attr === 'style') {
        var vis = /display\s*:\s*none|visibility\s*:\s*hidden/i;
        var wasHidden = vis.test(v.from || ''), isHidden = vis.test(v.to || '');
        if (wasHidden === isHidden) return;
        item.from = wasHidden ? 'hidden' : 'shown';
        item.to = isHidden ? 'hidden' : 'shown';
      } else if (v.attr === 'value') {
        if (isSensitive(v.el) || cfg.maskAll) return;
        item.from = clean(v.from, 60); item.to = clean(v.to, 60);
      } else {
        item.from = v.from == null ? null : clean(v.from, 60);
        item.to = v.to == null ? null : clean(v.to, 60);
      }
      changedOut.push(item);
    });

    var textOut = [];
    for (var t = 0; t < text.length && textOut.length < 20; t++) {
      if (!text[t].el.isConnected) continue;
      textOut.push({ selector: bestSelector(text[t].el), from: text[t].from, to: text[t].to });
    }

    var out = {};
    if (appeared.length) out.appeared = appeared;
    if (disappeared.length) out.disappeared = disappeared;
    if (changedOut.length) out.changed = changedOut;
    if (textOut.length) out.text = textOut;
    return out;
  }

  // ── Actions ───────────────────────────────────────────────────────

  function begin() {
    var id = docId + ':' + (++seqN);
    openWindow(id);
    emit('mark', null, id);
    return id;
  }

  function on(target, type, fn) {
    var wrapped = function (e) {
      try { if (e.isTrusted) fn(e); } catch (_) { /* never break the page */ }
    };
    target.addEventListener(type, wrapped, { capture: true, passive: true });
    listeners.push([target, type, wrapped]);
  }

  // Pointer: the boundary opens on pointerdown because many libraries
  // (react-select, MUI, AntD) open popups on mousedown, not click.
  var pending = null;   // { id, el, desc, x, y, t, drag, html5 }
  var lastPointerClickAt = -1e9;

  on(document, 'pointerdown', function (e) {
    if (e.button !== 0) return;
    flushFill();
    var el = pickTarget(e);
    if (!el) return;
    pending = { id: begin(), el: el, desc: describe(el), x: e.clientX, y: e.clientY, t: now(), drag: false, html5: false };
  });

  on(document, 'pointermove', function (e) {
    if (!pending || pending.drag) return;
    if (Math.abs(e.clientX - pending.x) + Math.abs(e.clientY - pending.y) > 5 && (e.buttons & 1)) pending.drag = true;
  });

  on(document, 'pointerup', function (e) {
    if (!pending || pending.html5) return;
    var p = pending;
    if (p.drag) {
      var under = null;
      try { under = document.elementFromPoint(e.clientX, e.clientY); } catch (_) { }
      emit('action', { type: 'drag', target: p.desc, dest: describe(under, true) }, p.id);
      pending = null;
      return;
    }
    var data = { type: 'click', target: p.desc };
    var tag = p.el.tagName;
    if (tag === 'INPUT' && (p.el.type === 'checkbox' || p.el.type === 'radio')) data.checked = !p.el.checked;
    emit('action', data, p.id);
    pending = null;
    lastPointerClickAt = now();
  });

  on(document, 'click', function (e) {
    // Clicks right after our pointer sequence are the same action — including
    // the extra click a <label> forwards to its input. A click with no
    // pointer sequence is keyboard activation (Enter/Space on a control).
    if (now() - lastPointerClickAt < 600) return;
    if (pending) return;   // pointer still down (pointerup not seen yet)
    var el = pickTarget(e);
    if (!el) return;
    var id = begin();
    emit('action', { type: 'click', target: describe(el), via: 'keyboard' }, id);
  });

  // HTML5 drag and drop: no pointerup fires once the browser takes over.
  var dndDest = null;
  on(document, 'dragstart', function () { if (pending) pending.html5 = true; });
  on(document, 'drop', function (e) { dndDest = pickTarget(e); });
  on(document, 'dragend', function () {
    if (!pending) return;
    emit('action', { type: 'drag', target: pending.desc, dest: describe(dndDest, true), html5: true }, pending.id);
    pending = null; dndDest = null;
  });

  // Typing: one "fill" per field, emitted when the user moves on.
  var fill = null;   // { id, el, desc, timer }
  var FILL_IDLE_MS = 500;

  function fieldValue(el) {
    if (el.isContentEditable) return el.innerText;
    return el.value;
  }

  function flushFill() {
    if (!fill) return;
    var f = fill;
    fill = null;
    clearTimeout(f.timer);
    var data = { type: 'fill', target: f.desc };
    var mv = maskValue(f.el, fieldValue(f.el));
    data.value = mv.value;
    if (mv.masked) { data.masked = true; data.length = mv.length; }
    emit('action', data, f.id);
  }

  on(document, 'input', function (e) {
    var el = pickTarget(e) || e.target;
    if (!isElement(el)) return;
    var tag = el.tagName;
    if (tag === 'SELECT') return;                         // handled by change
    if (tag === 'INPUT' && /^(checkbox|radio|range|file|color)$/i.test(el.type)) return;
    if (fill && fill.el !== el) flushFill();
    if (!fill) fill = { id: begin(), el: el, desc: describe(el), timer: 0 };
    else if (cur && cur.id === fill.id) cur.lastMut = now();   // keep the window alive while typing
    else { openWindow(fill.id); emit('mark', null, fill.id); }  // still typing: re-open + re-arm network correlation
    clearTimeout(fill.timer);
    fill.timer = setTimeout(flushFill, FILL_IDLE_MS);
  });

  on(document, 'change', function (e) {
    var el = e.target;
    if (!isElement(el)) return;
    if (el.tagName === 'SELECT') {
      flushFill();
      var id = begin();
      var opt = el.selectedOptions && el.selectedOptions[0];
      var mv = maskValue(el, el.value);
      emit('action', { type: 'select', target: describe(el), value: mv.value, label: opt && !mv.masked ? clean(opt.textContent) : undefined, masked: mv.masked || undefined }, id);
      return;
    }
    if (fill && fill.el === el) flushFill();
  });

  on(document, 'focusout', function (e) { if (fill && fill.el === e.target) flushFill(); });

  var SPECIAL_KEYS = /^(Enter|Escape|Tab|ArrowUp|ArrowDown|ArrowLeft|ArrowRight|Home|End|PageUp|PageDown|Delete|Backspace| )$/;
  on(document, 'keydown', function (e) {
    var el = pickTarget(e) || e.target;
    var editable = isElement(el) && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.isContentEditable);
    var combo = (e.ctrlKey || e.metaKey || e.altKey) && e.key.length === 1;
    if (!combo && !SPECIAL_KEYS.test(e.key)) return;
    // Editing keys inside a text field are part of the fill.
    if (editable && /^(Backspace|Delete|ArrowLeft|ArrowRight|Home|End| )$/.test(e.key)) return;
    if (e.key === ' ' && !editable) return;               // Space activates → reported as click
    flushFill();
    var key = (e.ctrlKey ? 'Control+' : '') + (e.metaKey ? 'Meta+' : '') + (e.altKey ? 'Alt+' : '') +
      (e.shiftKey && (combo || e.key.length > 1) ? 'Shift+' : '') + e.key;
    var id = begin();
    emit('action', { type: 'key', key: key, target: describe(el, true) }, id);
  });

  // Scroll: one action per scroll gesture, reported when it settles. Only
  // kept downstream if it caused something (lazy-load, virtualised rows).
  var scroll = null;  // { id, el, desc, top, left, timer }
  on(document, 'scroll', function (e) {
    var el = e.target === document ? document.scrollingElement || document.documentElement : e.target;
    if (!isElement(el)) return;
    if (!scroll || scroll.el !== el) {
      if (scroll) finishScroll();
      scroll = { id: null, el: el, desc: el === document.scrollingElement ? null : describe(el, true), top: el.scrollTop, left: el.scrollLeft, timer: 0 };
      if (!cur) scroll.id = begin();   // don't steal an open window from a click
    }
    clearTimeout(scroll.timer);
    scroll.timer = setTimeout(finishScroll, 150);
  });

  function finishScroll() {
    if (!scroll) return;
    var s = scroll;
    scroll = null;
    clearTimeout(s.timer);
    var dy = s.el.scrollTop - s.top, dx = s.el.scrollLeft - s.left;
    if (!s.id) return;
    if (!s.desc && Math.abs(dy) < 200 && Math.abs(dx) < 200) return;
    emit('action', { type: 'scroll', target: s.desc, scroll: { dy: Math.round(dy), dx: Math.round(dx), top: Math.round(s.el.scrollTop) } }, s.id);
  }

  // Ambient mutation counter — tells the analyzer how noisy the page is.
  timers.push(setInterval(function () {
    if (ambient > 0) { emit('ambient', { count: ambient }); ambient = 0; }
  }, 5000));

  observer.observe(document.documentElement || document, {
    childList: true, subtree: true,
    attributes: true, attributeOldValue: true,
    characterData: true, characterDataOldValue: true,
  });

  function stop() {
    try { flushFill(); } catch (_) { }
    try { finishScroll(); } catch (_) { }
    try { closeWindow(); } catch (_) { }
    if (ambient > 0) { emit('ambient', { count: ambient }); ambient = 0; }
    try { observer.disconnect(); } catch (_) { }
    listeners.forEach(function (l) { try { l[0].removeEventListener(l[1], l[2], { capture: true }); } catch (_) { } });
    listeners = [];
    timers.forEach(function (t) { clearInterval(t); });
    timers = [];
    if (window.__mzRec && window.__mzRec.sessionId === cfg.sessionId) {
      try { delete window.__mzRec; } catch (_) { window.__mzRec = undefined; }
    }
  }

  window.__mzRec = { sessionId: cfg.sessionId, stop: stop };
  // Flush before the document goes away (full navigation, reload).
  on(window, 'pagehide', function () { try { flushFill(); closeWindow(); } catch (_) { } });
})
