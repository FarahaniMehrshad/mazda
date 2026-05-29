using Mazda.Domain.Modules.Signatures;

namespace Mazda.Application.Common.Abstractions;

public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Email { get; }
    bool IsAuthenticated { get; }
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public interface IJwtIssuer
{
    string Issue(Guid userId, string email);
}

/// <summary>
/// Outbound port for the LLM. Implementations live in Infrastructure.
/// </summary>
public interface IScriptGenerator
{
    Task<ScriptGenerationResult> GenerateAsync(
        Signature signature,
        string targetUrl,
        string prompt,
        CancellationToken ct = default);
}

public record ScriptGenerationResult(string Script, int TokensUsed);

/// <summary>
/// Outbound port for "analyze component" — sends source to the LLM, gets signature JSON back.
/// </summary>
public interface IComponentAnalyzer
{
    Task<ComponentAnalysisResult> AnalyzeAsync(
        string componentSource,
        string fileName,
        string? humanDescription = null,
        CancellationToken ct = default);
}

public record AnalyzedElement(string Selector, string ElementType, string Description);
public record ComponentAnalysisResult(string SuggestedName, string SuggestedType, IReadOnlyList<AnalyzedElement> Elements, int TokensUsed);
