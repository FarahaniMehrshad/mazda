using System.Text.Json;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Runner;

/// <summary>
/// Runs a signature's (and its dependencies') state functions once against
/// the connected browser and returns the merged snapshot.
///
/// Uses <see cref="IScriptRunner.RunOnceAsync"/> because state gather is a
/// short synchronous phase whose output is parsed programmatically (from
/// the <c>__MZ_STATE_BEGIN__</c> / <c>__MZ_STATE_END__</c> fence the
/// <see cref="FunctionHarnessBuilder"/> writes) rather than tailed for
/// human display. Doing this on <c>RunAsync</c> would leak per-line log
/// noise into the wizard's SSE stream that the user has no business seeing.
/// </summary>
public sealed class StateGatherer(IScriptRunner runner) : IStateGatherer
{
    public async Task<GatheredState> GatherAsync(
        Signature signature,
        IReadOnlyList<Signature>? dependencies = null,
        CancellationToken ct = default)
    {
        // Fast path: no state functions anywhere → empty snapshot. Nothing
        // to spawn Playwright for; skip the whole runner round-trip.
        var hasAnyStateFn =
            signature.Functions.Any(f => f.Kind == FunctionKind.State)
            || (dependencies?.Any(d => d.Functions.Any(f => f.Kind == FunctionKind.State)) ?? false);

        if (!hasAnyStateFn)
        {
            return new GatheredState(
                Manifest: ManifestLabel(signature),
                CapturedAt: DateTime.UtcNow,
                StateJson: "{}");
        }

        var harness = FunctionHarnessBuilder.BuildGatherHarness(signature, dependencies);

        RunnerCompletion completion;
        try
        {
            completion = await runner.RunOnceAsync(harness, ct);
        }
        catch (Exception ex)
        {
            throw new HarnessException(
                "State gatherer could not spawn Playwright. Is the CDP browser " +
                "listening on the expected port?", ex);
        }

        // A harness-level failure (couldn't connect, node_modules missing,
        // etc.) still lets Playwright emit stdout — but the sentinel pair
        // may not appear at all. Distinguish that from "ran fine, printed
        // JSON" up-front so the caller sees the real reason for the miss.
        var stateJson = TryExtractSentinelPayload(completion.FullLog);
        if (stateJson is null)
        {
            if (completion.Kind is RunnerExitKind.Errored or RunnerExitKind.Cancelled)
            {
                throw new HarnessException(
                    $"State gatherer terminated with kind={completion.Kind} " +
                    $"(exit code {completion.ExitCode}) before emitting a state payload. " +
                    $"Last 500 chars of runner log:\n{Tail(completion.FullLog, 500)}");
            }
            // Playwright ran to a "passed" or "failed test" exit code but our
            // sentinels are absent — usually means a body threw during the
            // gather loop before the print statements. Surface the tail so
            // the wizard can display something useful.
            throw new HarnessException(
                "State gatherer completed but did not emit a state payload. " +
                "This usually means a state-function body threw before the " +
                "harness could reach the emit step. Last 500 chars of runner log:\n" +
                Tail(completion.FullLog, 500));
        }

        // Validate the payload is JSON-well-formed before we hand it on —
        // downstream (LlmPlanGenerator) inlines it into a prompt and a
        // malformed payload would poison the LLM call.
        try
        {
            using var _ = JsonDocument.Parse(stateJson);
        }
        catch (JsonException ex)
        {
            throw new HarnessException(
                $"State gatherer emitted a payload that isn't valid JSON: {ex.Message}. " +
                $"Payload was: {stateJson[..Math.Min(200, stateJson.Length)]}");
        }

        return new GatheredState(
            Manifest: ManifestLabel(signature),
            CapturedAt: DateTime.UtcNow,
            StateJson: stateJson);
    }

    /// <summary>
    /// Grep the runner's captured stdout for the sentinel pair the harness
    /// writes around the state JSON. Both sentinels must appear on their own
    /// lines. Everything between them (concatenated) is the payload.
    ///
    /// We match on line boundaries because Playwright's list reporter may
    /// print other content in between (it usually doesn't between two
    /// console.log calls on the same test, but robustness costs nothing).
    /// </summary>
    private static string? TryExtractSentinelPayload(string fullLog)
    {
        var lines = fullLog.Split('\n');
        int beginIdx = -1, endIdx = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (beginIdx < 0 && line == FunctionHarnessBuilder.StateBeginSentinel) beginIdx = i;
            else if (beginIdx >= 0 && line == FunctionHarnessBuilder.StateEndSentinel) { endIdx = i; break; }
        }
        if (beginIdx < 0 || endIdx <= beginIdx) return null;
        // Rejoin the interior with no separator — the harness prints the
        // JSON in a single console.log so there's no ambiguity.
        var interior = string.Concat(lines.Skip(beginIdx + 1).Take(endIdx - beginIdx - 1)
            .Select(l => l.TrimEnd('\r')));
        return interior.Trim();
    }

    private static string ManifestLabel(Signature s)
        => string.IsNullOrWhiteSpace(s.Version)
            ? s.Name
            : $"{s.Name}@{s.Version}";

    private static string Tail(string s, int chars)
        => s.Length <= chars ? s : s[^chars..];
}
