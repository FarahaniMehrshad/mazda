using System.Text.Json;
using System.Text.Json.Nodes;
using Mazda.Domain.Modules.Recordings;

namespace Mazda.Application.Modules.Recordings;

public sealed record EpisodeBuildResult(string Json, int EpisodeCount, int ActionCount, bool Trimmed);

/// <summary>
/// Deterministically condenses a raw recorder event log into action → effect
/// "episodes" — the compact, LLM-sized view of a recording.
///
/// Raw log: one <c>action</c> event per user interaction, plus <c>mutations</c>,
/// <c>network</c> and <c>nav</c> events tagged with the action id they were
/// attributed to (page-side for mutations, by time window for network/nav).
///
/// Output document:
/// <code>
/// { startUrl, origins, targets: [{best, role, name, hits, candidates}],
///   episodes: [{ i, t, action, value?, key?, target, dest?, urlBefore, urlAfter?,
///                effects: {appeared, disappeared, changed, text}, network, settleMs, count? }],
///   background: [{method, path, count}], warnings, ambientMutations }
/// </code>
/// Pure — no I/O — so it can be unit tested and re-run over stored events.
/// </summary>
public static class EpisodeBuilder
{
    public const int DefaultBudgetChars = 60_000;

    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class Ep
    {
        public string Id = "";
        public long T;
        public JsonObject? Action;
        public string? UrlBefore;
        public string? NavUrl;
        public readonly List<JsonObject> Mutations = new();
        public readonly List<JsonObject> Network = new();
    }

    public static EpisodeBuildResult Build(IEnumerable<RecordingEvent> events, int budgetChars = DefaultBudgetChars)
    {
        string? startUrl = null;
        var origins = new SortedSet<string>(StringComparer.Ordinal);
        var byId = new Dictionary<string, Ep>(StringComparer.Ordinal);
        var order = new List<Ep>();
        var background = new Dictionary<string, int>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var ambient = 0;
        var actionCount = 0;

        Ep Get(string id, long t)
        {
            if (!byId.TryGetValue(id, out var ep))
            {
                ep = new Ep { Id = id, T = t };
                byId[id] = ep;
                order.Add(ep);
            }
            return ep;
        }

        foreach (var e in events.OrderBy(x => x.Seq))
        {
            JsonObject? root;
            try { root = JsonNode.Parse(e.PayloadJson) as JsonObject; }
            catch (JsonException) { continue; }
            if (root is null) continue;
            var data = root["data"] as JsonObject;
            var url = Str(root["url"]);
            if (UrlTemplater.Origin(url) is { } o) origins.Add(o);

            switch (e.Kind)
            {
                case "ready":
                    startUrl ??= Str(data?["url"]);
                    if (UrlTemplater.Origin(startUrl) is { } so) origins.Add(so);
                    break;

                case "action":
                {
                    actionCount++;
                    var ep = Get(e.ActionId ?? $"seq{e.Seq}", e.TMs);
                    ep.Action = data;
                    ep.UrlBefore ??= url;
                    ep.T = Math.Min(ep.T, e.TMs);
                    break;
                }

                case "mutations":
                    if (e.ActionId is null || data is null) break;
                    Get(e.ActionId, e.TMs).Mutations.Add(data);
                    break;

                case "network":
                    if (data is null) break;
                    if (e.ActionId is null)
                    {
                        var key = $"{Str(data["method"]) ?? "GET"} {UrlTemplater.Template(Str(data["url"]))}";
                        background[key] = background.GetValueOrDefault(key) + 1;
                    }
                    else
                    {
                        // A request reported "no response yet" and later completed: keep only the final report.
                        var list = Get(e.ActionId, e.TMs).Network;
                        var rid = Str(data["rid"]);
                        var prior = rid is null ? -1 : list.FindIndex(x => Str(x["rid"]) == rid);
                        if (prior >= 0) list[prior] = data; else list.Add(data);
                    }
                    break;

                case "nav":
                {
                    var navUrl = Str(data?["url"]);
                    if (navUrl is null) break; // off-allowlist navigation: recorded as paused, no path
                    if (e.ActionId is not null)
                        Get(e.ActionId, e.TMs).NavUrl = navUrl;
                    else
                    {
                        // Navigation nobody clicked for (redirect, address bar, back
                        // button) — keep it as its own episode so the flow reads right.
                        var ep = Get($"nav{e.Seq}", e.TMs);
                        ep.Action = new JsonObject { ["type"] = "navigate" };
                        ep.NavUrl = navUrl;
                        ep.UrlBefore = url;
                    }
                    break;
                }

                case "ambient":
                    ambient += Int(data?["count"]) ?? 0;
                    break;

                case "warn":
                case "limit":
                {
                    var msg = Str(data?["msg"]) ?? Str(data?["reason"]) ?? e.Kind;
                    if (warnings.Count < 20 && !warnings.Contains(msg)) warnings.Add(msg);
                    break;
                }
            }
        }

        // ── Shape episodes ─────────────────────────────────────────
        var shaped = new List<JsonObject>();
        // Last known outline of each collection item ("#list > li" #1), so a
        // re-render (rows removed + re-added) can be turned back into what
        // actually changed inside the row.
        var lastOutlines = new Dictionary<string, JsonArray>(StringComparer.Ordinal);
        var outlinedCollections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ep in order.OrderBy(x => x.T))
        {
            if (ep.Action is null) continue; // effects with no surviving action event
            var type = Str(ep.Action["type"]) ?? "unknown";

            // The acted-on row's outline is its state BEFORE the action.
            if (ep.Action["target"] is JsonObject tgt && tgt["collection"] is JsonObject tcol
                && tcol["itemOutline"] is JsonArray tio && ItemKey(tcol) is { } tkey)
                lastOutlines[tkey] = (JsonArray)tio.DeepClone();

            var effects = MergeEffects(ep.Mutations);
            DiffCollectionItems(effects, lastOutlines);
            var network = new JsonArray(ep.Network.Select(ShapeNetwork).ToArray<JsonNode?>());
            var hasEffects = EffectCount(effects) > 0 || network.Count > 0 || ep.NavUrl is not null;

            // Scrolls only matter when they caused something (lazy-load,
            // virtualised list rows mounting).
            if (type == "scroll" && !hasEffects) continue;

            var o = new JsonObject
            {
                ["t"] = ep.T,
                ["action"] = type,
                ["_aid"] = ep.Id,   // internal: hover ↔ unhover linking; stripped below
            };
            CopyIf(ep.Action, o, "hoverOf");
            CopyIf(ep.Action, o, "value");
            CopyIf(ep.Action, o, "label");
            CopyIf(ep.Action, o, "key");
            CopyIf(ep.Action, o, "masked");
            if (ep.Action["target"] is JsonObject target)
            {
                var shapedTarget = ShapeTarget(target);
                // A collection's inner structure only needs saying once per document.
                if (shapedTarget["collection"] is JsonObject sc && Str(sc["item"]) is { } itemSel
                    && !outlinedCollections.Add(itemSel))
                    sc.Remove("itemOutline");
                o["target"] = shapedTarget;
            }
            if (ep.Action["dest"] is JsonObject dest) o["dest"] = ShapeTarget(dest);
            if (ep.Action["scroll"] is JsonObject scroll) o["scroll"] = scroll.DeepClone();
            if (ep.UrlBefore is not null) o["urlBefore"] = UrlTemplater.Template(ep.UrlBefore);
            if (ep.NavUrl is not null)
            {
                var after = UrlTemplater.Template(ep.NavUrl);
                if (after != Str(o["urlBefore"])) o["urlAfter"] = after;
            }
            if (EffectCount(effects) > 0) o["effects"] = effects;
            if (network.Count > 0) o["network"] = network;
            var settle = ep.Mutations.Select(m => Int(m["settleMs"]) ?? 0).DefaultIfEmpty(0).Max();
            if (settle > 0) o["settleMs"] = settle;
            shaped.Add(o);
        }

        // Consecutive fills into the same field are one logical "type into X",
        // and a click that only focused the field it was then typed into adds
        // nothing a fill doesn't already imply.
        var merged = new List<JsonObject>();
        foreach (var ep in shaped)
        {
            var prev = merged.LastOrDefault();
            // Leaving a host that a recent hover changed: the hover's effects
            // were transient. Say so on the hover instead of a separate episode
            // (an unhover with no matching hover is kept — something vanished).
            if (Str(ep["action"]) == "unhover")
            {
                var of = Str(ep["hoverOf"]);
                var leftHost = BestOf(ep);
                var hover = merged.TakeLast(4).LastOrDefault(m => Str(m["action"]) == "hover"
                    && (of is not null ? Str(m["_aid"]) == of : leftHost is not null && BestOf(m) == leftHost));
                if (hover is not null) { hover["revertsOnLeave"] = true; continue; }
                ep.Remove("hoverOf");
            }
            // Clicking a field / native <select> right before filling / choosing
            // in it is part of that same interaction.
            if (prev is not null && Str(prev["action"]) == "click" && Str(ep["action"]) is "fill" or "select"
                && BestOf(prev) == BestOf(ep) && BestOf(ep) is not null
                && prev["effects"] is null && prev["network"] is null && prev["urlAfter"] is null)
            {
                merged[^1] = ep;
                continue;
            }
            if (prev is not null && Str(prev["action"]) == "fill" && Str(ep["action"]) == "fill"
                && BestOf(prev) == BestOf(ep) && BestOf(ep) is not null)
            {
                ep["effects"] = UnionEffects(prev["effects"] as JsonObject, ep["effects"] as JsonObject);
                if (ep["effects"] is JsonObject eo && EffectCount(eo) == 0) ep.Remove("effects");
                // Type-ahead search fires a request per pause: the earlier
                // fill's requests and navigation belong to the merged fill.
                if (prev["network"] is JsonArray pn && pn.Count > 0)
                {
                    var net = new JsonArray();
                    var rids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var src in new[] { pn, ep["network"] as JsonArray })
                        foreach (var n in src ?? new JsonArray())
                            if (n is JsonObject no && (Str(no["rid"]) is not { } rid || rids.Add(rid)))
                                net.Add(no.DeepClone());
                    ep["network"] = net;
                }
                if (ep["urlAfter"] is null && prev["urlAfter"] is { } pua) ep["urlAfter"] = pua.DeepClone();
                if (prev["urlBefore"] is { } pub) ep["urlBefore"] = pub.DeepClone();
                if ((Int(prev["settleMs"]) ?? 0) > (Int(ep["settleMs"]) ?? 0)) ep["settleMs"] = Int(prev["settleMs"]);
                merged[^1] = ep;
                continue;
            }
            merged.Add(ep);
        }

        // Collapse identical back-to-back episodes ("clicked Next 5 times").
        var collapsed = new List<JsonObject>();
        string? prevShape = null;
        foreach (var ep in merged)
        {
            var shape = ShapeKey(ep);
            if (shape == prevShape && collapsed.Count > 0)
            {
                var last = collapsed[^1];
                last["count"] = (Int(last["count"]) ?? 1) + 1;
                continue;
            }
            collapsed.Add(ep);
            prevShape = shape;
        }
        for (var i = 0; i < collapsed.Count; i++)
        {
            // Put "i" first for readability.
            var ep = collapsed[i];
            var copy = new JsonObject { ["i"] = i };
            ep.Remove("_aid");
            foreach (var kv in ep.ToList()) { ep.Remove(kv.Key); copy[kv.Key] = kv.Value; }
            collapsed[i] = copy;
        }

        // Distinct interacted targets — the analyzer's shortlist of controls.
        var targets = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var ep in collapsed)
        {
            foreach (var key in new[] { "target", "dest" })
            {
                if (ep[key] is not JsonObject t || Str(t["best"]) is not { } best) continue;
                if (targets.TryGetValue(best, out var existing))
                {
                    existing["hits"] = (Int(existing["hits"]) ?? 1) + (Int(ep["count"]) ?? 1);
                    continue;
                }
                var entry = new JsonObject { ["best"] = best, ["hits"] = Int(ep["count"]) ?? 1 };
                CopyIf(t, entry, "role");
                CopyIf(t, entry, "name");
                CopyIf(t, entry, "tag");
                if (t["candidates"] is JsonArray c) entry["candidates"] = c.DeepClone();
                if (t["collection"] is JsonObject tc)
                {
                    var col = (JsonObject)tc.DeepClone();
                    col.Remove("itemOutline");
                    col.Remove("index");
                    entry["collection"] = col;
                }
                targets[best] = entry;
            }
        }

        var doc = new JsonObject
        {
            ["startUrl"] = startUrl is null ? null : UrlTemplater.Template(startUrl),
            ["origins"] = new JsonArray(origins.Select(x => (JsonNode?)x).ToArray()),
            ["targets"] = new JsonArray(targets.Values.Select(x => (JsonNode?)x).ToArray()),
            ["episodes"] = new JsonArray(collapsed.Select(x => (JsonNode?)x).ToArray()),
        };
        if (background.Count > 0)
            doc["background"] = new JsonArray(background
                .OrderByDescending(kv => kv.Value).Take(20)
                .Select(kv =>
                {
                    var sp = kv.Key.IndexOf(' ');
                    return (JsonNode?)new JsonObject { ["method"] = kv.Key[..sp], ["path"] = kv.Key[(sp + 1)..], ["count"] = kv.Value };
                }).ToArray());
        if (warnings.Count > 0) doc["warnings"] = new JsonArray(warnings.Select(w => (JsonNode?)w).ToArray());
        if (ambient > 0) doc["ambientMutations"] = ambient;

        var trimmed = FitBudget(doc, budgetChars);
        var json = doc.ToJsonString(Compact);
        return new EpisodeBuildResult(json, (doc["episodes"] as JsonArray)!.Count, actionCount, trimmed);
    }

    // ── Budget ─────────────────────────────────────────────────────

    /// <summary>
    /// Progressively drops detail until the document fits: bodies first (big,
    /// least structural), then long effect lists, then extra selector
    /// candidates, then episodes that show no effect, finally the tail.
    /// </summary>
    private static bool FitBudget(JsonObject doc, int budget)
    {
        int Size() => doc.ToJsonString(Compact).Length;
        if (Size() <= budget) return false;
        var episodes = (JsonArray)doc["episodes"]!;

        // 1. Network bodies.
        foreach (var ep in episodes.OfType<JsonObject>())
            foreach (var n in (ep["network"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            { n.Remove("req"); n.Remove("res"); }
        if (Size() <= budget) return true;

        // 2. Long effect lists and outlines.
        foreach (var ep in episodes.OfType<JsonObject>())
        {
            if (ep["effects"] is JsonObject fx)
            {
                foreach (var key in new[] { "appeared", "disappeared", "changed", "text", "itemChanges" })
                    if (fx[key] is JsonArray arr) TrimArray(arr, 8);
                foreach (var a in (fx["appeared"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                    if (a["outline"] is JsonArray ol) TrimArray(ol, 8);
            }
            if ((ep["target"] as JsonObject)?["collection"] is JsonObject tc && tc["itemOutline"] is JsonArray io) TrimArray(io, 8);
            if (ep["network"] is JsonArray net) TrimArray(net, 5);
        }
        if (Size() <= budget) return true;

        // 3. Selector candidates + ancestors.
        void ThinTarget(JsonNode? n)
        {
            if (n is not JsonObject t) return;
            if (t["candidates"] is JsonArray c) TrimArray(c, 3);
            t.Remove("ancestors");
        }
        foreach (var ep in episodes.OfType<JsonObject>())
        {
            ThinTarget(ep["target"]);
            ThinTarget(ep["dest"]);
            if (ep["effects"] is JsonObject fx && fx["appeared"] is JsonArray ap)
                foreach (var a in ap) ThinTarget(a);
        }
        foreach (var t in (doc["targets"] as JsonArray)!) ThinTarget(t);
        if (Size() <= budget) return true;

        // 4. Episodes with no observable effect (except value-carrying ones).
        for (var i = episodes.Count - 1; i >= 0; i--)
        {
            if (episodes[i] is not JsonObject ep) continue;
            var a = Str(ep["action"]);
            if (a is "fill" or "select" or "navigate") continue;
            if (ep["effects"] is null && ep["network"] is null && ep["urlAfter"] is null)
                episodes.RemoveAt(i);
        }
        if (Size() <= budget) return true;

        // 5. Drop the tail until it fits. Measure once and subtract each
        //    episode's own size (+1 for its separating comma) instead of
        //    re-serializing the whole document per removal.
        var size = Size();
        while (episodes.Count > 1 && size > budget)
        {
            var last = episodes[^1];
            size -= (last?.ToJsonString(Compact).Length ?? 4) + 1;
            episodes.RemoveAt(episodes.Count - 1);
        }
        doc["episodesTruncated"] = true;
        return true;
    }

    private static void TrimArray(JsonArray arr, int max)
    {
        while (arr.Count > max) arr.RemoveAt(arr.Count - 1);
    }

    // ── Shaping helpers ────────────────────────────────────────────

    /// <summary>Compact target: best selector + candidates + identity hints.</summary>
    internal static JsonObject ShapeTarget(JsonObject t)
    {
        var o = new JsonObject();
        var candidates = t["candidates"] as JsonArray;
        var best = BestCandidate(candidates);
        if (best is not null) o["best"] = best;
        CopyIf(t, o, "tag");
        CopyIf(t, o, "role");
        CopyIf(t, o, "name");
        CopyIf(t, o, "text");
        if (t["floating"] is JsonValue f && f.TryGetValue<bool>(out var fl) && fl) o["floating"] = true;
        if (t["inShadow"] is JsonValue s && s.TryGetValue<bool>(out var sh) && sh) o["inShadow"] = true;
        if (Int(t["childCount"]) is { } cc) o["childCount"] = cc;
        CopyIf(t, o, "textTransform");
        CopyIf(t, o, "data");
        CopyIf(t, o, "collection");
        CopyIf(t, o, "outline");
        CopyIf(t, o, "visible");
        // { on: "hover", via: js|class|css, host, hostCollection?, inner? }:
        // only visible while `host` is hovered — a script must hover it first.
        CopyIf(t, o, "revealedBy");
        if (candidates is not null)
            o["candidates"] = new JsonArray(candidates.OfType<JsonObject>().Take(6).Select(c =>
            {
                var co = new JsonObject { ["selector"] = Str(c["selector"]), ["kind"] = Str(c["kind"]) };
                if (Int(c["count"]) is { } n) co["count"] = n;
                return (JsonNode?)co;
            }).ToArray());
        if (t["ancestors"] is JsonArray anc && anc.Count > 0) o["ancestors"] = anc.DeepClone();
        return o;
    }

    /// <summary>
    /// Best selector among the recorder's candidates (which arrive in
    /// preference order: test-id, id, role, attribute, text, class, CSS path).
    /// The first SEMANTIC candidate that is unique — or unverifiable, i.e. a
    /// Playwright role/text selector the page could not count — wins; a
    /// positional CSS path only when nothing else is unique.
    /// </summary>
    internal static string? BestCandidate(JsonArray? candidates)
    {
        if (candidates is null) return null;
        var list = candidates.OfType<JsonObject>()
            .Select(c => (sel: Str(c["selector"]), kind: Str(c["kind"]), count: Int(c["count"])))
            .Where(x => x.sel is not null)
            .ToList();
        return list.FirstOrDefault(x => x.kind != "css" && (x.count == 1 || x.count is null)).sel
               ?? list.FirstOrDefault(x => x.count == 1).sel
               ?? list.FirstOrDefault().sel;
    }

    private static JsonObject ShapeNetwork(JsonObject n)
    {
        var o = new JsonObject
        {
            ["method"] = Str(n["method"]) ?? "GET",
            ["path"] = UrlTemplater.Template(Str(n["url"])),
        };
        if (Int(n["status"]) is { } st) o["status"] = st;
        if (Int(n["ms"]) is { } ms) o["ms"] = ms;
        if (Str(n["error"]) is { } err) o["error"] = err;
        if (n["pending"] is JsonValue pv && pv.TryGetValue<bool>(out var pend) && pend)
            o["pending"] = n["neverCompleted"] is JsonValue nc && nc.TryGetValue<bool>(out var never) && never
                ? "never completed during the recording"
                : "no response for 5s+";
        if (Str(n["reqBody"]) is { } req) o["req"] = req;
        if (Str(n["resBody"]) is { } res) o["res"] = res;
        return o;
    }

    private static JsonObject MergeEffects(List<JsonObject> batches)
    {
        var fx = new JsonObject
        {
            ["appeared"] = new JsonArray(),
            ["disappeared"] = new JsonArray(),
            ["changed"] = new JsonArray(),
            ["text"] = new JsonArray(),
        };
        foreach (var b in batches)
        {
            if (b["appeared"] is JsonArray ap)
                foreach (var a in ap.OfType<JsonObject>()) ((JsonArray)fx["appeared"]!).Add(ShapeTarget(a));
            foreach (var key in new[] { "disappeared", "changed", "text" })
                if (b[key] is JsonArray arr)
                    foreach (var item in arr) ((JsonArray)fx[key]!).Add(item?.DeepClone());
        }
        foreach (var key in new[] { "appeared", "disappeared", "changed", "text" })
            if (((JsonArray)fx[key]!).Count == 0) fx.Remove(key);
        return fx;
    }

    private static JsonObject UnionEffects(JsonObject? a, JsonObject? b)
    {
        var o = new JsonObject();
        foreach (var key in new[] { "appeared", "disappeared", "changed", "text", "itemChanges" })
        {
            var arr = new JsonArray();
            if (a?[key] is JsonArray x) foreach (var i in x) arr.Add(i?.DeepClone());
            if (b?[key] is JsonArray y) foreach (var i in y) arr.Add(i?.DeepClone());
            if (arr.Count > 0) o[key] = arr;
        }
        return o;
    }

    private static int EffectCount(JsonObject? fx)
        => fx is null ? 0 : new[] { "appeared", "disappeared", "changed", "text", "itemChanges" }
            .Sum(k => (fx[k] as JsonArray)?.Count ?? 0);

    private static string? ItemKey(JsonObject collection)
        => Str(collection["item"]) is { } item && Int(collection["index"]) is { } idx ? $"{item}#{idx}" : null;

    /// <summary>
    /// For every appeared element that is an item of a collection and carries
    /// an outline, diff it against that item's last known outline and record
    /// <c>itemChanges</c> ({item, index, sel, field, from, to}). An item whose
    /// outline did not change was merely re-rendered: its outline is dropped
    /// and it is marked <c>rerendered</c>.
    /// </summary>
    private static void DiffCollectionItems(JsonObject effects, Dictionary<string, JsonArray> last)
    {
        if (effects["appeared"] is not JsonArray appeared) return;
        var changes = new JsonArray();
        foreach (var a in appeared.OfType<JsonObject>())
        {
            if (a["collection"] is not JsonObject col || ItemKey(col) is not { } key) continue;
            if (a["outline"] is not JsonArray now) continue;
            if (last.TryGetValue(key, out var before))
            {
                var diffs = DiffOutline(before, now);
                if (diffs.Count == 0)
                {
                    a.Remove("outline");
                    a.Remove("candidates");
                    a["rerendered"] = true;
                }
                foreach (var d in diffs)
                {
                    d["item"] = Str(col["item"]);
                    d["index"] = Int(col["index"]);
                    if (changes.Count < 30) changes.Add(d);
                }
            }
            last[key] = (JsonArray)now.DeepClone();
        }
        if (changes.Count > 0) effects["itemChanges"] = changes;
    }

    private static List<JsonObject> DiffOutline(JsonArray before, JsonArray now)
    {
        static string K(JsonObject n) => Str(n["sel"]) ?? "";
        var old = before.OfType<JsonObject>().GroupBy(K).ToDictionary(g => g.Key, g => g.First());
        var cur = now.OfType<JsonObject>().GroupBy(K).ToDictionary(g => g.Key, g => g.First());
        var diffs = new List<JsonObject>();
        void Add(string sel, string field, JsonNode? from, JsonNode? to)
            => diffs.Add(new JsonObject { ["sel"] = sel, ["field"] = field, ["from"] = from?.DeepClone(), ["to"] = to?.DeepClone() });

        foreach (var (sel, n) in cur)
        {
            if (!old.TryGetValue(sel, out var o)) { Add(sel, "present", false, true); continue; }
            foreach (var f in new[] { "text", "checked", "value" })
                if ((o[f]?.ToJsonString() ?? "null") != (n[f]?.ToJsonString() ?? "null")) Add(sel, f, o[f], n[f]);
            var od = o["data"] as JsonObject; var nd = n["data"] as JsonObject;
            foreach (var attr in (od?.Select(x => x.Key) ?? Enumerable.Empty<string>())
                         .Union(nd?.Select(x => x.Key) ?? Enumerable.Empty<string>()))
            {
                var a = od?[attr]?.ToJsonString() ?? "null"; var b = nd?[attr]?.ToJsonString() ?? "null";
                if (a != b) Add(sel, attr, od?[attr], nd?[attr]);
            }
        }
        foreach (var sel in old.Keys.Where(k => !cur.ContainsKey(k)))
            Add(sel, "present", true, false);
        // A whole-mode switch (display ↔ edit form) shows up as many
        // present/absent lines; keep the list readable.
        return diffs.Take(12).ToList();
    }

    private static string? BestOf(JsonObject ep) => Str((ep["target"] as JsonObject)?["best"]);

    /// <summary>Equality key for collapsing repeats: action, target and the shape (not content) of effects.</summary>
    private static string ShapeKey(JsonObject ep)
    {
        // Hovering row after row of one list is one behaviour, not N: key
        // hovers by the collection, not the individual row.
        var who = Str(ep["action"]) is "hover" or "unhover"
            && (ep["target"] as JsonObject)?["collection"] is JsonObject hc && Str(hc["item"]) is { } hItem
            ? "col:" + hItem
            : BestOf(ep) ?? "";
        var parts = new List<string> { Str(ep["action"]) ?? "", who, Str(ep["value"]) ?? "", Str(ep["key"]) ?? "", ep["revertsOnLeave"] is null ? "" : "~leave" };
        if (ep["effects"] is JsonObject fx)
        {
            if (fx["appeared"] is JsonArray ap)
                parts.Add("+" + string.Join(",", ap.OfType<JsonObject>().Select(a => Str(a["role"]) ?? Str(a["tag"]))));
            if (fx["disappeared"] is JsonArray dis) parts.Add("-" + dis.Count);
            if (fx["changed"] is JsonArray ch)
                parts.Add("~" + string.Join(",", ch.OfType<JsonObject>().Select(c => Str(c["attr"]))));
        }
        if ((ep["effects"] as JsonObject)?["itemChanges"] is JsonArray ic)
            parts.Add("Δ" + string.Join(",", ic.OfType<JsonObject>().Select(c => $"{Str(c["sel"])}.{Str(c["field"])}")));
        if (ep["network"] is JsonArray net)
            parts.Add(string.Join(",", net.OfType<JsonObject>().Select(n => $"{Str(n["method"])} {Str(n["path"])}")));
        parts.Add(Str(ep["urlAfter"]) ?? "");
        return string.Join("|", parts);
    }

    private static void CopyIf(JsonObject from, JsonObject to, string key)
    {
        if (from[key] is { } v) to[key] = v.DeepClone();
    }

    public static string? Str(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static int? Int(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)l;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return null;
    }
}
