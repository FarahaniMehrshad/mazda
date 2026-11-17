using Mazda.Domain.Common;

namespace Mazda.Domain.Modules.Tracing;

/// <summary>
/// Where the time went in one operation: a function check, a gather + plan, a
/// run, a round trip. <see cref="TraceJson"/> holds the whole trace (spans,
/// Playwright calls with their body lines, the bodies as they were).
/// </summary>
public class RunTrace : Entity
{
    public Guid OwnerId { get; private set; }
    /// <summary>function-check | plan | execute | roundtrip | roundtrip-plan</summary>
    public string Kind { get; private set; } = string.Empty;
    /// <summary>What it is about: the function, the generation, the round trip.</summary>
    public Guid SubjectId { get; private set; }
    public string Label { get; private set; } = string.Empty;
    public long TotalMs { get; private set; }
    public string TraceJson { get; private set; } = "{}";

    private RunTrace() { }

    public RunTrace(Guid id, Guid ownerId, string kind, Guid subjectId)
    {
        Id = id; OwnerId = ownerId; Kind = kind; SubjectId = subjectId;
    }

    public void Update(string label, long totalMs, string traceJson)
    {
        Label = label; TotalMs = totalMs; TraceJson = traceJson;
        Touch();
    }
}
