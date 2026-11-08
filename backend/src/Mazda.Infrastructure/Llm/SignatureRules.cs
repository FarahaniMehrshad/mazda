namespace Mazda.Infrastructure.Llm;

/// <summary>
/// The signature-quality rules MAZDA's source analyzers apply
/// (<see cref="LlmComponentAnalyzer"/> for interactive elements,
/// <see cref="LlmInformationalAnalyzer"/> for read targets), stated so they
/// hold whatever the evidence is. The source analyzers phrase them in terms
/// of templates and handlers; "learn by watching" applies the same rules to
/// what was observed in the live DOM, so a learned signature has the same
/// shape as an analyzed one: same type vocabulary, list + listitem pairs for
/// every repeated container, read targets for dynamic values, and logic
/// notes on cardinality / lifecycle / relationships.
/// </summary>
internal static class SignatureRules
{
    public const string TypeVocabulary = """
        ── ELEMENT TYPES (closed vocabularies — use these tokens only) ──
        category "interactive" — something the user ACTS ON:
          input, textarea, select, combobox, multiselect, checkbox, radio, switch, toggle,
          button, link, menuitem, tab, accordion, slider, datepicker, timepicker, colorpicker,
          file-input, dropzone, search, chip, card, listitem, treeitem, row, cell, icon-button
        category "informational" — something a script READS, asserts, sorts or filters by:
          text, label, value, status, count, heading, badge, image-alt, timestamp,
          error-text, tooltip,
          list      — a repeating container (list, table body, grid, card deck…)
          listitem  — a per-item read target inside a list (the item's name, status…)
        """;

    public const string InformationalRules = """
        ── READ TARGETS (informational) ──
        Scripts must READ state, not only press buttons ("find the task named X and
        delete it", "is task 2 done?", "what priority does it have?"). Emit an
        informational element for each read target that
          1. carries user-visible text, or an attribute / state that means something
             (data-*, aria-*, checked, value), AND
          2. a script would plausibly assert on, sort by, filter by, or use to PICK
             one item out of many, AND
          3. is dynamic (it changed, or differs between items) — not fixed chrome.
        Skip decorative wrappers, static instructions and button labels themselves.
        When a value comes from a finite set (low | medium | high, true | false),
        list the observed values in `logic`.
        """;

    public const string ListRules = """
        ── REPEATING CONTAINERS — MANDATORY list + listitem PAIRS ──
        For every place that renders N items of the same kind:
          (a) ONE "list" element for the item collection itself, using the selector
              that matches EVERY item (the observed collection `item` selector).
              logic: what each item represents, how many there are, order of items,
              when items are added/removed, any empty state.
          (b) ONE "listitem" element per kind of read target inside an item — its
              title, its status / priority, its done flag — with a selector that
              matches that target in EVERY item (collection item + the observed
              path inside it, e.g. "<item> <path>"). logic: which list it belongs
              to, that `.nth(i)` follows the list's item order (0-based), what the
              value means, and its observed values.
          (c) ONE representative element per per-item CONTROL (each item's edit /
              delete / checkbox), again matching it in every item, with logic
              saying it is per-item and how to address item i.
        A selector that matches only the one item the user happened to touch
        (contains that item's text, or a position like :nth-of-type(2) on the
        item) is WRONG for any of these.
        """;

    public const string LogicRules = """
        ── LOGIC NOTES (elementLogic) ──
        description says WHAT the element is. logic says what a script author would
        otherwise have to discover by trial — write it when any of these is non-obvious:
          1. CARDINALITY — fixed, one per item of a list, N+1 of something?
          2. INDEX SEMANTICS — what `.nth(i)` means for this element.
          3. LIFECYCLE — when it exists: always, only in a mode (editing), only while a
             popup is open, created/destroyed by an action, gone after a timeout.
             For conditional elements say (a) the condition, (b) what occupies the
             slot otherwise (e.g. "the item's edit form REPLACES its display view
             until Save / Cancel"), (c) siblings gated by the same condition.
          4. RELATIONSHIPS — enables / disables / replaces / is mutually exclusive
             with another element.
          5. ORDER — sort rule of a list, where new items are inserted.
          6. VALUE vs DISPLAY — when a value is shown transformed (CSS text-transform
             capitalize/uppercase), state the underlying value and the display.
        1–4 plain sentences, concrete ("one per task", "0-based, list order"). Empty
        for self-explanatory elements. Don't pad.
        """;
}
