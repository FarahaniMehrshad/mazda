using FluentValidation;
using MediatR;
using Mazda.Application.Common.Abstractions;
using Mazda.Domain.Common;

namespace Mazda.Application.Modules.Cdp;

/// <summary>
/// Which Chrome tab MAZDA drives, and whether it can see one at all.
///
/// Before this existed every harness took the first page Chrome reported —
/// arbitrary once more than one tab is open, and never the focused tab. The
/// attachment is per-user and applies to gather, execute and function tests
/// alike, so there is one place to change it and one answer to "where is it
/// running?".
/// </summary>
public record CdpTabDto(string TargetId, string Title, string Url, bool Attached);

public record CdpStatusDto(
    bool Available,
    string? BrowserVersion,
    /// Why the browser could not be reached — null when Available.
    string? Reason,
    int Port,
    /// Empty when no tab is attached: harnesses then take the first page found.
    string AttachedTargetId,
    /// Title/URL captured at attach time. Survives Chrome being closed, so the
    /// UI can still name the attachment when the live list is empty.
    string AttachedTitle,
    string AttachedUrl,
    /// True when a tab is attached but no longer present in the live list —
    /// the run would fail, so the UI should prompt for a re-pick.
    bool AttachedTabMissing,
    IReadOnlyList<CdpTabDto> Tabs);

// ── Status (probe + list) ────────────────────────────────────────────

public record GetCdpStatusQuery : IRequest<Result<CdpStatusDto>>;

public class GetCdpStatusHandler(
    IUserSettingsRepository settings,
    ICdpClient cdp,
    ICurrentUser me,
    IUnitOfWork uow) : IRequestHandler<GetCdpStatusQuery, Result<CdpStatusDto>>
{
    public async Task<Result<CdpStatusDto>> Handle(GetCdpStatusQuery q, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<CdpStatusDto>(Error.Validation("Not authenticated."));

        var s = await settings.GetOrCreateAsync(me.UserId.Value, ct);
        await uow.SaveChangesAsync(ct);

        var status = await cdp.ProbeAsync(s.CdpPort, ct);
        if (!status.Available)
        {
            // Unreachable is a normal, expected state (Chrome closed, wrong
            // flags) — report it as data, not as a failed request, so the panel
            // can render the reason and the retry button.
            return Result.Success(new CdpStatusDto(
                false, null, status.Reason, s.CdpPort,
                s.AttachedTargetId, s.AttachedTitle, s.AttachedUrl,
                AttachedTabMissing: false,
                Tabs: Array.Empty<CdpTabDto>()));
        }

        IReadOnlyList<CdpTab> tabs;
        try
        {
            tabs = await cdp.ListTabsAsync(s.CdpPort, ct);
        }
        catch (Exception ex)
        {
            return Result.Success(new CdpStatusDto(
                false, status.BrowserVersion,
                $"Chrome answered but the tab list could not be read: {ExceptionMessage.Describe(ex)}",
                s.CdpPort, s.AttachedTargetId, s.AttachedTitle, s.AttachedUrl,
                AttachedTabMissing: false, Tabs: Array.Empty<CdpTabDto>()));
        }

        var attached = s.AttachedTargetId;
        var missing = !string.IsNullOrEmpty(attached)
                      && !tabs.Any(t => t.TargetId == attached);

        return Result.Success(new CdpStatusDto(
            true, status.BrowserVersion, null, s.CdpPort,
            attached, s.AttachedTitle, s.AttachedUrl,
            AttachedTabMissing: missing,
            Tabs: tabs.Select(t => new CdpTabDto(
                t.TargetId, t.Title, t.Url, t.TargetId == attached)).ToList()));
    }
}

// ── Attach / detach ──────────────────────────────────────────────────

public record AttachCdpTabCommand(string TargetId) : IRequest<Result<CdpStatusDto>>;

public class AttachCdpTabValidator : AbstractValidator<AttachCdpTabCommand>
{
    public AttachCdpTabValidator() => RuleFor(x => x.TargetId).NotEmpty().MaximumLength(200);
}

public class AttachCdpTabHandler(
    IUserSettingsRepository settings,
    ICdpClient cdp,
    ICurrentUser me,
    IUnitOfWork uow,
    ISender sender) : IRequestHandler<AttachCdpTabCommand, Result<CdpStatusDto>>
{
    public async Task<Result<CdpStatusDto>> Handle(AttachCdpTabCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<CdpStatusDto>(Error.Validation("Not authenticated."));

        var s = await settings.GetOrCreateAsync(me.UserId.Value, ct);

        // Confirm the tab exists before storing it, and capture its title/URL
        // at the same moment — attaching to a targetId that was never there
        // would fail much later, inside a harness.
        IReadOnlyList<CdpTab> tabs;
        try
        {
            tabs = await cdp.ListTabsAsync(s.CdpPort, ct);
        }
        catch (Exception ex)
        {
            return Result.Failure<CdpStatusDto>(Error.External(
                $"Could not read the tab list to confirm the attachment: {ExceptionMessage.Describe(ex)}"));
        }

        var tab = tabs.FirstOrDefault(t => t.TargetId == cmd.TargetId);
        if (tab is null)
            return Result.Failure<CdpStatusDto>(Error.Validation(
                "That tab is no longer open. Refresh the tab list and pick again."));

        s.AttachTab(tab.TargetId, tab.Title, tab.Url);
        await uow.SaveChangesAsync(ct);

        return await sender.Send(new GetCdpStatusQuery(), ct);
    }
}

public record DetachCdpTabCommand : IRequest<Result<CdpStatusDto>>;

public class DetachCdpTabHandler(
    IUserSettingsRepository settings,
    ICurrentUser me,
    IUnitOfWork uow,
    ISender sender) : IRequestHandler<DetachCdpTabCommand, Result<CdpStatusDto>>
{
    public async Task<Result<CdpStatusDto>> Handle(DetachCdpTabCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<CdpStatusDto>(Error.Validation("Not authenticated."));

        var s = await settings.GetOrCreateAsync(me.UserId.Value, ct);
        s.DetachTab();
        await uow.SaveChangesAsync(ct);
        return await sender.Send(new GetCdpStatusQuery(), ct);
    }
}

// ── Port ─────────────────────────────────────────────────────────────

public record SetCdpPortCommand(int Port) : IRequest<Result<CdpStatusDto>>;

public class SetCdpPortValidator : AbstractValidator<SetCdpPortCommand>
{
    public SetCdpPortValidator() => RuleFor(x => x.Port).InclusiveBetween(1, 65535);
}

public class SetCdpPortHandler(
    IUserSettingsRepository settings,
    ICurrentUser me,
    IUnitOfWork uow,
    ISender sender) : IRequestHandler<SetCdpPortCommand, Result<CdpStatusDto>>
{
    public async Task<Result<CdpStatusDto>> Handle(SetCdpPortCommand cmd, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<CdpStatusDto>(Error.Validation("Not authenticated."));

        var s = await settings.GetOrCreateAsync(me.UserId.Value, ct);
        if (s.CdpPort != cmd.Port)
        {
            // targetIds are scoped to a browser instance, so one from the old
            // port is meaningless against the new one.
            s.SetCdpPort(cmd.Port);
            s.DetachTab();
        }
        await uow.SaveChangesAsync(ct);
        return await sender.Send(new GetCdpStatusQuery(), ct);
    }
}

// ── Shared resolution for the runtime pipeline ───────────────────────

/// <summary>
/// Every use case that spawns a harness needs the user's attachment. Resolving
/// it here keeps that one line identical across plan, execute and function-test
/// instead of three subtly different copies.
/// </summary>
public static class CdpTargetResolver
{
    public static async Task<CdpTarget> ResolveAsync(
        IUserSettingsRepository settings, Guid userId, CancellationToken ct)
    {
        var s = await settings.GetOrCreateAsync(userId, ct);
        return new CdpTarget(
            s.CdpPort,
            string.IsNullOrWhiteSpace(s.AttachedTargetId) ? null : s.AttachedTargetId);
    }

    /// <summary>
    /// Same, plus a pre-flight check that the attached tab is still open.
    ///
    /// The harness refuses to run against the wrong tab either way, but only
    /// after Playwright has spawned and connected — which costs ~20s and buries
    /// the reason inside a log tail. A closed tab is a routine, predictable
    /// state (you close a tab, you forget), so callers that can return an error
    /// synchronously should use this and get a clean one-line 400 in about a
    /// second. The harness check stays as the last line of defence for the race
    /// where the tab closes between this call and the run.
    ///
    /// Returns the error message, or null when good to go.
    /// </summary>
    public static async Task<(CdpTarget Target, string? Error)> ResolveValidatedAsync(
        IUserSettingsRepository settings, ICdpClient cdp, Guid userId, CancellationToken ct)
    {
        var target = await ResolveAsync(settings, userId, ct);
        if (target.TargetId is null) return (target, null);

        try
        {
            var tabs = await cdp.ListTabsAsync(target.Port, ct);
            if (tabs.Any(t => t.TargetId == target.TargetId)) return (target, null);

            return (target,
                "The browser tab MAZDA is attached to is no longer open. " +
                "Pick another from the browser-attachment panel in the top bar.");
        }
        catch
        {
            // Browser unreachable, or the list call failed. Don't block on it:
            // let the run proceed and fail with the runner's own diagnostics,
            // which are better at describing a connection problem than we are.
            return (target, null);
        }
    }
}
