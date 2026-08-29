using System.Text.Json;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Infrastructure.Runner;

/// <summary>
/// Executes a <see cref="Plan"/> against the connected browser and returns
/// the terminal <see cref="ExecutionEnvelope"/>.
///
/// Uses <see cref="IScriptRunner.RunAsync"/> (not <c>RunOnceAsync</c>) so
/// per-step events flow through <see cref="IExecutionRegistry"/> into the
/// existing SSE endpoint — the wizard's Run page attaches to the same
/// stream every legacy execution used, and just needs to recognize lines
/// prefixed with <see cref="FunctionHarnessBuilder.EventLinePrefix"/> to
/// render structured step cards.
///
/// The envelope is assembled by parsing the same event lines from the
/// runner's returned <c>FullLog</c> after the process exits. Steps that
/// appeared in the plan but never emitted a <c>step-started</c> event
/// are reconstructed as <see cref="StepStatus.Skipped"/> — the harness
/// fail-fast returns early, so the tail of a failed plan legitimately
/// has no events in the log.
/// </summary>
public sealed class PlanInterpreter(IScriptRunner runner) : IPlanInterpreter
{
    public async Task<ExecutionEnvelope> ExecuteAsync(
        Guid runId,
        Signature signature,
        IReadOnlyList<Signature>? dependencies,
        Plan plan,
        GatheredState state,
        CancellationToken ct = default)
    {
        // Empty plan is a legitimate outcome — the planner explicitly emits
        // an empty steps array when the state doesn't ground the prompt.
        // Return a well-shaped envelope so the UI has something to render.
        if (plan.Steps.Count == 0)
        {
            return new ExecutionEnvelope(
                Outcome: RunOutcome.Ok,
                StepsTotal: 0,
                StepsExecuted: 0,
                Steps: Array.Empty<StepResult>(),
                CapturedState: state,
                Plan: plan);
        }

        var harness = FunctionHarnessBuilder.BuildExecuteHarness(signature, dependencies, plan);

        RunnerCompletion completion;
        try
        {
            completion = await runner.RunAsync(runId, harness, ct);
        }
        catch (Exception ex)
        {
            throw new HarnessException(
                "Plan interpreter could not spawn Playwright. Is the CDP browser " +
                "listening on the expected port?", ex);
        }

        // Parse the event stream out of the runner's captured stdout. Order
        // preserved because the runner appends line-by-line in emission
        // order.
        var events = ParseEvents(completion.FullLog);
        return AssembleEnvelope(plan, state, events, completion);
    }

    // ── Event parsing ─────────────────────────────────────────────────

    /// <summary>
    /// One <c>__MZ_EVENT__</c> line as pulled from the runner log. Kept as
    /// a JsonElement rather than fully-typed variants because there are
    /// only ~5 event kinds and the reconstruction below reads two or three
    /// fields per kind — full typing would balloon this file without
    /// meaningful safety.
    /// </summary>
    private sealed record HarnessEvent(string Type, JsonElement Payload);

    private static IReadOnlyList<HarnessEvent> ParseEvents(string fullLog)
    {
        var result = new List<HarnessEvent>();
        foreach (var raw in fullLog.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            // Playwright's list reporter prefixes each console.log line
            // with a small icon + relative path; we search for the
            // sentinel anywhere on the line rather than requiring it at
            // the start.
            var idx = line.IndexOf(FunctionHarnessBuilder.EventLinePrefix, StringComparison.Ordinal);
            if (idx < 0) continue;
            var jsonPart = line[(idx + FunctionHarnessBuilder.EventLinePrefix.Length)..].Trim();
            if (jsonPart.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(jsonPart);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("type", out var typeElem)
                    || typeElem.ValueKind != JsonValueKind.String) continue;
                result.Add(new HarnessEvent(typeElem.GetString()!, root.Clone()));
            }
            catch (JsonException)
            {
                // Malformed event → ignore. This can happen if Playwright's
                // reporter interleaves with our line mid-print. The
                // envelope assembler tolerates missing events for a step
                // by marking it Skipped.
            }
        }
        return result;
    }

    // ── Envelope assembly ─────────────────────────────────────────────

    /// <summary>
    /// Walk the plan and the observed event list together, mapping each
    /// plan step to a <see cref="StepResult"/>. This method is the single
    /// source of truth for how a running plan becomes a terminal envelope:
    /// step-ok → Ok, step-failed → Failed, absent → Skipped. Overall
    /// outcome is Failed if any step failed OR if the plan had steps that
    /// never even started (which happens on harness-level errors before
    /// the loop reached them).
    /// </summary>
    private static ExecutionEnvelope AssembleEnvelope(
        Plan plan,
        GatheredState state,
        IReadOnlyList<HarnessEvent> events,
        RunnerCompletion completion)
    {
        // Index the observed ok/failed events by step id so we can look
        // each plan step up in O(1). started-only events are ignored for
        // the envelope — we only care about terminal per-step events.
        var byId = new Dictionary<string, HarnessEvent>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            if (e.Type is not ("step-ok" or "step-failed")) continue;
            if (!e.Payload.TryGetProperty("id", out var idElem)) continue;
            if (idElem.ValueKind != JsonValueKind.String) continue;
            var id = idElem.GetString();
            if (string.IsNullOrEmpty(id)) continue;
            byId[id] = e; // last write wins if the harness ever double-emits
        }

        var stepResults = new List<StepResult>(plan.Steps.Count);
        var executed = 0;
        var anyFailed = false;

        foreach (var step in plan.Steps)
        {
            if (!byId.TryGetValue(step.Id, out var evt))
            {
                // No terminal event → either the fail-fast bailed before
                // this step, or the harness crashed. Envelope marks it
                // Skipped either way. The Outcome captures the failure.
                stepResults.Add(new StepResult(
                    Id: step.Id,
                    Call: step.Call,
                    Status: StepStatus.Skipped,
                    DurationMs: 0,
                    Error: null));
                continue;
            }

            executed++;
            var duration = evt.Payload.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetInt64() : 0;

            if (evt.Type == "step-ok")
            {
                stepResults.Add(new StepResult(step.Id, step.Call, StepStatus.Ok, duration, null));
            }
            else
            {
                anyFailed = true;
                StepError? err = null;
                if (evt.Payload.TryGetProperty("error", out var errElem)
                    && errElem.ValueKind == JsonValueKind.Object)
                {
                    var kind = errElem.TryGetProperty("kind", out var kElem) && kElem.ValueKind == JsonValueKind.String
                        ? ParseErrorKind(kElem.GetString()!)
                        : StepErrorKind.RuntimeError;
                    var msg = errElem.TryGetProperty("message", out var mElem) && mElem.ValueKind == JsonValueKind.String
                        ? mElem.GetString() ?? ""
                        : "";
                    err = new StepError(kind, msg);
                }
                stepResults.Add(new StepResult(step.Id, step.Call, StepStatus.Failed, duration, err));
            }
        }

        // Outcome policy:
        //   - Any step-failed  → Failed
        //   - Any step-skipped → Failed (fail-fast means either a failure
        //                       stopped the loop, or the harness crashed;
        //                       either way we didn't finish the plan)
        //   - Everything Ok    → Ok
        var anySkipped = stepResults.Any(s => s.Status == StepStatus.Skipped);
        var outcome = (anyFailed || anySkipped) ? RunOutcome.Failed : RunOutcome.Ok;

        // If we ended up Failed but nothing in the events explained why —
        // e.g. Playwright itself crashed before printing a step event — the
        // envelope Steps list still tells the truth (all Skipped, no
        // errors), but the whole run is opaque. Rather than fabricate an
        // error to hang off a specific step, we let the SSE log (which the
        // wizard also shows) carry the raw Playwright stack for the user.
        // Harness-level failures that fail every step get surfaced through
        // RunnerCompletion.Kind — a caller can key off completion.Kind to
        // render "runner crashed" separately from "your plan didn't work".
        _ = completion;

        return new ExecutionEnvelope(
            Outcome: outcome,
            StepsTotal: plan.Steps.Count,
            StepsExecuted: executed,
            Steps: stepResults,
            CapturedState: state,
            Plan: plan);
    }

    /// <summary>
    /// Map the harness's short error-kind strings onto the typed enum. Any
    /// unknown value falls through to RuntimeError — a bad kind string
    /// isn't worth failing the whole envelope over.
    /// </summary>
    private static StepErrorKind ParseErrorKind(string raw) => raw switch
    {
        "SchemaMismatch"     => StepErrorKind.SchemaMismatch,
        "FunctionNotFound"   => StepErrorKind.FunctionNotFound,
        "PreconditionFailed" => StepErrorKind.PreconditionFailed,
        "HarnessError"       => StepErrorKind.HarnessError,
        _                    => StepErrorKind.RuntimeError,
    };
}
