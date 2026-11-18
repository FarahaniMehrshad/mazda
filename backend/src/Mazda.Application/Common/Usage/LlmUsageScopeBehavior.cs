using System.Text;
using MediatR;
using Mazda.Application.Common.Abstractions;

namespace Mazda.Application.Common.Usage;

/// <summary>
/// Opens an LLM usage scope for every request, so every LLM call it causes is
/// attributed to the signed-in user even when the handler names no subject.
/// A request sent from inside another one keeps the outer scope.
/// </summary>
public sealed class LlmUsageScopeBehavior<TRequest, TResponse>(ICurrentUser me)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    // async on purpose: the scope set here must end with this request, not
    // leak into the caller's flow.
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (LlmUsageContext.Current is null)
            LlmUsageContext.Begin(me.UserId, KindOf(typeof(TRequest)));
        return await next();
    }

    /// <summary>"AnalyzeComponentCommand" → "analyze-component".</summary>
    public static string KindOf(Type t)
    {
        var name = t.Name;
        foreach (var suffix in new[] { "Command", "Query", "Request" })
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length) { name = name[..^suffix.Length]; break; }
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('-');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}
