using System.Text.Json;
using System.Text.Json.Nodes;
using Mazda.Application.Modules.RoundTrip;
using Xunit;

namespace Mazda.Tests;

/// <summary>
/// The round trip's definition of "the replay did what the user did".
/// The central rule: a value's role comes from how it varies, never from what it looks like.
/// </summary>
public class RoundTripComparisonTests
{
    // ── helpers ──────────────────────────────────────────────────────

    private static JsonObject Net(string method, string url, object body)
        => new() { ["method"] = method, ["url"] = url, ["reqBody"] = JsonSerializer.Serialize(body) };

    private static StepDelta Delta(params JsonObject[] network) => DeltaBuilder.Build(Array.Empty<JsonObject>(), network);

    private static StepObservation Ok(StepDelta d) => new(d, Ran: true, Threw: false, Error: null);

    private static StepJudgement Judge(StepDelta demo, StepDelta replay, string args = "{}",
        StepDelta? twin = null, LearnedRoles? roles = null, IReadOnlyList<StepDelta>? reps = null,
        ISet<string>? accepted = null, ISet<string>? rejected = null)
        => StepComparer.Judge(new StepContext("Fn", args, demo, Ok(replay), twin is null ? null : Ok(twin),
            reps ?? Array.Empty<StepDelta>(), (accepted ?? new HashSet<string>()).ToHashSet(), (rejected ?? new HashSet<string>()).ToHashSet(),
            roles ?? LearnedRoles.None));

    private static JsonObject Chart(string type, string board = "b1")
        => Net("POST", "http://app.test/api/chart", new { boardId = board, chartType = type, settings = "{\"legend\":true}" });

    // ── the wrong chart type ─────────────────────────────────────────

    [Fact]
    public void A_value_that_changes_with_the_choice_is_intent_and_must_match()
    {
        var a = Delta(Chart("BarChart"));
        var contrast = Delta(Chart("LineChart"));
        var roles = LearnedRoles.Learn(a, twin: a, contrast: contrast);
        Assert.Contains("field:POST /api/chart:chartType", roles.Intent);

        var j = Judge(demo: Delta(Chart("BarChart")), replay: Delta(Chart("StackedBarChart")), roles: roles);
        Assert.Equal("different", j.Verdict);
        Assert.Contains(j.Blocking, f => f.Summary.Contains("BarChart") && f.Summary.Contains("StackedBarChart"));
    }

    [Fact]
    public void Without_contrast_a_differing_value_is_unexplained_not_a_guess()
    {
        var j = Judge(demo: Delta(Chart("BarChart")), replay: Delta(Chart("StackedBarChart")));
        Assert.Equal("unexplained", j.Verdict);
    }

    [Fact]
    public void A_value_that_equals_the_chosen_argument_is_intent()
    {
        var demo = Delta(Net("POST", "http://app.test/api/column", new { name = "Gender" }));
        var replay = Delta(Net("POST", "http://app.test/api/column", new { name = "Title" }));
        Assert.Equal("different", Judge(demo, replay, args: "{\"columnName\":\"Gender\"}").Verdict);
    }

    // ── noise ────────────────────────────────────────────────────────

    [Fact]
    public void A_value_that_differs_between_two_identical_replays_is_noise_and_ignored()
    {
        var a = Delta(Chart("BarChart", board: "x1"));
        var b = Delta(Chart("BarChart", board: "x2"));
        var roles = LearnedRoles.Learn(a, b, contrast: null);
        Assert.Contains("field:POST /api/chart:boardId", roles.Noise);
        Assert.Equal("identical", Judge(demo: Delta(Chart("BarChart", board: "demo")), replay: a, roles: roles).Verdict);
    }

    [Fact]
    public void Json_inside_a_string_is_compared_field_by_field()
    {
        var (fields, complete) = DeltaBuilder.Flatten("{\"settings\":\"{\\\"legend\\\":true,\\\"axis\\\":{\\\"x\\\":1}}\"}");
        Assert.True(complete);
        Assert.Equal("true", fields["settings.legend"]);
        Assert.Equal("1", fields["settings.axis.x"]);
    }

    // ── the real Morva recording: two drags into "Value" ────────────

    private static (JsonObject First, JsonObject Second) MorvaRequests()
    {
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "morva-add-value-column.json")))!;
        var reqs = doc["addValueColumn"]!.AsArray().Select(n => (JsonObject)n!.DeepClone()).ToList();
        return (reqs[0], reqs[1]);
    }

    private static JsonObject WithField(JsonObject req, string field, JsonNode value)
    {
        var body = JsonNode.Parse(req["reqBody"]!.GetValue<string>())!.AsObject();
        body[field] = value;
        var copy = (JsonObject)req.DeepClone();
        copy["reqBody"] = body.ToJsonString();
        return copy;
    }

    [Fact]
    public void Morva_columnId_looks_like_an_id_but_carries_the_choice()
    {
        var (birthDate, title) = MorvaRequests();
        // Contrast run: same step, another column. columnId changes with the choice; nothing else does.
        var roles = LearnedRoles.Learn(Delta(birthDate), twin: Delta(birthDate), contrast: Delta(title));
        Assert.Contains("field:POST /api/v1/Chart/AddValueColumn:columnId", roles.Intent);
        Assert.DoesNotContain("field:POST /api/v1/Chart/AddValueColumn:columnId", roles.Noise);

        // Replay dragged the wrong column: blocking, though columnId is a uuid.
        var j = Judge(demo: Delta(birthDate), replay: Delta(WithField(birthDate, "columnId", title["reqBody"] is null ? "x" :
            JsonNode.Parse(title["reqBody"]!.GetValue<string>())!["columnId"]!.GetValue<string>())), roles: roles);
        Assert.Equal("different", j.Verdict);
    }

    [Fact]
    public void Morva_priority_depends_on_page_state_and_is_never_blocking_on_its_own()
    {
        var (birthDate, title) = MorvaRequests();
        // The page already had a column: priority 2 instead of 1. The user's own two drags show priority varies (1 vs 4).
        var j = Judge(demo: Delta(birthDate), replay: Delta(WithField(birthDate, "priority", 2)), reps: new[] { Delta(title) });
        Assert.Equal("unexplained", j.Verdict);
        Assert.Contains(j.Findings, f => f.Kind == "varies-across-calls");
        Assert.Empty(j.Blocking);
    }

    // ── answers, missing effects ─────────────────────────────────────

    [Fact]
    public void The_authors_answers_are_remembered()
    {
        var demo = Delta(Chart("BarChart"));
        var replay = Delta(Chart("StackedBarChart"));
        var key = "field:POST /api/chart:chartType";
        Assert.Equal("equivalent", Judge(demo, replay, accepted: new HashSet<string> { key }).Verdict);
        Assert.Equal("different", Judge(demo, replay, rejected: new HashSet<string> { key }).Verdict);
    }

    [Fact]
    public void Missing_primary_request_is_different_but_a_missing_follow_up_is_only_a_note()
    {
        var demo = Delta(Net("POST", "http://app.test/api/save", new { item = "b" }), Net("POST", "http://app.test/api/autosave", new { auto = true }));
        Assert.Equal("different", Judge(demo, StepDelta.Empty with { Dom = new[] { "~saved" } }).Verdict);

        var j = Judge(demo, Delta(Net("POST", "http://app.test/api/save", new { item = "b" })));
        Assert.Equal("equivalent", j.Verdict);
        Assert.Contains(j.Findings, f => f.Kind == "request-sometimes" && f.Severity == "note");
    }

    [Fact]
    public void Class_only_churn_is_not_a_page_change_and_the_chosen_value_must_show_up()
    {
        var churn = new JsonObject
        {
            ["changed"] = new JsonArray(new JsonObject { ["attr"] = "class", ["added"] = new JsonArray("ng-star-inserted", "ng-animating") }),
        };
        Assert.Empty(DeltaBuilder.Build(new[] { churn }, Array.Empty<JsonObject>()).Dom);

        var echo = new JsonObject { ["text"] = new JsonArray(new JsonObject { ["from"] = "", ["to"] = "Birth_Date" }) };
        var demo = DeltaBuilder.Build(new[] { echo }, Array.Empty<JsonObject>());
        var other = DeltaBuilder.Build(new[] { new JsonObject { ["text"] = new JsonArray(new JsonObject { ["from"] = "", ["to"] = "Title" }) } }, Array.Empty<JsonObject>());
        var j = Judge(demo, other, args: "{\"columnName\":\"Birth_Date\"}");
        Assert.Equal("different", j.Verdict);
        Assert.Contains(j.Blocking, f => f.Kind == "echo-missing");
    }
}
