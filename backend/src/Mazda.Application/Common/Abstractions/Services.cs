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
/// Outbound port for the RELATIONSHIP analysis pass — step 4 of the signature
/// pipeline. Receives the existing Signature (interactive + informational
/// elements already extracted) PLUS the component source, and produces a
/// graph that captures the SEMANTIC RELATIONS that hide in the DOM tree:
/// a parent container whose heading labels its children (group-membership),
/// a "button that controls field X" (controls), mutually-exclusive pairs,
/// "refers to the same entity as", etc. The result is consumed by the
/// test-generation prompt as additional grounding when the user phrases a
/// step relationally ("click the delete button next to row X", "select the
/// option under <heading>").
///
/// The result JSON is opaque to the application layer — the analyzer is
/// expected to emit a well-formed { "nodes": [...], "edges": [...] } shape
/// and the use case just persists the string verbatim onto the signature.
/// </summary>
public interface IRelationshipAnalyzer
{
    Task<RelationshipAnalysisResult> AnalyzeRelationshipsAsync(
        Signature signature,
        string componentSource,
        CancellationToken ct = default);
}

public record RelationshipAnalysisResult(string GraphJson, int TokensUsed);

/// <summary>
/// Outbound port for the FUNCTION CATALOGUE generator — the design-time step
/// that turns a fully-analyzed Signature into the set of high-level functions
/// the runtime planner LLM composes into plans. Runs once per signature (or
/// whenever the author explicitly regenerates). Output persists as
/// <c>SignatureFunction</c> rows attached to the signature.
///
/// The generator produces TWO KINDS of function:
///   • state  — no parameters, returns structured JSON. Runs automatically
///              during gatherInformation to snapshot the UI. The planner LLM
///              sees the aggregated returned data, not the function calls.
///   • action — parameterised (typed via JSON Schema), side-effect producing,
///              returns null. The planner LLM composes plans out of these,
///              writing literal args grounded in the state snapshot.
///
/// The result is validated for shape (unique names, valid kind, non-empty
/// body) before it comes back — the caller can rely on the invariant that
/// every emitted <c>GeneratedFunction</c> is safe to persist.
/// </summary>
public interface IFunctionCatalogueGenerator
{
    /// <param name="recordedEpisodes">
    /// EpisodesJson documents from "learn by watching" recordings of this
    /// signature. Authoritative for runtime behaviour (what appears after an
    /// action, which requests fire) — and the only grounding when the
    /// signature has no source code.
    /// </param>
    Task<FunctionCatalogueResult> GenerateAsync(
        Signature signature,
        IReadOnlyList<Signature>? dependencies = null,
        IReadOnlyList<string>? recordedEpisodes = null,
        CancellationToken ct = default);
}

/// <summary>
/// One function as returned by the generator, in a shape that maps directly
/// onto <c>SignatureFunction.Create(...)</c>. <see cref="Kind"/> is the
/// lowercase wire form ("state" / "action") — the use case parses it into
/// the domain enum before persisting.
/// </summary>
public record GeneratedFunction(
    string Name,
    string Kind,
    string Description,
    string ParametersJsonSchema,
    string ReturnsJsonSchema,
    string Preconditions,
    string Body);

public record FunctionCatalogueResult(
    IReadOnlyList<GeneratedFunction> Functions,
    int TokensUsed);

// ── Runtime: gather → plan → execute ─────────────────────────────────
// The three ports below are the runtime side of the function-catalogue
// architecture. At request time the runner:
//   1. calls IStateGatherer.GatherAsync(sig) → GatheredState (deterministic,
//      runs every state function once against the connected browser),
//   2. hands (catalogue, state, userPrompt) to IPlanGenerator.GeneratePlanAsync
//      → Plan (LLM composes a sequence of action calls with literal args),
//   3. calls IPlanInterpreter.ExecuteAsync(sig, plan, state) → ExecutionEnvelope
//      (deterministic, executes each step against the connected browser,
//      fail-fast on the first step error).
//
// Only step 2 involves an LLM. The gatherer and interpreter are pure
// Playwright execution — the whole point of the catalogue architecture is
// that runtime is deterministic given a plan.

/// <summary>
/// A snapshot of the UI captured by running every state function on the
/// signature (and on its dependencies, if any). Each state function's return
/// value is keyed by its name into <see cref="StateJson"/>. Individual
/// function failures during gather do NOT abort the whole gather — they land
/// in the JSON as <c>{"__mz_error": "..."}</c> so the planner LLM can see
/// what did and didn't observe.
/// </summary>
public record GatheredState(
    string SignatureLabel,  // "name@version" — for provenance
    DateTime CapturedAt,    // wall-clock at gather time
    string StateJson);      // opaque JSON object keyed by state function name

/// <summary>
/// A plan the LLM composed to fulfil the user prompt. Flat sequence, literal
/// args, no variables / refs / control flow (per the v1 design). Order matters
/// — steps execute in list order, fail-fast on the first failure.
/// </summary>
public record Plan(string? Reasoning, IReadOnlyList<PlanStep> Steps);

/// <summary>
/// One step in a <see cref="Plan"/>. <see cref="ArgsJson"/> is the raw JSON
/// object the LLM produced — the interpreter validates it against the
/// referenced function's parameters schema before invoking the body.
/// </summary>
public record PlanStep(string Id, string Call, string ArgsJson);

public enum StepStatus { Ok, Failed, Skipped }
public enum RunOutcome { Ok, Failed }

/// <summary>
/// Typed error categories the interpreter surfaces. Kept small on purpose —
/// enough to render meaningful UI without inviting bikeshed on subcategories.
/// </summary>
public enum StepErrorKind
{
    SchemaMismatch,     // step.args failed JSON-schema validation
    FunctionNotFound,   // step.call not in the catalogue
    PreconditionFailed, // (reserved — v1 doesn't enforce preconditions)
    RuntimeError,       // function body threw
    HarnessError,       // stitching / connect / spawn broke before we could run
}

public record StepError(StepErrorKind Kind, string Message);

public record StepResult(
    string Id,
    string Call,
    StepStatus Status,
    long DurationMs,
    StepError? Error);

/// <summary>
/// The complete result of a single run — everything the wizard's Run page
/// needs to render a post-mortem: what state we captured, what plan we
/// composed, what happened when we executed each step. Serialised onto the
/// generation row (see slice 4b for the aggregate rewire).
/// </summary>
public record ExecutionEnvelope(
    RunOutcome Outcome,
    int StepsTotal,
    int StepsExecuted,
    IReadOnlyList<StepResult> Steps,
    GatheredState CapturedState,
    Plan Plan);

/// <summary>
/// Runs every state function on the signature (and, when transitive
/// dependencies are supplied, their state functions too — namespaced by
/// signature name to prevent collisions). Returns a merged
/// <see cref="GatheredState"/>. Pure execution — no LLM.
///
/// A signature with zero state functions returns a well-formed but empty
/// state (<c>{}</c>). The gatherer never fails: an individual function's
/// runtime error is encoded into the returned JSON, and a whole-browser
/// failure surfaces as a <see cref="HarnessException"/>.
/// </summary>
/// <summary>
/// What a failed function test observed. Fed to <see cref="IFunctionFixer"/>
/// so the model diagnoses the actual failure rather than re-deriving the body
/// from scratch.
/// </summary>
public sealed record FunctionFailureContext(
    string? ErrorKind,
    string? ErrorMessage,
    string? ErrorStack,
    /// Arguments the function was called with — a selector can be right and
    /// the args still wrong.
    string ArgsJson,
    /// Where the browser actually was. A body that assumes the wrong view
    /// fails in a way only this explains.
    string? PageUrl,
    string? PageTitle,
    /// Tail of the runner log. Playwright's own diagnostics ("waiting for
    /// locator … resolved to 3 elements") are often more useful than the
    /// exception message.
    string? LogTail);

public sealed record FunctionFixResult(
    /// Replacement body only. Never the schemas — see IFunctionFixer.
    string Body,
    /// One or two sentences on what was wrong, shown next to the diff.
    string Explanation,
    int TokensUsed);

/// <summary>
/// Repairs a function body that failed against the live page.
///
/// Scoped deliberately to the BODY. Changing the parameter schema would
/// invalidate the arguments the author just typed and break the contract the
/// runtime planner composes against; a fix that genuinely needs a new
/// parameter is a catalogue-level edit, not a repair.
///
/// Uses the generator model, like the catalogue generator — this writes real
/// Playwright, and weak models regress badly at it.
/// </summary>
public interface IFunctionFixer
{
    Task<FunctionFixResult> FixAsync(
        Signature signature,
        IReadOnlyList<Signature>? dependencies,
        SignatureFunction function,
        FunctionFailureContext failure,
        /// Optional free-text steer from the author ("the row index is
        /// 1-based in this grid"). Empty when they just want a diagnosis.
        string? userHint,
        CancellationToken ct = default);
}

// ── CDP attachment ───────────────────────────────────────────────────
// Which Chrome tab MAZDA drives. Every harness used to take the first page
// Chrome reported, which is arbitrary once more than one tab is open.

/// <summary>
/// Where a harness should connect, and which tab it should drive.
/// <see cref="TargetId"/> empty means "first page found" — the behaviour
/// before tab attachment existed, kept as the no-preference default.
/// </summary>
public sealed record CdpTarget(int Port, string? TargetId)
{
    public static CdpTarget Default { get; } = new(9222, null);
}

/// <summary>One driveable tab, as reported by Chrome's <c>/json/list</c>.</summary>
public sealed record CdpTab(string TargetId, string Title, string Url);

public sealed record CdpStatus(bool Available, string? BrowserVersion, string? Reason)
{
    public static CdpStatus Reachable(string version) => new(true, version, null);
    public static CdpStatus Unavailable(string reason) => new(false, null, reason);
}

/// <summary>
/// Reads Chrome's HTTP debugging endpoints. Implementations must spoof the
/// <c>Host</c> header to <c>localhost:&lt;port&gt;</c> — Chrome 111+ rejects
/// anything else, and from inside the container we necessarily connect by a
/// different hostname.
/// </summary>
public interface ICdpClient
{
    Task<CdpStatus> ProbeAsync(int port, CancellationToken ct = default);
    Task<IReadOnlyList<CdpTab>> ListTabsAsync(int port, CancellationToken ct = default);
}

public interface IStateGatherer
{
    Task<GatheredState> GatherAsync(
        Signature signature,
        IReadOnlyList<Signature>? dependencies,
        CdpTarget target,
        CancellationToken ct = default);
}

/// <summary>
/// Result of running ONE catalogue function against the live browser.
///
/// A body that throws is a successful test with <c>Ok == false</c> — the whole
/// point of the affordance is to see that message. Only an unreachable CDP
/// browser or a spec that won't compile raises <see cref="HarnessException"/>.
/// </summary>
public sealed record FunctionTestOutcome(
    bool Ok,
    string? ReturnedJson,   // state functions return data; actions return null
    string? ErrorKind,      // "RuntimeError" | "FunctionNotFound" — null when Ok
    string? ErrorMessage,
    string? ErrorStack,
    int DurationMs,
    string? PageUrl,        // where the browser actually was, for context
    string? PageTitle,
    string LogTail);        // tail of runner stdout, for when the payload isn't enough

/// <summary>
/// Design-time debugging port: run a single function from a signature's
/// catalogue, with caller-supplied arguments, against the CDP-attached browser.
/// Not part of the runtime gather → plan → execute path.
/// </summary>
public interface IFunctionTester
{
    Task<FunctionTestOutcome> TestAsync(
        Signature signature,
        IReadOnlyList<Signature>? dependencies,
        string qualifiedFunctionName,
        string argsJson,
        CdpTarget target,
        CancellationToken ct = default);
}

/// <summary>
/// Runtime LLM. Given the catalogue (action functions only — state functions
/// don't get invoked by the planner), the gathered state snapshot, and the
/// user's natural-language prompt, produces a <see cref="Plan"/>.
///
/// The generator model has one job: pick 1–5 function names from the
/// catalogue and fill their args with values grounded in the state snapshot.
/// It never writes Playwright — that's what the catalogue is for.
/// </summary>
public interface IPlanGenerator
{
    Task<PlanGenerationResult> GeneratePlanAsync(
        Signature signature,
        IReadOnlyList<Signature>? dependencies,
        GatheredState state,
        string userPrompt,
        CancellationToken ct = default);
}

public record PlanGenerationResult(Plan Plan, int TokensUsed);

/// <summary>
/// Executes a <see cref="Plan"/> against the connected browser. Streams
/// per-step events into <see cref="IExecutionRegistry"/> keyed by
/// <paramref name="runId"/> so an SSE subscriber can render the run live,
/// and also assembles the final <see cref="ExecutionEnvelope"/> for
/// persistence.
///
/// Fail-fast: on the first step failure, execution stops. Later steps
/// appear in the envelope with <see cref="StepStatus.Skipped"/> — the plan
/// still shipped, but the runtime declined to keep going.
/// </summary>
public interface IPlanInterpreter
{
    Task<ExecutionEnvelope> ExecuteAsync(
        Guid runId,
        Signature signature,
        IReadOnlyList<Signature>? dependencies,
        Plan plan,
        GatheredState state,
        CdpTarget target,
        CancellationToken ct = default);
}

/// <summary>
/// Thrown when the runner harness itself cannot execute — Playwright failed
/// to spawn, the CDP endpoint was unreachable, the stitched script wouldn't
/// compile. Distinct from a step-level RuntimeError, which means the browser
/// was reachable and Playwright ran but the function body threw.
/// </summary>
public class HarnessException : Exception
{
    public HarnessException(string message) : base(message) { }
    public HarnessException(string message, Exception inner) : base(message, inner) { }
}

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
/// Outbound port for inferring a UI user-flow graph from a Signature.
/// </summary>
public interface IFlowAnalyzer
{
    Task<FlowAnalysisResult> InferFlowsAsync(
        Signature signature,
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

    /// <summary>True when a feed for this id is held (started and not yet forgotten).</summary>
    bool Exists(Guid id);

    /// <summary>Drop a finished feed's buffered lines. Open subscribers are completed.</summary>
    void Forget(Guid id);
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
    /// Same as <see cref="RunAsync(Guid,string,CancellationToken)"/> with knobs for
    /// long-lived scripts (the recorder): a per-line sink, opting out of the
    /// registry / full-log buffers (both are unbounded), a graceful-stop window
    /// on cancellation, and a replacement playwright.config.ts.
    /// </summary>
    Task<RunnerCompletion> RunAsync(Guid id, string script, ScriptRunOptions options, CancellationToken ct = default);

    /// <summary>
    /// One-shot run that bypasses the SSE registry. For analytical scripts whose
    /// output is parsed programmatically rather than tailed live (analyze-state
    /// extraction, etc.). Captures stdout+stderr into FullLog and returns when
    /// the process exits.
    /// </summary>
    Task<RunnerCompletion> RunOnceAsync(string script, CancellationToken ct = default);
}

/// <summary>
/// Options for <see cref="IScriptRunner.RunAsync(Guid,string,ScriptRunOptions,CancellationToken)"/>.
/// Defaults reproduce the classic RunAsync behaviour exactly.
/// </summary>
/// <param name="OnLine">Called with every ANSI-stripped stdout/stderr line.</param>
/// <param name="StreamToRegistry">Append every line to <see cref="IExecutionRegistry"/>.</param>
/// <param name="CaptureFullLog">Buffer every line into <see cref="RunnerCompletion.FullLog"/>.</param>
/// <param name="GracefulStopTimeout">
/// When set, cancellation first writes the file named by the child's
/// <c>MAZDA_STOP_FILE</c> env var and waits this long for a clean exit before
/// killing the process tree. Null keeps the immediate kill.
/// </param>
/// <param name="PlaywrightConfigOverride">Replaces the default playwright.config.ts.</param>
public sealed record ScriptRunOptions(
    Action<string>? OnLine = null,
    bool StreamToRegistry = true,
    bool CaptureFullLog = true,
    TimeSpan? GracefulStopTimeout = null,
    string? PlaywrightConfigOverride = null);

// ── "Learn by watching" ───────────────────────────────────────────────

/// <summary>What the recorder host needs to attach and start capturing.</summary>
public sealed record RecordingStartSpec(
    Guid SessionId,
    CdpTarget Target,
    IReadOnlyList<string> AllowedOrigins,
    bool CaptureBodies,
    bool MaskAllInputs);

/// <summary>
/// Owns the long-lived recorder processes. Start returns immediately; the
/// host streams events into the DB + <see cref="IExecutionRegistry"/> (keyed
/// by session id) and persists the terminal session state itself.
/// </summary>
public interface IRecordingHost
{
    void Start(RecordingStartSpec spec);
    /// <summary>Ask the recorder to flush and exit. False when the session is not running in this process.</summary>
    bool RequestStop(Guid sessionId);
    bool IsActive(Guid sessionId);
    /// <summary>Request stop on every active session (API shutdown).</summary>
    void StopAll();
}

/// <summary>An element the recording analyzer proposes adding to (or matching in) the signature.</summary>
public sealed record ProposedElement(
    string Selector,
    string Category,
    string ElementType,
    string Description,
    string Logic,
    /// Selector of the existing element this is the same control as, or null when new.
    string? MatchesExisting,
    /// Episode indices ("i") that evidence this element.
    IReadOnlyList<int> Evidence);

public sealed record RecordingAnalysisResult(
    IReadOnlyList<ProposedElement> Elements,
    /// Flow graph in the LlmFlowAnalyzer schema ({ nodes, edges }).
    string FlowsJson,
    string Notes,
    int TokensUsed,
    /// Recording sessions this proposal was derived from (joint analysis).
    IReadOnlyList<Guid>? SessionIds = null,
    /// Analyzed without previously learned elements: applying it may replace them.
    bool Fresh = false,
    /// Per-session readback: the analyzer's title + what it understood.
    IReadOnlyList<SessionUnderstanding>? Sessions = null);

/// <summary>What the analyzer took one recorded session to show.</summary>
public sealed record SessionUnderstanding(Guid SessionId, string Title, string Understanding);

/// <summary>One recorded session handed to the analyzer.</summary>
public sealed record RecordedSessionInput(Guid Id, string Label, string Intent, string EpisodesJson);

/// <summary>
/// Turns recorded episodes into proposed elements + flows. Takes every
/// recording of a signature at once so the result is one consolidated
/// element set, not one overlapping set per session.
/// </summary>
public interface IRecordingAnalyzer
{
    /// <param name="ignoreLearned">Hide elements previously learned from recordings (Origin = Recorded) from the model.</param>
    Task<RecordingAnalysisResult> AnalyzeAsync(Signature signature, IReadOnlyList<RecordedSessionInput> sessions, bool ignoreLearned = false, CancellationToken ct = default);
}
