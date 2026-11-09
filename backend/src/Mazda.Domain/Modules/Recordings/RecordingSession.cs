using Mazda.Domain.Common;

namespace Mazda.Domain.Modules.Recordings;

public enum RecordingStatus
{
    /// <summary>The recorder process is attached and streaming events.</summary>
    Recording,
    /// <summary>Stop was requested; waiting for the recorder to flush and exit.</summary>
    Stopping,
    /// <summary>Recorder exited cleanly; episodes are built and the session can be analyzed.</summary>
    Completed,
    /// <summary>Recorder crashed, could not attach, or the API restarted mid-session.</summary>
    Failed,
}

/// <summary>
/// One "learn by watching" session: MAZDA attached to the user's Chrome tab
/// and recorded what they did (input events) and what the page did in
/// response (DOM mutations, network, navigation).
///
/// The raw event log lives in <see cref="RecordingEvent"/> rows (append-only,
/// streamed in while recording). At stop time it is condensed into
/// <see cref="EpisodesJson"/> — action → effect episodes — which is what the
/// recording analyzer and the function-catalogue generator consume.
/// </summary>
public class RecordingSession : Entity
{
    public Guid OwnerId { get; private set; }
    public Guid SignatureId { get; private set; }
    /// <summary>Display name. Auto (timestamp, then the analyzer's title) until the user renames it.</summary>
    public string Label { get; private set; } = string.Empty;
    /// <summary>True while <see cref="Label"/> is machine-chosen and may be replaced by the analyzer's title.</summary>
    public bool LabelIsAuto { get; private set; } = true;
    /// <summary>Optional: what the user says they were demonstrating. Given to the analyzer as the session's goal.</summary>
    public string Intent { get; private set; } = string.Empty;
    /// <summary>The analyzer's account of what this session shows — a readback the user checks against their intent.</summary>
    public string Understanding { get; private set; } = string.Empty;
    public RecordingStatus Status { get; private set; } = RecordingStatus.Recording;
    public DateTime StartedAt { get; private set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; private set; }
    public string StartUrl { get; private set; } = string.Empty;
    /// <summary>JSON array of origins (scheme://host:port) the recorder may observe.</summary>
    public string AllowedOriginsJson { get; private set; } = "[]";
    public bool CaptureBodies { get; private set; } = true;
    public bool MaskAllInputs { get; private set; }
    public int CdpPort { get; private set; }
    public string TargetId { get; private set; } = string.Empty;
    public int EventCount { get; private set; }
    public int ActionCount { get; private set; }
    /// <summary>A size / event / duration cap was hit, or the episode summary had to be trimmed.</summary>
    public bool Truncated { get; private set; }
    /// <summary>Failure reason, or a note on how a completed session ended (e.g. tab closed).</summary>
    public string Error { get; private set; } = string.Empty;
    public string EpisodesJson { get; private set; } = string.Empty;
    /// <summary>Last analyzer proposal (elements + flows), awaiting review / apply.</summary>
    public string ProposalJson { get; private set; } = string.Empty;
    public DateTime? AppliedAt { get; private set; }
    /// <summary>Feed this session's episodes to the function-catalogue generator.</summary>
    public bool UseForGrounding { get; private set; } = true;

    public const int MaxLabel = 200;
    public const int MaxUrl = 2000;

    private RecordingSession() { }

    public static RecordingSession Start(
        Guid ownerId, Guid signatureId, string? label, string? intent, string startUrl,
        string allowedOriginsJson, bool captureBodies, bool maskAllInputs,
        int cdpPort, string targetId)
    {
        if (ownerId == Guid.Empty) throw new ArgumentException("ownerId required.");
        if (signatureId == Guid.Empty) throw new ArgumentException("signatureId required.");
        return new RecordingSession
        {
            OwnerId = ownerId,
            SignatureId = signatureId,
            Label = string.IsNullOrWhiteSpace(label) ? $"Recording {DateTime.UtcNow:yyyy-MM-dd HH:mm}" : label.Trim(),
            LabelIsAuto = string.IsNullOrWhiteSpace(label),
            Intent = intent?.Trim() ?? string.Empty,
            // Column is 2000 chars; deep links with long state can exceed it.
            StartUrl = startUrl is { Length: > MaxUrl } ? startUrl[..(MaxUrl - 1)] + "…" : startUrl ?? string.Empty,
            AllowedOriginsJson = string.IsNullOrWhiteSpace(allowedOriginsJson) ? "[]" : allowedOriginsJson,
            CaptureBodies = captureBodies,
            MaskAllInputs = maskAllInputs,
            CdpPort = cdpPort,
            TargetId = targetId ?? string.Empty,
        };
    }

    public bool IsActive => Status is RecordingStatus.Recording or RecordingStatus.Stopping;

    /// <summary>User rename: from now on the analyzer's title no longer replaces it.</summary>
    public void Rename(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return;
        Label = label.Trim().Length > MaxLabel ? label.Trim()[..MaxLabel] : label.Trim();
        LabelIsAuto = false;
        Touch();
    }

    public void SetIntent(string? intent) { Intent = intent?.Trim() ?? string.Empty; Touch(); }

    /// <summary>Record what the analyzer understood; adopt its title unless the user named the session.</summary>
    public void SetUnderstanding(string? title, string? understanding)
    {
        Understanding = understanding?.Trim() ?? string.Empty;
        if (LabelIsAuto && !string.IsNullOrWhiteSpace(title))
            Label = title.Trim().Length > 200 ? title.Trim()[..200] : title.Trim();
        Touch();
    }

    public void SetUseForGrounding(bool value) { UseForGrounding = value; Touch(); }

    public void MarkStopping()
    {
        if (Status == RecordingStatus.Recording) Status = RecordingStatus.Stopping;
        Touch();
    }

    public void MarkCompleted(string episodesJson, int eventCount, int actionCount, bool truncated, string? note)
    {
        Status = RecordingStatus.Completed;
        EpisodesJson = episodesJson ?? string.Empty;
        EventCount = eventCount;
        ActionCount = actionCount;
        Truncated = truncated;
        Error = note ?? string.Empty;
        EndedAt = DateTime.UtcNow;
        Touch();
    }

    public void MarkFailed(string message, int eventCount = 0, int actionCount = 0, string? episodesJson = null)
    {
        Status = RecordingStatus.Failed;
        Error = message ?? "Recording failed.";
        EventCount = eventCount;
        ActionCount = actionCount;
        if (episodesJson is not null) EpisodesJson = episodesJson;
        EndedAt = DateTime.UtcNow;
        Touch();
    }

    public void SetProposal(string proposalJson) { ProposalJson = proposalJson ?? string.Empty; Touch(); }

    public void MarkApplied() { AppliedAt = DateTime.UtcNow; Touch(); }
}

/// <summary>
/// One raw event from the recorder, persisted as it streams in. Payload is the
/// full event JSON (already masked page-side and sanitized server-side),
/// stored as text — we never query into it from SQL.
/// </summary>
public class RecordingEvent
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid SessionId { get; private set; }
    public int Seq { get; private set; }
    public long TMs { get; private set; }
    public string Kind { get; private set; } = null!;
    public string? ActionId { get; private set; }
    public string PayloadJson { get; private set; } = "{}";

    private RecordingEvent() { }

    public RecordingEvent(Guid sessionId, int seq, long tMs, string kind, string? actionId, string payloadJson)
    {
        SessionId = sessionId;
        Seq = seq;
        TMs = tMs;
        Kind = string.IsNullOrWhiteSpace(kind) ? "unknown" : (kind.Length > 20 ? kind[..20] : kind);
        ActionId = string.IsNullOrWhiteSpace(actionId) ? null : (actionId.Length > 64 ? actionId[..64] : actionId);
        PayloadJson = payloadJson ?? "{}";
    }
}
