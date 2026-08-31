using Mazda.Domain.Common;

namespace Mazda.Domain.Modules.Identity;

/// <summary>
/// Per-user preferences. Today that means one thing: which Chrome tab MAZDA
/// drives over CDP.
///
/// Before this existed, every harness took <c>contexts()[0].pages()[0]</c> —
/// the first target Chrome happened to report, which with more than one tab
/// open is arbitrary and is not the focused tab. Attaching explicitly makes
/// the choice visible and stable across gather, execute and function tests.
///
/// One row per user, keyed by <see cref="UserId"/>. The row is created lazily
/// on first read, so a user who never opens the CDP panel never gets one.
/// </summary>
public class UserSettings : Entity
{
    public Guid UserId { get; private set; }

    /// <summary>
    /// Port Chrome is listening on for remote debugging. The HOST is not a
    /// setting: the harness always emits <c>localhost:&lt;port&gt;</c> and the
    /// runner rewrites it to reach the container's host — see
    /// <c>LocalProcessScriptRunner.RewriteHosts</c>. Only the port varies in
    /// practice (people who change it usually did so to avoid a clash).
    /// </summary>
    public int CdpPort { get; private set; } = 9222;

    /// <summary>
    /// CDP <c>targetId</c> of the attached tab, or empty for "just take the
    /// first page you find" (the pre-attachment behaviour).
    ///
    /// A targetId survives reloads and navigations but dies with the tab, so
    /// it is validated against a live tab list before use and the harness
    /// fails with a re-pick message rather than silently falling back.
    /// </summary>
    public string AttachedTargetId { get; private set; } = string.Empty;

    /// <summary>
    /// Title and URL captured when the tab was attached. Purely for display —
    /// they let the UI name the attachment even while Chrome is closed and the
    /// live list is unavailable. Never used for matching, which is targetId
    /// only; two tabs can share a URL.
    /// </summary>
    public string AttachedTitle { get; private set; } = string.Empty;
    public string AttachedUrl { get; private set; } = string.Empty;

    private UserSettings() { } // EF

    public static UserSettings CreateDefault(Guid userId) => new() { UserId = userId };

    public void SetCdpPort(int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        CdpPort = port;
        Touch();
    }

    public void AttachTab(string targetId, string? title, string? url)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            throw new ArgumentException("targetId required.", nameof(targetId));
        AttachedTargetId = targetId.Trim();
        AttachedTitle = title?.Trim() ?? string.Empty;
        AttachedUrl = url?.Trim() ?? string.Empty;
        Touch();
    }

    /// <summary>Fall back to "first page found" — the pre-attachment behaviour.</summary>
    public void DetachTab()
    {
        AttachedTargetId = string.Empty;
        AttachedTitle = string.Empty;
        AttachedUrl = string.Empty;
        Touch();
    }
}
