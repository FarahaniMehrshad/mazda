using Mazda.Domain.Modules.Signatures;

namespace Mazda.Application.Common.Abstractions;

/// <summary>
/// Resolves the "effective signature" of a component — its own Signature
/// PLUS every transitive dependency in topological order — so generation
/// pipelines see one merged view. Cycles are surfaced as exceptions because
/// they imply data corruption (the Add-dependency use case rejects cycles
/// at write time).
/// </summary>
public interface IEffectiveSignatureResolver
{
    /// <summary>
    /// Returns the root signature followed by every transitive dependency
    /// in topological order: the deepest dependency first, then its parents,
    /// then the root LAST. Order matters for the LLM context: deeper deps
    /// are foundation components mentioned earlier as "context", the root is
    /// what the prompt actually targets.
    /// Returns null when the root signature isn't found / not owned by user.
    /// </summary>
    Task<IReadOnlyList<Signature>?> ResolveAsync(Guid rootId, Guid ownerId, CancellationToken ct = default);

    /// <summary>
    /// Cheap check: given an existing signature and a candidate new
    /// dependency, would adding it create a cycle? Used by the add-dependency
    /// use case to reject invalid edits with a clean validation error.
    /// </summary>
    Task<bool> WouldCreateCycleAsync(Guid signatureId, Guid candidateDependsOnId, Guid ownerId, CancellationToken ct = default);
}

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
        IReadOnlyList<Signature>? dependencies = null,
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
        string? stateJson = null,
        IReadOnlyList<Signature>? dependencies = null,
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

public record AnalyzedElement(string Selector, string Category, string ElementType, string Description, string Logic);
public record ComponentAnalysisResult(string SuggestedName, string SuggestedType, IReadOnlyList<AnalyzedElement> Elements, int TokensUsed);

/// <summary>
/// Dedicated deep-analysis pass for informational elements only. Receives the
/// existing signature (so it knows what's already covered) PLUS the source,
/// and returns a richer set of read-target elements — text content, list /
/// table / grid containers with their per-row anchors, status displays, etc.
/// </summary>
public interface IInformationalAnalyzer
{
    Task<InformationalAnalysisResult> AnalyzeInformationalAsync(
        Signature signature,
        string componentSource,
        CancellationToken ct = default);
}

public record InformationalAnalysisResult(IReadOnlyList<AnalyzedElement> Elements, int TokensUsed);

/// <summary>
/// Outbound port: given the raw extraction JSON produced by the analyze-state
/// Playwright script (an opaque per-selector key→value blob) and the signature
/// (descriptions / logic for each selector), produce a clean semantic JSON
/// representing the component's current state.
/// </summary>
public interface IStateBuilder
{
    Task<StateBuildResult> BuildStateAsync(
        Signature signature,
        string rawExtractionJson,
        IReadOnlyList<Signature>? dependencies = null,
        CancellationToken ct = default);
}

public record StateBuildResult(string StateJson, int TokensUsed);

/// <summary>
/// Outbound port for inferring a UI user-flow graph from a Signature
/// (and optional Cypress integration test source as grounding context).
/// </summary>
public interface IFlowAnalyzer
{
    Task<FlowAnalysisResult> InferFlowsAsync(
        Signature signature,
        string? cypressSource,
        CancellationToken ct = default);
}

public record FlowAnalysisResult(string FlowsJson, int TokensUsed);

// ── Script execution ─────────────────────────────────────────────────

public enum RunnerExitKind { Passed, FailedTest, Errored, Cancelled }

public sealed record RunnerCompletion(RunnerExitKind Kind, int ExitCode, string FullLog);

/// <summary>
/// In-memory registry of currently running script executions, so the SSE
/// endpoint can attach to the live log stream of a given generation.
/// </summary>
public interface IExecutionRegistry
{
    /// <summary>
    /// Wipe any previous-run state (lines, completed flag, leftover subscribers)
    /// before a new run starts. Idempotent.
    /// </summary>
    void Reset(Guid generationId);

    /// <summary>Push a new line into the live log buffer for an execution.</summary>
    void Append(Guid generationId, string line);

    /// <summary>Mark a run as completed (terminal state).</summary>
    void Complete(Guid generationId, RunnerCompletion result);

    /// <summary>Snapshot of every log line emitted so far, in order.</summary>
    IReadOnlyList<string> GetLog(Guid generationId);

    /// <summary>
    /// Subscribe for live updates. Returns a channel that yields each new line
    /// as it is appended; the channel completes when the run finishes.
    /// </summary>
    IAsyncEnumerable<string> Subscribe(Guid generationId, CancellationToken ct);

    bool IsRunning(Guid generationId);
}

/// <summary>
/// Outbound port for executing a Playwright script on the host that's running
/// the API. The runner spawns a child process, streams lines into the
/// IExecutionRegistry, and returns a terminal result.
/// </summary>
public interface IScriptRunner
{
    Task<RunnerCompletion> RunAsync(Guid generationId, string script, CancellationToken ct = default);

    /// <summary>
    /// One-shot run that bypasses the SSE registry. For analytical scripts whose
    /// output is parsed programmatically rather than tailed live (analyze-state
    /// extraction, etc.). Captures stdout+stderr into FullLog and returns when
    /// the process exits.
    /// </summary>
    Task<RunnerCompletion> RunOnceAsync(string script, CancellationToken ct = default);
}
