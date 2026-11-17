using System.Text.Json;
using System.Text.Json.Nodes;
using Mazda.Application.Common.Abstractions;
using Mazda.Application.Modules.RoundTrip;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Runner;

/// <summary>
/// Runs a round-trip harness (<see cref="FunctionHarnessBuilder.BuildRoundTripHarness"/>)
/// and parses what it observed. Progress lines stream to the round trip's
/// live feed; the full output stays out of the registry.
/// </summary>
public sealed class RoundTripReplayer(IScriptRunner runner, IExecutionRegistry registry) : IRoundTripReplayer
{
    private const string Config = """
        import { defineConfig } from '@playwright/test';
        export default defineConfig({
          testDir: '.',
          timeout: 900_000,
          reporter: [['list'], ['./mz-trace-reporter.mjs']],
          use: { actionTimeout: 10_000, navigationTimeout: 20_000, trace: 'off', screenshot: 'off', video: 'off' },
        });
        """;

    private const string ProgressPrefix = "__MZ_RT__ ";

    public async Task<ReplayRun> ReplayAsync(
        Guid feedId,
        Signature signature,
        IReadOnlyList<Signature>? dependencies,
        IReadOnlyList<RoundTripCall> calls,
        CdpTarget target,
        string? startUrl,
        CancellationToken ct = default)
    {
        var harness = FunctionHarnessBuilder.BuildRoundTripHarness(
            signature, dependencies, calls, target, startUrl, RecorderScriptBuilder.PageSource);

        RunnerCompletion completion;
        try
        {
            completion = await runner.RunAsync(
                Guid.NewGuid(), harness,
                new ScriptRunOptions(
                    OnLine: line =>
                    {
                        if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
                            registry.Append(feedId, "  " + line[ProgressPrefix.Length..]);
                    },
                    StreamToRegistry: false,
                    PlaywrightConfigOverride: Config),
                ct);
        }
        catch (Exception ex)
        {
            throw new HarnessException(
                "Could not spawn Playwright for the replay. Is the CDP browser listening on the expected port?", ex);
        }

        var logTail = HarnessOutput.Tail(completion.FullLog, 3000);
        if (string.IsNullOrEmpty(completion.RoundTripJson))
            throw new HarnessException(
                $"The replay ended (kind={completion.Kind}, exit code {completion.ExitCode}) without reporting what it observed. " +
                "This usually means the browser tab was unreachable or a function body does not compile. " +
                $"Last lines of the runner log:\n{logTail}");

        try
        {
            var root = JsonNode.Parse(completion.RoundTripJson) as JsonObject;
            var steps = new List<ReplayedStep>();
            foreach (var s in (root?["steps"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var err = s["error"] as JsonObject;
                steps.Add(new ReplayedStep(
                    Call: Str(s["call"]) ?? "",
                    Ran: Bool(s["ran"]),
                    Threw: Bool(s["ran"]) && !Bool(s["ok"]),
                    Error: Str(err?["message"]),
                    ErrorStack: Str(err?["stack"]),
                    DurationMs: s["durationMs"] is JsonValue dv && dv.TryGetValue<int>(out var ms) ? ms : 0,
                    Mutations: Objects(s["mutations"]),
                    Network: Objects(s["network"]),
                    Console: (s["console"] as JsonArray)?.Select(x => x?.ToString() ?? "").ToList() ?? new List<string>(),
                    UrlAfter: Str(s["urlAfter"]),
                    Aria: Str(s["aria"]),
                    BeforeJpeg: Str(s["before"]),
                    AfterJpeg: Str(s["after"]))
                {
                    BeforeBusy = Str(s["beforeBusy"]),
                    AfterBusy = Str(s["afterBusy"]),
                });
            }
            return new ReplayRun(steps, logTail);
        }
        catch (JsonException ex)
        {
            throw new HarnessException($"The replay wrote observations that could not be read: {ex.Message}");
        }
    }

    private static List<JsonObject> Objects(JsonNode? n)
        => (n as JsonArray)?.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList() ?? new List<JsonObject>();
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
