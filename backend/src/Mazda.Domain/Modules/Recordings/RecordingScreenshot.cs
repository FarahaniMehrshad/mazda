namespace Mazda.Domain.Modules.Recordings;

/// <summary>
/// The page right after one demonstrated action settled (JPEG, base64).
/// Shown next to a replayed function's result so the author can compare
/// "what I did" with "what the function did" at a glance. Deleted with the
/// recording.
/// </summary>
public class RecordingScreenshot
{
    public Guid SessionId { get; private set; }
    /// <summary>The recorder's action id, with characters outside [A-Za-z0-9_-] replaced by '_'.</summary>
    public string ActionKey { get; private set; } = null!;
    public string JpegB64 { get; private set; } = string.Empty;

    private RecordingScreenshot() { }

    public RecordingScreenshot(Guid sessionId, string actionKey, string jpegB64)
    {
        SessionId = sessionId;
        ActionKey = actionKey;
        JpegB64 = jpegB64;
    }

    public static string KeyFor(string actionId)
        => new(actionId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
}
