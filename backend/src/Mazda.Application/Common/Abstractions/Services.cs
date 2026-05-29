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
    /// <summary>
    /// Generate Playwright setup code (before-all / fixture wiring) from a free-form
    /// English description (e.g. "open https://app.example.com" or
    /// "connect to a running Chrome on localhost:9222 via CDP").
    /// </summary>
    Task<ScriptGenerationResult> GenerateSetupAsync(
        Signature signature,
        string setupPrompt,
        CancellationToken ct = default);

    /// <summary>
    /// Generate the Playwright test body from the user's test prompt. The setup
    /// script (if any) is provided as context so the test code references the
    /// same browser/context/page identifiers.
    /// </summary>
    Task<ScriptGenerationResult> GenerateTestAsync(
        Signature signature,
        string testPrompt,
        string? setupScript,
        CancellationToken ct = default);

    /// <summary>
    /// Merge a setup script and a test script into a single coherent
    /// Playwright .spec.ts file, reconciling imports and fixtures.
    /// </summary>
    Task<ScriptGenerationResult> MergeAsync(
        string setupScript,
        string testScript,
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
