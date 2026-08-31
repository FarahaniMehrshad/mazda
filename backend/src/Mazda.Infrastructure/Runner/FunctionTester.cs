using System.Text.Json;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Runner;

/// <summary>
/// Runs ONE catalogue function against the connected browser and reports what
/// it did. The design-time counterpart to <see cref="StateGatherer"/>: same
/// shape (build harness → <see cref="IScriptRunner.RunOnceAsync"/> → parse the
/// fenced payload), different harness and a different failure contract.
///
/// The contract that matters: a body which throws comes back as a normal
/// outcome with <c>Ok == false</c>, because seeing that message is the entire
/// point of the feature. <see cref="HarnessException"/> is reserved for the
/// browser being unreachable or the spec failing to run at all.
/// </summary>
public sealed class FunctionTester(IScriptRunner runner) : IFunctionTester
{
    private const int LogTailChars = 2000;

    public async Task<FunctionTestOutcome> TestAsync(
        Signature signature,
        IReadOnlyList<Signature>? dependencies,
        string qualifiedFunctionName,
        string argsJson,
        CdpTarget target,
        CancellationToken ct = default)
    {
        var harness = FunctionHarnessBuilder.BuildTestHarness(
            signature, dependencies, qualifiedFunctionName, argsJson, target);

        RunnerCompletion completion;
        try
        {
            completion = await runner.RunOnceAsync(harness, ct);
        }
        catch (Exception ex)
        {
            throw new HarnessException(
                "Could not spawn Playwright to test this function. Is the CDP " +
                "browser listening on the expected port?", ex);
        }

        var logTail = HarnessOutput.Tail(completion.FullLog, LogTailChars);

        var payload = HarnessOutput.ExtractFenced(
            completion.FullLog,
            FunctionHarnessBuilder.TestBeginSentinel,
            FunctionHarnessBuilder.TestEndSentinel);

        // No fence means the harness never reached its emit step — the browser
        // was unreachable, the spec didn't compile, or Playwright itself died.
        // The body's own exceptions are caught inside the harness, so they
        // never land here.
        if (payload is null)
        {
            throw new HarnessException(
                $"The function-test harness terminated (kind={completion.Kind}, " +
                $"exit code {completion.ExitCode}) without reporting a result. " +
                "This usually means the CDP browser was unreachable or the " +
                $"stitched spec failed to compile. Last {LogTailChars} chars of runner log:\n" +
                logTail);
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            var ok = root.TryGetProperty("ok", out var okEl)
                     && okEl.ValueKind == JsonValueKind.True;

            string? returnedJson = null;
            if (root.TryGetProperty("returned", out var ret) && ret.ValueKind != JsonValueKind.Null)
            {
                // Re-serialise rather than pass the raw text through: the value
                // may be any JSON shape, and the frontend renders it as JSON.
                returnedJson = ret.GetRawText();
            }

            string? errorKind = null, errorMessage = null, errorStack = null;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                errorKind = Str(err, "kind");
                errorMessage = Str(err, "message");
                errorStack = Str(err, "stack");
            }

            var durationMs = root.TryGetProperty("durationMs", out var d)
                             && d.TryGetInt32(out var ms) ? ms : 0;

            return new FunctionTestOutcome(
                Ok: ok,
                ReturnedJson: returnedJson,
                ErrorKind: errorKind,
                ErrorMessage: errorMessage,
                ErrorStack: errorStack,
                DurationMs: durationMs,
                PageUrl: Str(root, "pageUrl"),
                PageTitle: Str(root, "pageTitle"),
                LogTail: logTail);
        }
        catch (JsonException ex)
        {
            throw new HarnessException(
                $"The function-test harness emitted a payload that isn't valid JSON: {ex.Message}. " +
                $"Payload was: {payload[..Math.Min(200, payload.Length)]}");
        }
    }

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
