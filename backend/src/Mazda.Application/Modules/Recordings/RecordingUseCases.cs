using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using MediatR;
using Mazda.Application.Common.Abstractions;
using Mazda.Application.Modules.Cdp;
using Mazda.Application.Modules.Signatures;
using Mazda.Domain.Common;
using Mazda.Domain.Modules.Recordings;

namespace Mazda.Application.Modules.Recordings;

// ── DTOs ─────────────────────────────────────────────────────────────

public record RecordingDto(
    Guid Id,
    Guid SignatureId,
    string Label,
    /// True while the label is machine-chosen (timestamp / analyzer title).
    bool LabelIsAuto,
    /// The user's optional statement of what they demonstrated.
    string Intent,
    /// The analyzer's readback of what this session shows; empty until analyzed.
    string Understanding,
    string Status,
    DateTime StartedAt,
    DateTime? EndedAt,
    string StartUrl,
    IReadOnlyList<string> AllowedOrigins,
    bool CaptureBodies,
    bool MaskAllInputs,
    int EventCount,
    int ActionCount,
    bool Truncated,
    string Error,
    bool HasEpisodes,
    bool HasProposal,
    DateTime? AppliedAt,
    bool UseForGrounding);

public record RecordingEventDto(int Seq, long TMs, string Kind, string? ActionId, string PayloadJson);

public record RecordingEpisodesDto(Guid RecordingId, string EpisodesJson);

public record ProposedElementDto(
    string Selector,
    string Category,
    string ElementType,
    string Description,
    string Logic,
    string? MatchesExisting,
    IReadOnlyList<int> Evidence,
    /// False when the selector (or MatchesExisting) is already in the signature — applying only enriches it.
    bool IsNew,
    /// Interactive, but never acted on in the evidence: its behaviour is inferred.
    bool Untried = false);

public record RecordingProposalDto(
    /// Session the proposal is stored on (the newest analyzed one); apply through it.
    Guid RecordingId,
    IReadOnlyList<ProposedElementDto> Elements,
    string FlowsJson,
    string Notes,
    int TokensUsed,
    /// Every session the proposal was derived from.
    IReadOnlyList<Guid> SessionIds);

public record ApplyRecordingResultDto(
    SignatureDto Signature,
    int AddedElements,
    int EnrichedElements,
    int AddedNodes,
    int AddedEdges,
    /// The signature already had a function catalogue; regenerate it to use what was learned.
    bool FunctionsStale,
    int RemovedElements = 0);

internal static class RecordingMapper
{
    public static RecordingDto ToDto(this RecordingSession r) => new(
        r.Id, r.SignatureId, r.Label, r.LabelIsAuto, r.Intent, r.Understanding, r.Status.ToString(), r.StartedAt, r.EndedAt, r.StartUrl,
        ParseOrigins(r.AllowedOriginsJson), r.CaptureBodies, r.MaskAllInputs,
        r.EventCount, r.ActionCount, r.Truncated, r.Error,
        !string.IsNullOrWhiteSpace(r.EpisodesJson), !string.IsNullOrWhiteSpace(r.ProposalJson),
        r.AppliedAt, r.UseForGrounding);

    public static IReadOnlyList<string> ParseOrigins(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }
}

// ── Start ────────────────────────────────────────────────────────────

public record StartRecordingCommand(
    Guid SignatureId,
    string? Label,
    string? Intent,
    IReadOnlyList<string>? AllowedOrigins,
    bool CaptureBodies = true,
    bool MaskAllInputs = false) : IRequest<Result<RecordingDto>>;

public class StartRecordingValidator : AbstractValidator<StartRecordingCommand>
{
    public StartRecordingValidator()
    {
        RuleFor(x => x.SignatureId).NotEmpty();
        RuleFor(x => x.Label).MaximumLength(200);
        RuleFor(x => x.Intent).MaximumLength(2000);
        RuleForEach(x => x.AllowedOrigins).Must(o => UrlTemplater.Origin(o) is not null)
            .WithMessage("Allowed origins must look like https://host[:port].");
    }
}

public class StartRecordingHandler(
    ISignatureRepository signatures,
    IRecordingRepository recordings,
    IRecordingHost host,
    IUserSettingsRepository userSettings,
    ICdpClient cdp,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<StartRecordingCommand, Result<RecordingDto>>
{
    internal const string OrphanMessage = "The recorder process is no longer running.";

    public async Task<Result<RecordingDto>> Handle(StartRecordingCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<RecordingDto>(Error.Validation("Not authenticated."));
        var owner = me.UserId.Value;

        var sig = await signatures.FindByIdAsync(cmd.SignatureId, owner, ct);
        if (sig is null) return Result.Failure<RecordingDto>(Error.NotFound("Signature"));

        // One live recorder per user: there is one attached tab per user, and
        // two recorders on the same tab would double-record every event.
        var active = await recordings.FindActiveForOwnerAsync(owner, ct);
        if (active is not null)
        {
            if (host.IsActive(active.Id))
                return Result.Failure<RecordingDto>(Error.Conflict(
                    "A recording is already in progress. Stop it before starting another."));
            // DB says live but no process backs it — an orphan. Close it out
            // (conditionally: the host may have just finalized it).
            await recordings.TryMarkOrphanFailedAsync(active.Id, OrphanMessage, ct);
        }

        var (target, error) = await CdpTargetResolver.ResolveValidatedAsync(userSettings, cdp, owner, ct);
        if (error is not null) return Result.Failure<RecordingDto>(Error.Validation(error));
        if (target.TargetId is null)
            return Result.Failure<RecordingDto>(Error.Validation(
                "Attach a browser tab first (browser-attachment panel in the top bar). " +
                "Recording needs to know exactly which tab to watch."));

        // Default allowlist: the attached tab's own origin. Everything outside
        // it — third-party iframes, analytics beacons, other sites the user
        // wanders off to — is not recorded.
        var startUrl = string.Empty;
        try
        {
            var tabs = await cdp.ListTabsAsync(target.Port, ct);
            startUrl = tabs.FirstOrDefault(t => t.TargetId == target.TargetId)?.Url ?? string.Empty;
        }
        catch
        {
            return Result.Failure<RecordingDto>(Error.External(
                "Could not reach Chrome's debugging endpoint. Is it running with --remote-debugging-port?"));
        }

        var origins = (cmd.AllowedOrigins ?? Array.Empty<string>())
            .Select(UrlTemplater.Origin).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (origins.Count == 0 && UrlTemplater.Origin(startUrl) is { } tabOrigin) origins.Add(tabOrigin);
        if (origins.Count == 0)
            return Result.Failure<RecordingDto>(Error.Validation(
                "The attached tab is not on an http(s) page. Navigate it to the app you want MAZDA to learn, " +
                "or pass allowedOrigins explicitly."));

        var session = RecordingSession.Start(
            owner, sig.Id, cmd.Label, cmd.Intent, UrlTemplater.RedactQuery(startUrl),
            JsonSerializer.Serialize(origins), cmd.CaptureBodies, cmd.MaskAllInputs,
            target.Port, target.TargetId);
        await recordings.AddAsync(session, ct);
        await uow.SaveChangesAsync(ct);

        host.Start(new RecordingStartSpec(session.Id, target, origins, cmd.CaptureBodies, cmd.MaskAllInputs));
        return Result.Success(session.ToDto());
    }
}

// ── Stop ─────────────────────────────────────────────────────────────

public record StopRecordingCommand(Guid Id) : IRequest<Result<RecordingDto>>;

public class StopRecordingHandler(
    IRecordingRepository recordings,
    IRecordingHost host,
    ICurrentUser me) : IRequestHandler<StopRecordingCommand, Result<RecordingDto>>
{
    public async Task<Result<RecordingDto>> Handle(StopRecordingCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<RecordingDto>(Error.Validation("Not authenticated."));
        var r = await recordings.FindByIdAsync(cmd.Id, me.UserId.Value, ct);
        if (r is null) return Result.Failure<RecordingDto>(Error.NotFound("Recording"));
        if (!r.IsActive) return Result.Success(r.ToDto());

        // Returns immediately: the host flushes, builds episodes and persists
        // Completed in the background. The client waits for the SSE `end`.
        //
        // Both writes are conditional UPDATEs, never a tracked save of `r`:
        // the host may finalize (Completed/Failed) between our read and our
        // write — tab closed or a cap hit at the same moment — and a tracked
        // save would put the session back to Stopping/Failed for good.
        if (!host.IsActive(r.Id))
        {
            if (await recordings.TryMarkOrphanFailedAsync(r.Id, StartRecordingHandler.OrphanMessage, ct))
                r.MarkFailed(StartRecordingHandler.OrphanMessage, r.EventCount, r.ActionCount);
            return Result.Success(r.ToDto());
        }
        // Stopping is persisted BEFORE signalling, so the host's terminal
        // write is always the last one.
        if (await recordings.TryMarkStoppingAsync(r.Id, ct)) r.MarkStopping();
        host.RequestStop(r.Id);
        return Result.Success(r.ToDto());
    }
}

// ── Reads ────────────────────────────────────────────────────────────

public record GetRecordingQuery(Guid Id) : IRequest<Result<RecordingDto>>;

public class GetRecordingHandler(IRecordingRepository recordings, ICurrentUser me)
    : IRequestHandler<GetRecordingQuery, Result<RecordingDto>>
{
    public async Task<Result<RecordingDto>> Handle(GetRecordingQuery q, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<RecordingDto>(Error.Validation("Not authenticated."));
        var r = await recordings.FindByIdAsync(q.Id, me.UserId.Value, ct);
        return r is null ? Result.Failure<RecordingDto>(Error.NotFound("Recording")) : Result.Success(r.ToDto());
    }
}

public record ListRecordingsQuery(Guid SignatureId) : IRequest<Result<IReadOnlyList<RecordingDto>>>;

public class ListRecordingsHandler(IRecordingRepository recordings, ICurrentUser me)
    : IRequestHandler<ListRecordingsQuery, Result<IReadOnlyList<RecordingDto>>>
{
    public async Task<Result<IReadOnlyList<RecordingDto>>> Handle(ListRecordingsQuery q, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<IReadOnlyList<RecordingDto>>(Error.Validation("Not authenticated."));
        var list = await recordings.ListBySignatureAsync(q.SignatureId, me.UserId.Value, ct);
        return Result.Success<IReadOnlyList<RecordingDto>>(list.Select(r => r.ToDto()).ToList());
    }
}

public record ListRecordingEventsQuery(Guid Id, int AfterSeq, int Take) : IRequest<Result<IReadOnlyList<RecordingEventDto>>>;

public class ListRecordingEventsHandler(IRecordingRepository recordings, ICurrentUser me)
    : IRequestHandler<ListRecordingEventsQuery, Result<IReadOnlyList<RecordingEventDto>>>
{
    public async Task<Result<IReadOnlyList<RecordingEventDto>>> Handle(ListRecordingEventsQuery q, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<IReadOnlyList<RecordingEventDto>>(Error.Validation("Not authenticated."));
        var r = await recordings.FindByIdAsync(q.Id, me.UserId.Value, ct);
        if (r is null) return Result.Failure<IReadOnlyList<RecordingEventDto>>(Error.NotFound("Recording"));
        var take = Math.Clamp(q.Take <= 0 ? 200 : q.Take, 1, 500);
        var events = await recordings.ListEventsAsync(r.Id, q.AfterSeq, take, ct);
        return Result.Success<IReadOnlyList<RecordingEventDto>>(events
            .Select(e => new RecordingEventDto(e.Seq, e.TMs, e.Kind, e.ActionId, e.PayloadJson)).ToList());
    }
}

public record GetRecordingEpisodesQuery(Guid Id) : IRequest<Result<RecordingEpisodesDto>>;

public class GetRecordingEpisodesHandler(IRecordingRepository recordings, ICurrentUser me)
    : IRequestHandler<GetRecordingEpisodesQuery, Result<RecordingEpisodesDto>>
{
    public async Task<Result<RecordingEpisodesDto>> Handle(GetRecordingEpisodesQuery q, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<RecordingEpisodesDto>(Error.Validation("Not authenticated."));
        var r = await recordings.FindByIdAsync(q.Id, me.UserId.Value, ct);
        if (r is null) return Result.Failure<RecordingEpisodesDto>(Error.NotFound("Recording"));
        return Result.Success(new RecordingEpisodesDto(r.Id, r.EpisodesJson));
    }
}

// ── Update / Delete ──────────────────────────────────────────────────

public record UpdateRecordingCommand(Guid Id, string? Label, bool? UseForGrounding, string? Intent = null) : IRequest<Result<RecordingDto>>;

public class UpdateRecordingValidator : AbstractValidator<UpdateRecordingCommand>
{
    public UpdateRecordingValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Label).MaximumLength(Mazda.Domain.Modules.Recordings.RecordingSession.MaxLabel);
        RuleFor(x => x.Intent).MaximumLength(2000);
    }
}

public class UpdateRecordingHandler(IRecordingRepository recordings, ICurrentUser me, IUnitOfWork uow)
    : IRequestHandler<UpdateRecordingCommand, Result<RecordingDto>>
{
    public async Task<Result<RecordingDto>> Handle(UpdateRecordingCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<RecordingDto>(Error.Validation("Not authenticated."));
        var r = await recordings.FindByIdAsync(cmd.Id, me.UserId.Value, ct);
        if (r is null) return Result.Failure<RecordingDto>(Error.NotFound("Recording"));
        if (cmd.Label is not null) r.Rename(cmd.Label);
        if (cmd.UseForGrounding is { } g) r.SetUseForGrounding(g);
        if (cmd.Intent is not null) r.SetIntent(cmd.Intent);
        await uow.SaveChangesAsync(ct);
        return Result.Success(r.ToDto());
    }
}

public record DeleteRecordingCommand(Guid Id) : IRequest<Result>;

public class DeleteRecordingHandler(IRecordingRepository recordings, IRecordingHost host, ICurrentUser me, IUnitOfWork uow)
    : IRequestHandler<DeleteRecordingCommand, Result>
{
    public async Task<Result> Handle(DeleteRecordingCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure(Error.Validation("Not authenticated."));
        var r = await recordings.FindByIdAsync(cmd.Id, me.UserId.Value, ct);
        if (r is null) return Result.Failure(Error.NotFound("Recording"));
        if (r.IsActive && host.IsActive(r.Id))
            return Result.Failure(Error.Validation("Stop the recording before deleting it."));
        await recordings.RemoveAsync(r, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ── Analyze (LLM) ────────────────────────────────────────────────────

public record AnalyzeRecordingCommand(Guid Id) : IRequest<Result<RecordingProposalDto>>;

/// <summary>
/// Analyze several (default: all completed) recordings of a signature in ONE
/// analyzer call, producing one consolidated proposal. With <c>Fresh</c> the
/// analyzer ignores elements previously learned from recordings, so applying
/// with <c>replaceLearned</c> rebuilds them instead of piling up variants.
/// </summary>
public record AnalyzeSignatureRecordingsCommand(Guid SignatureId, IReadOnlyList<Guid>? RecordingIds = null, bool Fresh = false)
    : IRequest<Result<RecordingProposalDto>>;

public class AnalyzeRecordingHandler(ISender sender, IRecordingRepository recordings, ICurrentUser me)
    : IRequestHandler<AnalyzeRecordingCommand, Result<RecordingProposalDto>>
{
    public async Task<Result<RecordingProposalDto>> Handle(AnalyzeRecordingCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<RecordingProposalDto>(Error.Validation("Not authenticated."));
        var r = await recordings.FindByIdAsync(cmd.Id, me.UserId.Value, ct);
        if (r is null) return Result.Failure<RecordingProposalDto>(Error.NotFound("Recording"));
        return await sender.Send(new AnalyzeSignatureRecordingsCommand(r.SignatureId, new[] { r.Id }), ct);
    }
}

public class AnalyzeSignatureRecordingsHandler(
    IRecordingRepository recordings,
    ISignatureRepository signatures,
    IRecordingAnalyzer analyzer,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<AnalyzeSignatureRecordingsCommand, Result<RecordingProposalDto>>
{
    /// <summary>Episode characters sent in one analysis (~60–70k tokens).</summary>
    public const int AnalyzeBudgetChars = 240_000;

    public async Task<Result<RecordingProposalDto>> Handle(AnalyzeSignatureRecordingsCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<RecordingProposalDto>(Error.Validation("Not authenticated."));
        var owner = me.UserId.Value;
        var sig = await signatures.FindByIdAsync(cmd.SignatureId, owner, ct);
        if (sig is null) return Result.Failure<RecordingProposalDto>(Error.NotFound("Signature"));

        var all = await recordings.ListBySignatureAsync(sig.Id, owner, ct);
        var picked = all
            .Where(r => cmd.RecordingIds is null || cmd.RecordingIds.Contains(r.Id))
            .Where(r => r.Status == RecordingStatus.Completed && r.ActionCount > 0 && !string.IsNullOrWhiteSpace(r.EpisodesJson))
            .OrderBy(r => r.CreatedAt)
            .ToList();
        if (picked.Count == 0)
            return Result.Failure<RecordingProposalDto>(Error.Validation(
                cmd.RecordingIds is { Count: 1 }
                    ? "This recording isn't Completed or captured no interactions."
                    : "No completed recordings with interactions to analyze."));

        // Total prompt budget: each session is ≤60k chars of episodes, so
        // "analyze all" over many sessions would overflow the model's context.
        // Keep the newest sessions that fit (the newest always does) and say
        // which were left out.
        var skipped = 0;
        if (picked.Sum(r => r.EpisodesJson.Length) > AnalyzeBudgetChars)
        {
            var kept = new List<Mazda.Domain.Modules.Recordings.RecordingSession>();
            var total = 0;
            foreach (var r in Enumerable.Reverse(picked))
            {
                if (kept.Count > 0 && total + r.EpisodesJson.Length > AnalyzeBudgetChars) { skipped++; continue; }
                kept.Add(r);
                total += r.EpisodesJson.Length;
            }
            kept.Reverse();
            picked = kept;
        }

        // Elements the analyzer should treat as known. Fresh = forget what
        // earlier recordings taught; source-derived and manual ones stay.
        var known = sig.Elements
            .Where(e => !cmd.Fresh || e.Origin != Mazda.Domain.Modules.Signatures.ElementOrigin.Recorded)
            .Select(e => e.ElementSelector)
            .ToList();

        RecordingAnalysisResult result;
        try
        {
            result = await analyzer.AnalyzeAsync(
                sig,
                picked.Select(r => new RecordedSessionInput(r.Id, r.LabelIsAuto ? "" : r.Label, r.Intent, r.EpisodesJson)).ToList(),
                cmd.Fresh,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<RecordingProposalDto>(Error.Validation($"Recording analyzer returned invalid output: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return Result.Failure<RecordingProposalDto>(Error.External($"Recording analyzer failed: {ExceptionMessage.Describe(ex)}"));
        }

        var recordedKnown = sig.Elements
            .Where(e => e.Origin == Mazda.Domain.Modules.Signatures.ElementOrigin.Recorded)
            .Select(e => e.ElementSelector).ToHashSet(StringComparer.Ordinal);
        var validated = ProposalValidator.Validate(result, picked.Select(r => r.EpisodesJson), known, recordedKnown)
            with { SessionIds = picked.Select(r => r.Id).ToList(), Fresh = cmd.Fresh };
        if (skipped > 0)
            validated = validated with
            {
                Notes = $"{skipped} older recording(s) were left out to fit the analyzer's input limit; " +
                        "analyze them separately or delete ones you no longer need.\n" + validated.Notes,
            };

        // Each session's readback: what the analyzer understood + its title.
        foreach (var u in validated.Sessions ?? Array.Empty<SessionUnderstanding>())
            picked.FirstOrDefault(r => r.Id == u.SessionId)?.SetUnderstanding(u.Title, u.Understanding);

        // Stored on the newest analyzed session; apply goes through it.
        var host = picked[^1];
        host.SetProposal(JsonSerializer.Serialize(validated));
        await uow.SaveChangesAsync(ct);
        return Result.Success(ProposalValidator.ToDto(host.Id, validated, known));
    }
}

/// <summary>
/// Server-side guard on the analyzer's output. The prompt tells the model to
/// pick selectors verbatim from what was observed; this enforces it, dropping
/// (not failing on) anything invented — a hallucinated selector in the
/// signature would surface much later as a function that can never run.
/// </summary>
public static class ProposalValidator
{
    public static RecordingAnalysisResult Validate(
        RecordingAnalysisResult result, string episodesJson, IEnumerable<string> existingSelectors)
        => Validate(result, new[] { episodesJson }, existingSelectors);

    /// <summary>
    /// Keeps only grounded elements. A selector is grounded when it was
    /// observed (a candidate, a collection item, an outline path under its
    /// root or collection item), already exists in the signature, or is a
    /// combination of such pieces (descendant, child or <c>&gt;&gt;</c> chain).
    /// An ungrounded selector whose <c>matchesExisting</c> resolves is
    /// REWRITTEN to that existing selector — it never survives as itself, so
    /// apply can't later turn an invented selector into a new element.
    /// </summary>
    public static RecordingAnalysisResult Validate(
        RecordingAnalysisResult result, IEnumerable<string> episodesJsons, IEnumerable<string> existingSelectors,
        IReadOnlySet<string>? recordedExisting = null)
    {
        var docs = episodesJsons.ToList();
        var existing = existingSelectors.ToHashSet(StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var relative = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in docs) Collect(doc, observed, relative);
        var known = new HashSet<string>(observed, StringComparer.Ordinal);
        known.UnionWith(existing);
        var episodes = IndexEpisodes(docs);

        var elements = new List<ProposedElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in result.Elements)
        {
            if (string.IsNullOrWhiteSpace(e.Selector)) continue;
            var match = e.MatchesExisting is { } m && existing.Contains(m) ? m : null;
            var targets = EvidenceTargets(e, episodes);
            var grounded = IsGrounded(e.Selector, known, relative);

            // A learned element can only absorb this proposal if the user
            // actually acted on IT in the evidence. Otherwise the model is
            // pairing sibling controls ("close icon = the pencil element we
            // learned earlier") and the merge would keep the wrong selector.
            if (match is not null && grounded && recordedExisting is not null && recordedExisting.Contains(match)
                && match != e.Selector && !Exercised(match, targets))
                match = null;

            ProposedElement kept;
            if (grounded) kept = e with { MatchesExisting = match };
            else if (match is not null) kept = e with { Selector = match, MatchesExisting = match };
            else continue;

            var interactive = string.Equals(kept.Category, "interactive", StringComparison.OrdinalIgnoreCase);
            kept = kept with { Untried = interactive && !Exercised(kept.Selector, targets) };
            if (seen.Add(kept.Selector)) elements.Add(kept);
        }

        var flowSelectors = existing.Concat(elements.Select(e => e.Selector)).ToHashSet(StringComparer.Ordinal);
        var (flows, _, _) = RecordingMerge.MergeFlows(null, result.FlowsJson ?? "{}", flowSelectors);
        return result with { Elements = elements, FlowsJson = flows };
    }

    // ── What was acted on ─────────────────────────────────────────────

    /// <summary>(recording index, episode i) → episode object.</summary>
    private static Dictionary<(int, int), JsonObject> IndexEpisodes(IReadOnlyList<string> docs)
    {
        var map = new Dictionary<(int, int), JsonObject>();
        for (var r = 0; r < docs.Count; r++)
        {
            JsonNode? root;
            try { root = JsonNode.Parse(docs[r]); } catch (JsonException) { continue; }
            foreach (var ep in (root?["episodes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (EpisodeBuilder.Int(ep["i"]) is { } i) map[(r, i)] = ep;
        }
        return map;
    }

    /// <summary>Targets (and drag destinations) of the element's evidence episodes.</summary>
    private static List<JsonObject> EvidenceTargets(ProposedElement e, Dictionary<(int, int), JsonObject> episodes)
    {
        var eps = new List<JsonObject>();
        if (e.EvidenceRefs is { Count: > 0 } refs)
        {
            foreach (var r in refs)
            {
                var parts = r.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out var ri) && int.TryParse(parts[1], out var ei)
                    && episodes.TryGetValue((ri, ei), out var ep)) eps.Add(ep);
            }
        }
        else
        {
            // Legacy proposals: bare episode numbers, ambiguous across recordings.
            foreach (var i in e.Evidence)
                eps.AddRange(episodes.Where(kv => kv.Key.Item2 == i).Select(kv => kv.Value));
        }
        var targets = new List<JsonObject>();
        foreach (var ep in eps)
        {
            if (EpisodeBuilder.Str(ep["action"]) is "hover" or "unhover" or "scroll") continue;   // not acting on a control
            if (ep["target"] is JsonObject t) targets.Add(t);
            if (ep["dest"] is JsonObject d) targets.Add(d);
        }
        return targets;
    }

    /// <summary>
    /// Was <paramref name="selector"/> the thing acted on (or a container of
    /// it) in one of these targets? Exact match against the target's
    /// selectors, else its LAST compound must fit a compound of the target's
    /// own paths or ancestors: every tag / id / class / attribute / pseudo it
    /// names must be present there. `…morva-icons.applied-column-icon` does
    /// not fit a click on `morva-icons.cursor-pointer.icon-12[icon="close"]`.
    /// </summary>
    public static bool Exercised(string selector, IReadOnlyList<JsonObject> targets)
    {
        if (targets.Count == 0) return false;
        var sel = selector.Trim();
        var bounds = TokenBounds(sel);
        var leaf = bounds.Count == 0 ? null : Compound.Parse(sel[bounds[^1].Start..bounds[^1].End]);
        var engineSel = sel.StartsWith("role=", StringComparison.Ordinal) || sel.StartsWith("text=", StringComparison.Ordinal);
        foreach (var t in targets)
        {
            var paths = new List<string>();
            if (EpisodeBuilder.Str(t["best"]) is { } b) paths.Add(b);
            foreach (var c in (t["candidates"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                if (EpisodeBuilder.Str(c["selector"]) is { } cs) paths.Add(cs);
            if (t["collection"] is JsonObject col && EpisodeBuilder.Str(col["item"]) is { } item)
            {
                paths.Add(item);
                if (EpisodeBuilder.Str(col["inner"]) is { } inner) paths.Add($"{item} {inner}");
            }
            if (paths.Contains(sel, StringComparer.Ordinal)) return true;
            if (leaf is null || engineSel) continue;

            var compounds = new List<Compound>();
            foreach (var p in paths)
            {
                if (p.StartsWith("role=", StringComparison.Ordinal) || p.StartsWith("text=", StringComparison.Ordinal)) continue;
                foreach (var (s0, e0) in TokenBounds(p)) compounds.Add(Compound.Parse(p[s0..e0]));
            }
            compounds.Add(Compound.FromDescriptor(t));
            foreach (var a in (t["ancestors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                compounds.Add(Compound.FromDescriptor(a));
            if (compounds.Any(c => leaf.FitsIn(c))) return true;
        }
        return false;
    }

    /// <summary>One compound selector (`tag#id.a.b[k="v"]:nth-of-type(2)`) as sets.</summary>
    private sealed class Compound
    {
        public string? Tag, Id;
        public readonly HashSet<string> Classes = new(StringComparer.Ordinal);
        public readonly HashSet<string> Attrs = new(StringComparer.Ordinal);    // "k=v" or "k"
        public readonly HashSet<string> Pseudos = new(StringComparer.Ordinal);

        private static readonly System.Text.RegularExpressions.Regex Part = new(
            @"\G(?:[a-zA-Z][\w-]*|\*|#[\w-]+|\.[\w-]+|\[[^\]]+\]|::?[\w-]+(?:\([^)]*\))?)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        public static Compound Parse(string text)
        {
            var c = new Compound();
            var s = text.Trim().TrimStart('>', '+', '~').Trim();
            var pos = 0;
            while (pos < s.Length)
            {
                var m = Part.Match(s, pos);
                if (!m.Success || m.Length == 0) { pos++; continue; }
                var v = m.Value;
                if (v[0] == '#') c.Id = v[1..];
                else if (v[0] == '.') c.Classes.Add(v[1..]);
                else if (v[0] == '[') c.Attrs.Add(NormAttr(v[1..^1]));
                else if (v[0] == ':') c.Pseudos.Add(v);
                else if (v != "*") c.Tag = v.ToLowerInvariant();
                pos += v.Length;
            }
            return c;
        }

        private static string NormAttr(string a)
        {
            var eq = a.IndexOf('=');
            return eq < 0 ? a.Trim() : a[..eq].Trim() + "=" + a[(eq + 1)..].Trim().Trim('"', '\'');
        }

        /// <summary>From a recorder descriptor (target or ancestor): tag, classes, attrs.</summary>
        public static Compound FromDescriptor(JsonObject d)
        {
            var c = new Compound { Tag = EpisodeBuilder.Str(d["tag"])?.ToLowerInvariant() };
            foreach (var cl in (d["classes"] as JsonArray ?? new JsonArray()))
                if (EpisodeBuilder.Str(cl) is { } x) c.Classes.Add(x);
            if (d["attrs"] is JsonObject at)
                foreach (var (k, v) in at)
                {
                    if (k == "id" && EpisodeBuilder.Str(v) is { } id) c.Id = id;
                    else if (EpisodeBuilder.Str(v) is { } val) c.Attrs.Add($"{k}={val}");
                }
            return c;
        }

        public bool FitsIn(Compound t)
        {
            // An empty compound (bare combinator) fits nothing.
            if (Tag is null && Id is null && Classes.Count == 0 && Attrs.Count == 0) return false;
            if (Tag is not null && Tag != t.Tag) return false;
            if (Id is not null && Id != t.Id) return false;
            if (!Classes.IsSubsetOf(t.Classes)) return false;
            if (!Pseudos.IsSubsetOf(t.Pseudos)) return false;
            foreach (var a in Attrs)
                if (!t.Attrs.Contains(a) && !(a.IndexOf('=') < 0 && t.Attrs.Any(x => x.StartsWith(a + "=", StringComparison.Ordinal))))
                    return false;
            return true;
        }
    }

    /// <summary>
    /// True when the selector, split at combinators outside quotes / brackets,
    /// can be covered by known pieces. <paramref name="suffixOnly"/> pieces
    /// (row-relative outline paths such as <c>button</c> or <c>span.title</c>)
    /// only count AFTER a known anchor — alone they match anything.
    /// </summary>
    public static bool IsGrounded(string selector, IReadOnlySet<string> known, IReadOnlySet<string>? suffixOnly = null)
    {
        var sel = selector.Trim();
        if (known.Contains(sel)) return true;
        var bounds = TokenBounds(sel);
        if (bounds.Count < 2) return false;
        var memo = new Dictionary<int, bool>();
        bool From(int i)
        {
            if (i == bounds.Count) return true;
            if (memo.TryGetValue(i, out var r)) return r;
            r = false;
            for (var j = i; j < bounds.Count && !r; j++)
            {
                var piece = sel[bounds[i].Start..bounds[j].End].Trim();
                var ok = known.Contains(piece) || (i > 0 && suffixOnly is not null && suffixOnly.Contains(piece));
                if (ok && From(j + 1)) r = true;
            }
            memo[i] = r;
            return r;
        }
        return From(0);
    }

    /// <summary>Compound-selector token spans, split at ' ', '>', '>>' outside quotes, brackets and parentheses.</summary>
    private static List<(int Start, int End)> TokenBounds(string sel)
    {
        var spans = new List<(int, int)>();
        int depth = 0, start = -1;
        char quote = '\0';
        for (var i = 0; i < sel.Length; i++)
        {
            var c = sel[i];
            if (quote != '\0')
            {
                if (c == '\\') { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; if (start < 0) start = i; continue; }
            if (c is '[' or '(') depth++;
            else if (c is ']' or ')') depth--;
            var sep = depth == 0 && (c == ' ' || c == '>');
            if (sep)
            {
                if (start >= 0) { spans.Add((start, i)); start = -1; }
            }
            else if (start < 0) start = i;
        }
        if (start >= 0) spans.Add((start, sel.Length));
        return spans;
    }

    public static RecordingProposalDto ToDto(Guid recordingId, RecordingAnalysisResult p, IEnumerable<string> existingSelectors)
    {
        var existing = existingSelectors.ToHashSet(StringComparer.Ordinal);
        return new RecordingProposalDto(
            recordingId,
            p.Elements.Select(e => new ProposedElementDto(
                e.Selector, e.Category, e.ElementType, e.Description, e.Logic, e.MatchesExisting, e.Evidence,
                IsNew: !existing.Contains(e.Selector) && (e.MatchesExisting is null || !existing.Contains(e.MatchesExisting)),
                Untried: e.Untried))
             .ToList(),
            p.FlowsJson, p.Notes, p.TokensUsed, p.SessionIds ?? new[] { recordingId });
    }

    /// <summary>
    /// Every selector the recorder actually observed: candidates and best
    /// picks, collection item selectors (and item + inner path), outline
    /// paths both bare (row-relative, for <c>row.locator(...)</c>) and
    /// anchored under their element's best selector / collection item.
    /// </summary>
    public static HashSet<string> ObservedSelectors(string episodesJson)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        Collect(episodesJson, set, new HashSet<string>(StringComparer.Ordinal));
        return set;
    }

    /// <summary>Fills <paramref name="set"/> with standalone selectors and <paramref name="relative"/> with bare row-relative paths.</summary>
    private static void Collect(string episodesJson, HashSet<string> set, HashSet<string> relative)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(episodesJson); } catch (JsonException) { return; }

        void AddOutline(JsonArray outline, IEnumerable<string?> bases)
        {
            foreach (var item in outline.OfType<JsonObject>())
            {
                if (EpisodeBuilder.Str(item["sel"]) is not { Length: > 0 } rel) continue;
                relative.Add(rel);
                foreach (var b in bases)
                    if (!string.IsNullOrEmpty(b)) set.Add($"{b} {rel}");
            }
        }

        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                {
                    var best = EpisodeBuilder.Str(o["best"]);
                    string? colItem = null;
                    if (o["collection"] is JsonObject col && EpisodeBuilder.Str(col["item"]) is { } item)
                    {
                        colItem = item;
                        set.Add(item);
                        if (EpisodeBuilder.Str(col["inner"]) is { } inner) { relative.Add(inner); set.Add($"{item} {inner}"); }
                        if (col["itemOutline"] is JsonArray io) AddOutline(io, new[] { item });
                    }
                    if (o["outline"] is JsonArray ol) AddOutline(ol, new[] { best, colItem });
                    // itemChanges rows: {item, sel, ...}
                    if (EpisodeBuilder.Str(o["item"]) is { } chItem && EpisodeBuilder.Str(o["sel"]) is { Length: > 0 } chSel)
                    { set.Add(chItem); relative.Add(chSel); set.Add($"{chItem} {chSel}"); }
                    foreach (var (k, v) in o)
                    {
                        if (k is "selector" or "best" && EpisodeBuilder.Str(v) is { } s) set.Add(s);
                        else Walk(v);
                    }
                    break;
                }
                case JsonArray a:
                    foreach (var c in a) Walk(c);
                    break;
            }
        }
        Walk(root);
    }
}

// ── Apply ────────────────────────────────────────────────────────────

public record ApplyRecordingCommand(
    Guid Id,
    IReadOnlyList<string> AcceptedSelectors,
    bool EnrichExisting = true,
    bool ApplyFlows = true,
    /// Replace elements learned from earlier recordings with this proposal's
    /// (only for a proposal analyzed with Fresh).
    bool ReplaceLearned = false) : IRequest<Result<ApplyRecordingResultDto>>;

public class ApplyRecordingHandler(
    IRecordingRepository recordings,
    ISignatureRepository signatures,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<ApplyRecordingCommand, Result<ApplyRecordingResultDto>>
{
    public async Task<Result<ApplyRecordingResultDto>> Handle(ApplyRecordingCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<ApplyRecordingResultDto>(Error.Validation("Not authenticated."));
        var owner = me.UserId.Value;
        var r = await recordings.FindByIdAsync(cmd.Id, owner, ct);
        if (r is null) return Result.Failure<ApplyRecordingResultDto>(Error.NotFound("Recording"));
        if (string.IsNullOrWhiteSpace(r.ProposalJson))
            return Result.Failure<ApplyRecordingResultDto>(Error.Validation("Analyze the recording before applying it."));

        RecordingAnalysisResult proposal;
        try { proposal = JsonSerializer.Deserialize<RecordingAnalysisResult>(r.ProposalJson)!; }
        catch (Exception ex) { return Result.Failure<ApplyRecordingResultDto>(Error.Validation($"Stored proposal is unreadable: {ex.Message}")); }

        var sig = await signatures.FindByIdAsync(r.SignatureId, owner, ct);
        if (sig is null) return Result.Failure<ApplyRecordingResultDto>(Error.NotFound("Signature"));
        var hadFunctions = sig.Functions.Count > 0;
        if (cmd.ReplaceLearned && !proposal.Fresh)
            return Result.Failure<ApplyRecordingResultDto>(Error.Validation(
                "Replacing learned elements needs a proposal analyzed from scratch (fresh)."));

        var merge = RecordingMerge.Merge(
            sig, proposal.Elements, proposal.FlowsJson,
            cmd.AcceptedSelectors.ToHashSet(StringComparer.Ordinal),
            cmd.EnrichExisting, cmd.ApplyFlows, cmd.ReplaceLearned);

        // Additive apply: existing selectors all survive, so the function
        // catalogue and relationship graph stay. Replace-learned removes
        // selectors, so the same policy invalidates them (flows are re-set
        // below from the proposal). ReplaceElementsAsync clears the change
        // tracker, so sessions are re-loaded afterwards.
        // One transaction: ReplaceElementsAsync deletes the old rows with
        // immediate SQL, so a failing insert must roll that delete back
        // instead of leaving the signature with no elements.
        await uow.InTransactionAsync(async tct =>
        {
            await signatures.ReplaceElementsAsync(sig, merge.Elements, ElementChangePolicy.InvalidateOnlyIfExistingSelectorsChanged, tct);
            if (merge.FlowsJson is not null) sig.SetUserFlows(merge.FlowsJson);

            foreach (var sid in (proposal.SessionIds ?? Array.Empty<Guid>()).Append(cmd.Id).Distinct())
                (await recordings.FindByIdAsync(sid, owner, tct))?.MarkApplied();
            return await uow.SaveChangesAsync(tct);
        }, ct);

        var changed = merge.AddedElements > 0 || merge.EnrichedElements > 0 || merge.RemovedElements > 0;
        return Result.Success(new ApplyRecordingResultDto(
            sig.ToDto(), merge.AddedElements, merge.EnrichedElements, merge.AddedNodes, merge.AddedEdges,
            FunctionsStale: hadFunctions && changed,
            RemovedElements: merge.RemovedElements));
    }
}
