namespace Mazda.Application.Common.Tracing;

/// <summary>
/// Where time goes in one operation (a function check, a gather, a run, a round trip).
///
/// A flat list of nodes with parent links. Every node has an absolute start
/// (Unix ms) and a duration; a node's own ("self") time is its duration minus
/// its children's. Three sources fill it:
///   • backend spans (LLM calls, database, whole phases) from <see cref="TraceRecorder"/>;
///   • each Playwright script: process start-up, harness phases, function calls,
///     and every Playwright API call with the body line that made it (<see cref="ScriptTrace"/>);
///   • function bodies as they were when the trace was taken, so line numbers stay right.
/// </summary>
public sealed record TraceDoc(
    string Kind,
    string Label,
    long Start,
    long Ms,
    List<TraceNode> Nodes,
    Dictionary<string, TraceBody> Bodies);

/// <param name="Fn">Function the node ran inside, when known.</param>
/// <param name="Body">Key into <see cref="TraceDoc.Bodies"/> for <paramref name="Fn"/>'s source.</param>
/// <param name="Line">1-based line in that body.</param>
/// <param name="TimedOut">A Playwright timeout, including ones a body swallowed with <c>.catch</c>.</param>
public sealed record TraceNode(int Id, int? Parent, string Name, string Category, long Start, long Ms)
{
    public string? Fn { get; init; }
    public string? Body { get; init; }
    public int? Line { get; init; }
    public string? Error { get; init; }
    public bool TimedOut { get; init; }
    /// <summary>For an LLM call: exactly what it used and cost.</summary>
    public TraceLlmUsage? Llm { get; init; }
}

/// <summary>One LLM call's usage on its trace node. <c>Input</c> is uncached input; <c>Reasoning</c> is part of <c>Output</c>.</summary>
public sealed record TraceLlmUsage(
    string Purpose,
    string Provider,
    string Model,
    long Input,
    long CacheRead,
    long CacheWrite,
    long Output,
    long Reasoning,
    decimal? CostUsd,
    string CostSource,
    bool Ok);

public sealed record TraceBody(string Fn, List<string> Lines);

/// <summary>
/// Categories. Network time is expected; the rest is what a slow run is made of.
/// </summary>
public static class TraceCategory
{
    public const string Operation = "operation";   // a backend phase; its self time is backend work
    public const string Llm = "llm";
    public const string Db = "db";
    public const string Startup = "startup";       // run dir, process + TypeScript start, connecting, shutting down
    public const string Harness = "harness";       // settle, recorder, forensics, harness code between steps
    public const string Function = "function";     // one call of a catalogue function; self time = JS between Playwright calls
    public const string Network = "network";       // waiting for responses, navigation
    public const string Action = "action";         // click, fill, hover… includes Playwright's wait for the element
    public const string Wait = "wait";             // waitFor, waitForSelector, expect
    public const string Read = "read";             // textContent, count, evaluate…
    public const string Sleep = "sleep";           // fixed pauses
    public const string Capture = "capture";       // screenshots, accessibility snapshots
    public const string Timeout = "timeout";       // summary bucket: calls that ran into a timeout
    public const string Js = "js";                 // summary bucket: a function's own time between Playwright calls
    public const string Backend = "backend";       // summary bucket: a backend phase's own time
    public const string Other = "other";

    public static bool IsNormal(string category) => category == Network;
}
