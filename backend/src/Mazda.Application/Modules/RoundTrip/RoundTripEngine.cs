using System.Text;
using System.Text.Json.Nodes;
using Mazda.Application.Common.Usage;
using Mazda.Application.Common.Abstractions;
using Mazda.Application.Common.Tracing;
using Mazda.Application.Modules.Cdp;
using Mazda.Application.Modules.Recordings;
using Mazda.Domain.Common;
using Mazda.Domain.Modules.RoundTrip;
using Mazda.Domain.Modules.Signatures;

namespace Mazda.Application.Modules.RoundTrip;

/// <summary>
/// Runs one round trip in the background:
///   1. learn  - replay the confirmed plan twice (what differs is noise) and once
///               with the contrast choices (what differs carries the choice);
///   2. judge  - compare every step with its demonstrated span; what the comparison cannot
///               decide goes to the picture judge when it is on (off by default), else to the author;
///   3. fix    - earliest step that differs: diagnose (function / plan / earlier step /
///               nondeterminism), apply one change, replay from the start, keep the change
///               only if that step got closer and no earlier step got worse;
///   4. check  - repaired functions must still react to their arguments (contrast replay).
/// Every replay starts from the recording's start page and replays the whole plan:
/// recordings are self-contained, so each replay begins from a fresh context.
/// </summary>
public sealed class RoundTripEngine(
    ISignatureRepository signatures,
    IRecordingRepository recordings,
    IRoundTripRepository roundTrips,
    IEffectiveSignatureResolver resolver,
    IUserSettingsRepository userSettings,
    ICdpClient cdp,
    IRoundTripReplayer replayer,
    IEffectJudge judge,
    IRoundTripFixer fixer,
    ICatalogueLint lint,
    IExecutionRegistry feed,
    ITraceStore traces,
    IUnitOfWork uow)
{
    public const int MaxFixAttempts = 6;
    public const int MaxAttemptsPerFunction = 3;

    private RoundTripSession _session = null!;
    private Signature _sig = null!;
    private IReadOnlyList<Signature>? _deps;
    private PlanModel _plan = null!;
    private RoundTripDemo _demo = null!;
    private CdpTarget _target = null!;
    private List<LogEntry> _log = new();
    private int _runs;
    private readonly Dictionary<int, LearnedRoles> _roles = new();
    /// <summary>Functions that failed in one learning replay and passed in the other, with the body that did so.</summary>
    private readonly Dictionary<string, (string Body, string Error)> _flaky = new(StringComparer.Ordinal);
    /// <summary>A fix applied but not yet judged: undone if the run stops before the verdict.</summary>
    private (SignatureFunction Fn, string OldBody)? _trial;

    private TraceRecorder _trace = null!;
    private Guid _ownerId;

    public async Task RunAsync(Guid sessionId, Guid ownerId, CancellationToken ct = default)
    {
        _trace = TraceContext.Begin("roundtrip", "round trip");
        // Its own usage scope: this runs in the background, after the request that started it.
        LlmUsageContext.Begin(ownerId, LlmUsageKinds.RoundTrip, sessionId, "round trip");
        _ownerId = ownerId;
        var root = _trace.Span("round trip");
        try
        {
            var session = await roundTrips.FindAsync(sessionId, ownerId, ct) ?? throw new InvalidOperationException("Round trip not found.");
            _session = session;
            _log = RoundTripJson.Read<List<LogEntry>>(session.LogJson) ?? new List<LogEntry>();
            _runs = RoundTripJson.Read<ResultModel>(session.ResultJson)?.Replays ?? 0;
            await RunCoreAsync(ownerId, ct);
        }
        catch (Exception) when (ct.IsCancellationRequested && _session is not null)
        {
            if (_trial is { } t)
            {
                try { await RevertAsync(t.Fn, t.OldBody, "the run was stopped before the fix could be checked", CancellationToken.None); }
                catch (Exception ex) { Say("error", $"Could not undo the untested change to {t.Fn.Name}: {ExceptionMessage.Describe(ex)}", t.Fn.Name); }
            }
            Say("info", "Stopped by you. Fixes that were already checked and kept stay; undo them below if you want.");
            _session.SetStatus(RoundTripStatus.Stopped);
            _session.SetLog(RoundTripJson.Write(_log));
            await uow.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Say("error", $"Stopped: {ExceptionMessage.Describe(ex)}");
            if (_session is not null)
            {
                _session.SetStatus(RoundTripStatus.Failed, ExceptionMessage.Describe(ex));
                _session.SetLog(RoundTripJson.Write(_log));
                await uow.SaveChangesAsync(CancellationToken.None);
            }
        }
        finally
        {
            root.Dispose();
            await traces.SaveAsync(_trace, ownerId, sessionId, CancellationToken.None);
            feed.Complete(sessionId, new RunnerCompletion(RunnerExitKind.Passed, 0, string.Empty));
        }
    }

    private async Task RunCoreAsync(Guid ownerId, CancellationToken ct)
    {
        _sig = await signatures.FindByIdAsync(_session.SignatureId, ownerId, ct) ?? throw new InvalidOperationException("The signature no longer exists.");
        if (_sig.DependencyIds.Count > 0)
            _deps = (await resolver.ResolveAsync(_sig.Id, ownerId, ct))?.Where(s => s.Id != _sig.Id).ToList();
        _plan = RoundTripJson.Read<PlanModel>(_session.PlanJson) ?? throw new InvalidOperationException("The plan could not be read.");
        _trace.Label = $"round trip: {_plan.Steps.Count} step(s) ({string.Join(", ", _plan.Steps.Select(x => x.Call).Distinct().Take(4))})";
        if (LlmUsageContext.Current is { } usageScope) usageScope.SubjectLabel = _trace.Label;
        var events = await recordings.ListAllEventsAsync(_session.RecordingId, ct);
        if (events.Count == 0) throw new InvalidOperationException("The recording has no events.");
        _demo = RoundTripDemo.Build(_plan, events);

        var (browserTab, cdpError) = await CdpTargetResolver.ResolveValidatedAsync(userSettings, cdp, ownerId, ct);
        if (cdpError is not null) throw new InvalidOperationException(cdpError);
        _target = browserTab;

        var actions = Actions();
        var profiles = (await roundTrips.ListProfilesAsync(_sig.Id, ct)).ToDictionary(p => p.FunctionId);

        Say("info", _session.PictureJudge
            ? "The picture judge is on: a model compares the pictures of steps the comparison cannot decide."
            : "The picture judge is off: steps the comparison cannot decide are left for you to answer.");
        if (!string.IsNullOrWhiteSpace(_session.StartUrl))
            Say("info", $"Every replay starts on {_session.StartUrl} (chosen for this round trip; the recording started elsewhere).");

        // ── 1. Learn ─────────────────────────────────────────────────
        Say("info", $"Replaying {_plan.Steps.Count} step(s) twice to learn what naturally differs between runs.");
        var a = await ReplayAsync(Args(), "first replay", ct);
        var aNo = _runs;
        var b = await ReplayAsync(Args(), "second replay (same choices)", ct);
        ReplayRun? c = null;
        if (_plan.Steps.Any(s => !string.IsNullOrWhiteSpace(s.ContrastArgsJson)))
            c = await ReplayAsync(Args(contrast: true), "contrast replay (other choices)", ct);
        for (var i = 0; i < _plan.Steps.Count; i++)
        {
            var hasContrast = !string.IsNullOrWhiteSpace(_plan.Steps[i].ContrastArgsJson);
            _roles[i] = LearnedRoles.Learn(Delta(a, i), Delta(b, i), hasContrast && c is not null ? Delta(c, i) : null);

            // Same choices, same body, one run failed and the other did not: the function depends on
            // something the app does not do every time. One lucky pass later must not hide that.
            var oa = i < a.Steps.Count ? a.Steps[i] : null;
            var ob = i < b.Steps.Count ? b.Steps[i] : null;
            if (oa is { Ran: true } && ob is { Ran: true } && oa.Threw != ob.Threw
                && actions.FirstOrDefault(x => x.Name == _plan.Steps[i].Call) is { } flakyFn)
            {
                _flaky[flakyFn.Name] = (flakyFn.Body, (oa.Threw ? oa.Error : ob.Error) ?? "");
                Say("verdict", $"Step {i + 1} ({flakyFn.Name}) failed in one replay and passed in the other with the same choices.", flakyFn.Name);
            }
        }

        // ── 2. Judge ─────────────────────────────────────────────────
        var current = a;
        var currentRun = aNo;
        var results = await JudgeRunAsync(current, b, currentRun, profiles, ct);
        await PersistAsync(results, RoundTripStatus.Running, ct);

        // ── 3. Fix ───────────────────────────────────────────────────
        var attempts = 0;
        var perFunction = new Dictionary<string, int>(StringComparer.Ordinal);
        var gaveUp = new HashSet<int>();
        var repaired = new HashSet<string>(StringComparer.Ordinal);
        var previousAttempts = new Dictionary<int, StringBuilder>();
        PlanChangeModel? pendingPlan = null;
        while (attempts < MaxFixAttempts)
        {
            var target = results.FindIndex(r => r.Verdict == "different" && !gaveUp.Contains(r.Index));
            if (target < 0) break;
            var step = _plan.Steps[target];
            var fn = actions.FirstOrDefault(x => x.Name == step.Call);
            if (fn is null) { gaveUp.Add(target); continue; }
            if (perFunction.GetValueOrDefault(fn.Name) >= MaxAttemptsPerFunction) { gaveUp.Add(target); continue; }
            attempts++;
            perFunction[fn.Name] = perFunction.GetValueOrDefault(fn.Name) + 1;

            Say("fix", $"Step {target + 1} ({fn.Name}) did not reproduce your demonstration. Asking for a diagnosis…", fn.Name);
            RoundTripEvidence evidence;
            using (_trace.Span($"collect evidence for step {target + 1}", TraceCategory.Db))
                evidence = await EvidenceAsync(target, results, current, currentRun, previousAttempts.GetValueOrDefault(target)?.ToString(), ct);
            RoundTripFix fix;
            try { using (_trace.Span($"diagnose step {target + 1} (LLM)")) fix = await fixer.DiagnoseAsync(_sig, fn, actions, evidence, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Say("error", $"The repair model could not be reached: {ExceptionMessage.Describe(ex)}", fn.Name);
                gaveUp.Add(target);
                continue;
            }
            var attemptLog = previousAttempts.TryGetValue(target, out var sbA) ? sbA : previousAttempts[target] = new StringBuilder();
            attemptLog.AppendLine($"- fault={fix.Fault}: {fix.Explanation}");

            switch (fix.Fault)
            {
                case "plan":
                {
                    var change = new PlanChangeModel(target, fix.PlanCall ?? step.Call, fix.PlanArgsJson ?? step.ArgsJson, fix.Explanation);
                    var proposed = WithChange(_plan, change);
                    if (PlanRules.Problem(proposed, actions, _demo.Episodes) is { } problem)
                    {
                        Say("plan", $"The proposed plan change for step {target + 1} is not valid ({problem}); not applied.", fn.Name);
                        attemptLog.AppendLine($"  rejected: {problem}");
                        continue;
                    }
                    // Park it: the author decides at the end; the other steps can still be fixed meanwhile.
                    pendingPlan ??= change;
                    gaveUp.Add(target);
                    Say("plan", $"Step {target + 1} looks like a plan problem: {fix.Explanation} You will be asked to confirm the change.", fn.Name);
                    continue;
                }
                case "earlier-step":
                    if (target > 0)
                    {
                        Say("fix", $"The cause seems to be an earlier step: {fix.Explanation}", fn.Name);
                        results[target - 1] = results[target - 1] with { Verdict = "different" };
                        gaveUp.Add(target);
                    }
                    else gaveUp.Add(target);
                    continue;
                case "nondeterminism":
                    Say("question", $"Step {target + 1}: the app seems to behave differently between runs ({fix.Explanation}). Asking you.", fn.Name);
                    results[target] = results[target] with { Verdict = "question", Question = $"Mazda thinks the app behaves differently between runs here: {fix.Explanation} Did the function do what you did?" };
                    gaveUp.Add(target);
                    continue;
            }

            // fault = function
            var newBody = fix.Body?.Trim() ?? "";
            var reject = newBody.Length == 0 ? "no body was proposed"
                : newBody == fn.Body.Trim() ? "the proposed body is unchanged"
                : lint.Issues(fn, newBody) is { Count: > 0 } issues ? $"it breaks the body rules ({issues[0]})"
                : HardCodedChoice(fn, newBody) is { } literal ? $"it hard-codes your demonstrated choice “{literal}” instead of using the argument"
                : null;
            if (reject is not null)
            {
                Say("fix", $"A proposed fix for {fn.Name} was not applied: {reject}.", fn.Name);
                attemptLog.AppendLine($"  rejected: {reject}");
                continue;
            }

            var oldBody = fn.Body;
            await roundTrips.AddRevisionAsync(new FunctionBodyRevision(fn.Id, _sig.Id, _session.Id, oldBody, fix.Explanation), ct);
            fn.UpdateBody(newBody);
            _trial = (fn, oldBody);
            await uow.SaveChangesAsync(ct);
            Say("fix", $"Trying a fix for {fn.Name}: {fix.Explanation}", fn.Name);
            attemptLog.AppendLine("  body tried:\n" + Indent(newBody));

            ReplayRun next;
            try { next = await ReplayAsync(Args(), $"replay after fixing {fn.Name}", ct); }
            catch (HarnessException ex) when (!ct.IsCancellationRequested)
            {
                _trial = null;
                await RevertAsync(fn, oldBody, $"the fixed body did not run ({ExceptionMessage.Describe(ex)})", ct);
                attemptLog.AppendLine($"  outcome: the harness could not run it: {ExceptionMessage.Describe(ex)}");
                continue;
            }
            var nextRun = _runs;
            var nextResults = await JudgeRunAsync(next, null, nextRun, profiles, ct);
            _trial = null;

            var improved = Rank(nextResults[target].Verdict) > Rank(results[target].Verdict);
            var regressed = Enumerable.Range(0, target).Any(i => Rank(nextResults[i].Verdict) < Rank(results[i].Verdict));
            if (improved && !regressed)
            {
                Say("fix", $"Kept the fix for {fn.Name}: step {target + 1} is now “{nextResults[target].Verdict}”.", fn.Name);
                repaired.Add(fn.Name);
                current = next; currentRun = nextRun; results = nextResults;
                await PersistAsync(results, RoundTripStatus.Running, ct);
            }
            else
            {
                // What the fixed body actually did: the next diagnosis needs this to correct the attempt, not guess anew.
                var outcome = Outcome(nextResults[target]);
                var why = regressed ? "it made an earlier step worse" : $"the step did not get closer to your demonstration ({outcome})";
                await RevertAsync(fn, oldBody, why, ct);
                attemptLog.AppendLine($"  outcome when replayed: {outcome}" + (regressed ? "; it also made an earlier step worse" : ""));
            }
        }

        // ── 4. A repaired function must still react to its argument ──
        // Per function, not per step: one verdict, at most one revert. Only steps that ran
        // cleanly in both replays count as evidence; everything else is "cannot tell".
        if (repaired.Count > 0 && _plan.Steps.Any(s => repaired.Contains(s.Call) && !string.IsNullOrWhiteSpace(s.ContrastArgsJson)))
        {
            var check = await ReplayAsync(Args(contrast: true), "contrast replay after fixes", ct);
            foreach (var name in repaired.Where(n => _plan.Steps.Any(s => s.Call == n && !string.IsNullOrWhiteSpace(s.ContrastArgsJson))))
            {
                var reacts = 0; var ignores = 0;
                for (var i = 0; i < _plan.Steps.Count; i++)
                {
                    var s = _plan.Steps[i];
                    if (s.Call != name || string.IsNullOrWhiteSpace(s.ContrastArgsJson)) continue;
                    switch (ReactsToArgument(i, current, check))
                    {
                        case true: reacts++; break;
                        case false: ignores++; break;
                    }
                }
                var fn = actions.First(x => x.Name == name);
                if (reacts > 0 || ignores == 0)
                {
                    if (reacts == 0) Say("info", $"Could not confirm that the fixed {name} still reacts to its argument (the contrast replay gave no clean comparison); the fix is kept.", name);
                    continue;
                }
                var revision = await roundTrips.LatestRevisionAsync(fn.Id, _session.Id, ct);
                if (revision is null) continue;
                await RevertAsync(fn, revision.Body, "with another choice it sent the same values, so it ignores its argument", ct);
                // The stored verdicts described the body just undone.
                for (var i = 0; i < results.Count; i++)
                    if (_plan.Steps[i].Call == name)
                        results[i] = results[i] with
                        {
                            Verdict = "different",
                            Findings = results[i].Findings.Prepend(new Finding("blocking", "fix-undone", "fix-undone",
                                "Mazda's last fix was undone: with another choice it sent the same values, so it ignored its argument.")).ToList(),
                        };
            }
        }

        if (pendingPlan is not null) _session.SetPendingPlanChange(RoundTripJson.Write(pendingPlan));
        var status = pendingPlan is not null ? RoundTripStatus.NeedsPlan
            : results.Any(r => r.Verdict == "question") ? RoundTripStatus.NeedsAnswers
            : RoundTripStatus.Done;
        Say("info", Summary(results));
        await PersistAsync(results, status, ct);
    }

    // ── Replay + judge ───────────────────────────────────────────────

    private List<RoundTripCall> Args(bool contrast = false)
        => _plan.Steps.Select(s => new RoundTripCall(s.Call,
            contrast && !string.IsNullOrWhiteSpace(s.ContrastArgsJson) ? s.ContrastArgsJson! : s.ArgsJson)).ToList();

    private const int PictureReadinessMaxSeconds = 15;

    /// <summary>The page every replay opens first: the author's choice, else where the recording started.</summary>
    private string? StartUrl => string.IsNullOrWhiteSpace(_session.StartUrl) ? _demo.StartUrl : _session.StartUrl;

    private async Task<ReplayRun> ReplayAsync(List<RoundTripCall> calls, string label, CancellationToken ct)
    {
        Say("replay", $"Replay {_runs + 1}: {label}…");
        ReplayRun run;
        using (_trace.Span($"replay {_runs + 1}: {label}"))
            run = await replayer.ReplayAsync(_session.Id, _sig, _deps, calls, _target, StartUrl, ct);
        // A stopped replay comes back as a cancelled run, not an exception: stop here before judging it.
        ct.ThrowIfCancellationRequested();
        _runs++;
        using var saving = _trace.Span("save the replay's pictures", TraceCategory.Db);
        var shots = new List<RoundTripShot>();
        for (var i = 0; i < run.Steps.Count; i++)
        {
            if (run.Steps[i].BeforeJpeg is { } bj) shots.Add(new RoundTripShot(_session.Id, _runs, i, "before", bj));
            if (run.Steps[i].AfterJpeg is { } aj) shots.Add(new RoundTripShot(_session.Id, _runs, i, "after", aj));
            foreach (var (phase, busy) in new[] { ("before", run.Steps[i].BeforeBusy), ("after", run.Steps[i].AfterBusy) })
                if (busy is not null)
                    Say("info", $"Replay {_runs}, step {i + 1} ({run.Steps[i].Call}): the {phase} picture was taken while the page was still busy " +
                                $"({busy}); waited {PictureReadinessMaxSeconds} s for it.", run.Steps[i].Call);
        }
        await roundTrips.ReplaceShotsAsync(_session.Id, _runs, shots, ct);
        await uow.SaveChangesAsync(ct);
        return run;
    }

    private static StepDelta Delta(ReplayRun run, int i)
    {
        if (i >= run.Steps.Count) return StepDelta.Empty;
        var s = run.Steps[i];
        // Same masking the recorder applies to stored demonstrations, so masked values compare as masked on both sides.
        var network = s.Network.Select(n =>
        {
            var copy = (JsonObject)n.DeepClone();
            if (copy["reqBody"] is JsonValue v && v.TryGetValue<string>(out var body)) copy["reqBody"] = PayloadSanitizer.RedactBody(body);
            return copy;
        });
        return DeltaBuilder.Build(s.Mutations, network);
    }

    private static StepObservation Observe(ReplayRun run, int i)
    {
        if (i >= run.Steps.Count) return new StepObservation(StepDelta.Empty, false, false, null);
        var s = run.Steps[i];
        var before = i > 0 && run.Steps[i - 1].Ran ? run.Steps[i - 1].Aria : null;
        return new StepObservation(Delta(run, i), s.Ran, s.Threw, s.Error, before, s.Aria);
    }

    /// <summary>The step's own roles, plus what the other steps of the same function learned.</summary>
    private LearnedRoles RolesFor(int i)
    {
        var call = _plan.Steps[i].Call;
        var others = Enumerable.Range(0, _plan.Steps.Count)
            .Where(k => k != i && _plan.Steps[k].Call == call && _roles.ContainsKey(k))
            .Select(k => _roles[k]);
        return LearnedRoles.Merge(_roles.GetValueOrDefault(i) ?? LearnedRoles.None, others);
    }

    private async Task<List<StepResultModel>> JudgeRunAsync(
        ReplayRun run, ReplayRun? twin, int runNo, Dictionary<Guid, FunctionComparisonProfile> profiles, CancellationToken ct)
    {
        var actions = Actions();
        var list = new List<StepResultModel>();
        for (var i = 0; i < _plan.Steps.Count; i++)
        {
            var step = _plan.Steps[i];
            var fn = actions.FirstOrDefault(x => x.Name == step.Call);
            var profile = fn is null ? null : profiles.GetValueOrDefault(fn.Id);
            var accepted = RoundTripJson.Read<List<string>>(profile?.AcceptedJson)?.ToHashSet() ?? new HashSet<string>();
            var rejected = RoundTripJson.Read<List<string>>(profile?.RejectedJson)?.ToHashSet() ?? new HashSet<string>();
            var repetitions = _plan.Steps.Select((s, k) => (s, k))
                .Where(x => x.k != i && x.s.Call == step.Call && x.s.ArgsJson != step.ArgsJson)
                .Select(x => _demo.Deltas[x.k]).ToList();
            var siblings = _plan.Steps.Select((s, k) => (s, k))
                .Where(x => x.k != i && x.s.Call == step.Call)
                .Select(x => _demo.Deltas[x.k]).ToList();

            var obs = Observe(run, i);
            var judgement = StepComparer.Judge(new StepContext(step.Call, step.ArgsJson, _demo.Deltas[i], obs,
                twin is null ? null : Observe(twin, i), repetitions, accepted, rejected,
                RolesFor(i), siblings));

            var verdict = judgement.Verdict;
            JudgeModel? judgeModel = null;
            string? question = null;
            if (verdict == "unexplained" && !_session.PictureJudge)
            {
                // The picture judge is off: the author compares the pictures.
                verdict = "question";
                question = "Mazda could not tell whether the function did what you did. Compare the pictures: is the result the same?";
                Say("question", $"Step {i + 1} ({step.Call}): the comparison cannot decide and the picture judge is off. Asking you.", step.Call);
            }
            else if (verdict == "unexplained")
            {
                var (jv, jr) = await PictureJudgeAsync(i, runNo, judgement, ct);
                judgeModel = new JudgeModel(jv, jr);
                verdict = jv switch { "same" => "equivalent", "different" => "different", _ => "question" };
                if (verdict == "question")
                    question = "Mazda could not tell whether the function did what you did. Compare the pictures: is the result the same?";
                Say("verdict", $"Step {i + 1} ({step.Call}): the picture judge says “{jv}”. {jr}", step.Call);
            }

            var findings = judgement.Findings.ToList();
            if (fn is not null && _flaky.TryGetValue(fn.Name, out var flaky) && flaky.Body == fn.Body && verdict != "not-run")
            {
                findings.Insert(0, new Finding("blocking", "flaky", "flaky",
                    $"With the same choices it failed in one replay and passed in another (failed with: {flaky.Error.Split('\n')[0]}). " +
                    "It depends on something the app does not do every time."));
                verdict = "different";
                question = null;
            }

            var s = i < run.Steps.Count ? run.Steps[i] : null;
            list.Add(new StepResultModel(i, step.Call, verdict, findings, judgeModel, question,
                s?.Error, s?.DurationMs ?? 0, runNo,
                _demo.RequestLines[i], RoundTripDemo.Lines(obs.Delta), s?.Console.ToList() ?? new List<string>()));
        }
        Say("verdict", "Results: " + string.Join(", ", list.Select(r => $"{r.Index + 1}:{r.Verdict}")));
        return list;
    }

    private async Task<(string Verdict, string Reason)> PictureJudgeAsync(int step, int runNo, StepJudgement j, CancellationToken ct)
    {
        try
        {
            var (bk, ak) = _demo.ShotKeys[step];
            var demoBefore = bk is null ? null : await recordings.FindScreenshotAsync(_session.RecordingId, bk, ct);
            var demoAfter = ak is null ? null : await recordings.FindScreenshotAsync(_session.RecordingId, ak, ct);
            var replayBefore = await roundTrips.FindShotAsync(_session.Id, runNo, step, "before", ct);
            var replayAfter = await roundTrips.FindShotAsync(_session.Id, runNo, step, "after", ct);
            var diffs = string.Join("\n", j.Findings.Where(f => f.Severity != "note").Select(f => "- " + f.Summary));
            using var span = _trace.Span($"picture judge, step {step + 1} (LLM)");
            var v = await judge.JudgeAsync($"{_plan.Steps[step].Call} {_plan.Steps[step].ArgsJson}: {_demo.Descriptions[step]}",
                diffs, demoBefore, demoAfter, replayBefore, replayAfter, ct);
            return (v.Verdict, v.Reason);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return ("unsure", $"The picture judge could not be reached: {ExceptionMessage.Describe(ex)}");
        }
    }

    // ── Fix helpers ──────────────────────────────────────────────────

    private async Task<RoundTripEvidence> EvidenceAsync(int i, List<StepResultModel> results, ReplayRun run, int runNo, string? previous, CancellationToken ct)
    {
        var r = results[i];
        var s = i < run.Steps.Count ? run.Steps[i] : null;
        var (_, ak) = _demo.ShotKeys[i];
        var earlier = string.Join("\n", results.Take(i).Select(x => $"step {x.Index + 1} {x.Call}: {x.Verdict}"));
        return new RoundTripEvidence(
            StepDescription: $"Step {i + 1} of {_plan.Steps.Count}: {_plan.Steps[i].Call}",
            ArgsJson: _plan.Steps[i].ArgsJson,
            DemoSteps: _demo.Descriptions[i],
            Differences: string.Join("\n", r.Findings.Select(f => $"[{f.Severity}] {f.Summary}"))
                         + (r.Judge is null ? "" : $"\n[picture judge] {r.Judge.Verdict}: {r.Judge.Reason}"),
            DemoRequests: string.Join("\n", r.DemoRequests),
            ReplayRequests: string.Join("\n", r.ReplayRequests),
            Error: s?.Error,
            ErrorStack: s?.ErrorStack,
            Console: s?.Console ?? Array.Empty<string>(),
            Aria: s?.Aria,
            EarlierSteps: earlier.Length == 0 ? "(this is the first step)" : earlier,
            DemoAfter: ak is null ? null : await recordings.FindScreenshotAsync(_session.RecordingId, ak, ct),
            ReplayAfter: await roundTrips.FindShotAsync(_session.Id, runNo, i, "after", ct),
            PreviousAttempts: previous);
    }

    /// <summary>A value the user chose in the demonstration that a fix newly writes into the body: overfitting to one example.</summary>
    private string? HardCodedChoice(SignatureFunction fn, string newBody)
    {
        foreach (var step in _plan.Steps.Where(s => s.Call == fn.Name))
            foreach (var v in StepComparer.ChosenValues(step.ArgsJson))
                if (v.Length >= 3 && newBody.Contains(v, StringComparison.Ordinal) && !fn.Body.Contains(v, StringComparison.Ordinal))
                    return v;
        return null;
    }

    private async Task RevertAsync(SignatureFunction fn, string body, string why, CancellationToken ct)
    {
        fn.UpdateBody(body);
        if (await roundTrips.LatestRevisionAsync(fn.Id, _session.Id, ct) is { } rev && rev.Body == body)
            await roundTrips.RemoveRevisionAsync(rev, ct);
        await uow.SaveChangesAsync(ct);
        Say("revert", $"Undid the change to {fn.Name}: {why}.", fn.Name);
    }

    private static PlanModel WithChange(PlanModel plan, PlanChangeModel change)
    {
        var steps = plan.Steps.Select((s, i) => i == change.Step ? s with { Call = change.Call, ArgsJson = change.ArgsJson } : s).ToList();
        return plan with { Steps = steps };
    }

    /// <summary>
    /// Does step <paramref name="i"/> send different values for the fields that carry the choice when only
    /// the choice changes? Null when it cannot be told: the step did not run cleanly in one of the
    /// replays, or no choice-carrying field was seen in both.
    /// </summary>
    private bool? ReactsToArgument(int i, ReplayRun normal, ReplayRun contrast)
    {
        var a = i < normal.Steps.Count ? normal.Steps[i] : null;
        var c = i < contrast.Steps.Count ? contrast.Steps[i] : null;
        if (a is not { Ran: true, Threw: false } || c is not { Ran: true, Threw: false }) return null;
        var da = Delta(normal, i);
        var dc = Delta(contrast, i);
        var roles = RolesFor(i);
        var intent = roles.Intent;
        var compared = 0;

        // Nothing was learned while the function was still broken (a buggy body can send the same
        // values for every choice): compare the two replays directly, minus what is known to be noise.
        if (intent.Count == 0)
        {
            foreach (var ra in da.Requests)
            {
                var rc = dc.Requests.FirstOrDefault(r => r.Key == ra.Key);
                if (rc is null) continue;
                foreach (var (f, v) in ra.Fields)
                {
                    if (roles.Noise.Contains($"field:{ra.Key}:{f}") || !rc.Fields.TryGetValue(f, out var w)) continue;
                    compared++;
                    if (v != w) return true;
                }
            }
            return compared == 0 ? null : false;
        }

        foreach (var key in intent)
        {
            // "field:{METHOD /path}:{field}"
            var rest = key["field:".Length..];
            var sep = rest.LastIndexOf(':');
            if (sep <= 0) continue;
            var (req, field) = (rest[..sep], rest[(sep + 1)..]);
            var va = da.Requests.FirstOrDefault(r => r.Key == req)?.Fields.GetValueOrDefault(field);
            var vc = dc.Requests.FirstOrDefault(r => r.Key == req)?.Fields.GetValueOrDefault(field);
            if (va is null || vc is null) continue;
            compared++;
            if (va != vc) return true;
        }
        return compared == 0 ? null : false;
    }

    private static string Outcome(StepResultModel r)
    {
        if (!string.IsNullOrEmpty(r.Error)) return $"it failed with: {r.Error.Split('\n')[0]}";
        var diffs = r.Findings.Where(f => f.Severity != "note").Select(f => f.Summary).Take(3).ToList();
        if (r.Judge is not null) diffs.Add($"picture judge: {r.Judge.Reason}");
        return diffs.Count == 0 ? r.Verdict : string.Join(" / ", diffs);
    }

    private static string Indent(string body)
    {
        var lines = body.Split('\n');
        return string.Join("\n", lines.Take(60).Select(l => "    " + l)) + (lines.Length > 60 ? "\n    …" : "");
    }

    private static int Rank(string verdict) => verdict switch
    {
        "identical" or "equivalent" => 3,
        "question" => 2,
        "different" => 1,
        _ => 0,
    };

    private static string Summary(List<StepResultModel> results)
    {
        var ok = results.Count(r => r.Verdict is "identical" or "equivalent");
        var q = results.Count(r => r.Verdict == "question");
        var bad = results.Count(r => r.Verdict == "different");
        return $"{ok} of {results.Count} step(s) reproduce your demonstration" +
               (bad > 0 ? $", {bad} still differ" : "") + (q > 0 ? $", {q} need your answer" : "") + ".";
    }

    private List<SignatureFunction> Actions() => _sig.Functions.Where(f => f.Kind == FunctionKind.Action).ToList();

    private async Task PersistAsync(List<StepResultModel> results, RoundTripStatus status, CancellationToken ct)
    {
        _session.SetResult(RoundTripJson.Write(new ResultModel(results, _runs, Summary(results))));
        _session.SetLog(RoundTripJson.Write(_log));
        _session.SetStatus(status);
        using (_trace.Span("save", TraceCategory.Db))
            await uow.SaveChangesAsync(ct);
        // The trace so far, so it can be looked at while the round trip runs.
        await traces.SaveAsync(_trace, _ownerId, _session.Id, CancellationToken.None);
    }

    private void Say(string kind, string message, string? function = null)
    {
        _log.Add(new LogEntry(DateTime.UtcNow, kind, message, function));
        if (_session is not null) feed.Append(_session.Id, message);
    }
}
