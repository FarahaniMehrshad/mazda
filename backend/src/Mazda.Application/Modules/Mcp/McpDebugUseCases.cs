using System.Text;
using System.Text.Json;
using Mazda.Domain.Common;
using Mazda.Application.Common.Abstractions;
using Mazda.Application.Modules.Generations;
using MediatR;

namespace Mazda.Application.Modules.Mcp;

// Debugging tools an agent can call after a run failed. They are not
// user-defined McpTools: they are fixed, read-only, and keyed by the
// generation id that every failed invocation reports.

public static class McpDebugTools
{
    public const string GetRunFailure = "get_run_failure";
    public const string GetStepArtifact = "get_step_artifact";
    public const string SearchRunNetwork = "search_run_network";

    public static readonly IReadOnlyList<string> All = [GetRunFailure, GetStepArtifact, SearchRunNetwork];

    public static bool IsDebugTool(string? name)
        => name is not null && All.Contains(name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Text for the agent, plus image data when the artifact is a screenshot.</summary>
public record McpDebugResult(string Text, string? ImageBase64, bool IsError);

public record RunDebugCommand(
    string Tool,
    Guid GenerationId,
    string? Kind = null,
    int Offset = 0,
    int Limit = 0,
    string? UrlContains = null,
    string? Step = null,
    bool FailedOnly = false) : IRequest<Result<McpDebugResult>>;

public class RunDebugHandler(IGenerationRepository repo, ICurrentUser me)
    : IRequestHandler<RunDebugCommand, Result<McpDebugResult>>
{
    public async Task<Result<McpDebugResult>> Handle(RunDebugCommand c, CancellationToken ct)
    {
        if (me.UserId is null) return Result.Failure<McpDebugResult>(Error.Validation("Not authenticated."));
        var g = await repo.FindByIdAsync(c.GenerationId, me.UserId.Value, ct);
        if (g is null) return Fail($"No run with generation_id {c.GenerationId} (or it belongs to another user).");

        switch (c.Tool.ToLowerInvariant())
        {
            case McpDebugTools.GetRunFailure:
            {
                var head = $"run {g.Id}: status={g.ExecutionStatus}";
                var report = ForensicsFormatter.Report(g.ForensicsJson, g.ExecutionEnvelopeJson, g.ExecutionLog);
                return Ok($"{head}\n\n{report}");
            }

            case McpDebugTools.GetStepArtifact:
            {
                if (string.IsNullOrWhiteSpace(c.Kind))
                    return Fail($"'kind' is required. One of: {string.Join(", ", ForensicsFormatter.ArtifactKinds)}.");
                if (c.Kind.Equals("log", StringComparison.OrdinalIgnoreCase))
                {
                    var log = g.ExecutionLog;
                    if (string.IsNullOrEmpty(log)) return Fail("No runner log was stored for this run.");
                    return Ok(PageText(log, c.Offset, c.Limit));
                }
                if (string.IsNullOrEmpty(g.ForensicsJson))
                    return Fail("No page snapshot was stored for this run. Only runs where a step failed with a live page have one; try kind=log.");
                if (!string.IsNullOrWhiteSpace(c.Step) && !BundleStep(g.ForensicsJson).Equals(c.Step, StringComparison.Ordinal))
                    return Fail($"Snapshots are only taken for the failing step, which is '{BundleStep(g.ForensicsJson)}', not '{c.Step}'.");

                var slice = ForensicsFormatter.Artifact(g.ForensicsJson, c.Kind, c.Offset, c.Limit);
                return Result.Success(new McpDebugResult(slice.Text, slice.ImageBase64, IsError: !slice.Found));
            }

            case McpDebugTools.SearchRunNetwork:
            {
                if (string.IsNullOrEmpty(g.ForensicsJson))
                    return Fail("No network log was stored for this run (no page snapshot).");
                using var doc = JsonDocument.Parse(g.ForensicsJson);
                if (!doc.RootElement.TryGetProperty("network", out var net) || net.ValueKind != JsonValueKind.Array)
                    return Ok("(no network traffic captured)");

                var sb = new StringBuilder();
                var n = 0;
                foreach (var r in net.EnumerateArray())
                {
                    string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    if (!string.IsNullOrEmpty(c.UrlContains)
                        && S("url")?.Contains(c.UrlContains, StringComparison.OrdinalIgnoreCase) != true) continue;
                    if (!string.IsNullOrEmpty(c.Step) && S("step") != c.Step) continue;
                    if (c.FailedOnly && !ForensicsFormatter.IsBad(r)) continue;
                    if (++n > 60) { sb.AppendLine("… more matches omitted; narrow with url_contains / step / failed_only."); break; }
                    sb.AppendLine(ForensicsFormatter.RequestLine(r, withBodies: true));
                }
                return Ok(n == 0 ? "No captured requests match those filters." : sb.ToString().TrimEnd());
            }

            default:
                return Fail($"Unknown debug tool '{c.Tool}'.");
        }
    }

    private static string BundleStep(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.TryGetProperty("stepId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }

    private static string PageText(string text, int offset, int limit)
    {
        limit = Math.Clamp(limit <= 0 ? ForensicsFormatter.DefaultArtifactLimit : limit, 500, 60_000);
        offset = Math.Clamp(offset, 0, text.Length);
        var slice = text.Substring(offset, Math.Min(limit, text.Length - offset));
        var end = offset + slice.Length;
        return slice + (end < text.Length
            ? $"\n\n[chars {offset}-{end} of {text.Length}; call again with offset={end} for more]"
            : $"\n\n[chars {offset}-{end} of {text.Length}; end]");
    }

    private static Result<McpDebugResult> Ok(string text) => Result.Success(new McpDebugResult(text, null, false));
    private static Result<McpDebugResult> Fail(string text) => Result.Success(new McpDebugResult(text, null, true));
}
