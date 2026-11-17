using System.Diagnostics;

namespace Mazda.Application.Common.Tracing;

/// <summary>
/// Collects one operation's trace. Spans nest by async flow: a span opened
/// with <see cref="Span"/> is the parent of everything recorded until it is
/// disposed, including the Playwright scripts the runner attaches through
/// <see cref="TraceContext"/>. Thread-safe; cheap enough to leave always on.
/// </summary>
public sealed class TraceRecorder
{
    private readonly object _gate = new();
    private readonly List<TraceNode> _nodes = new();
    private readonly Dictionary<string, TraceBody> _bodies = new(StringComparer.Ordinal);
    private readonly AsyncLocal<int?> _current = new();
    private int _nextId = 1;
    private readonly long _start = Now();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public TraceRecorder(string kind, string label)
    {
        Kind = kind;
        Label = label;
    }

    /// <summary>The stored trace's id: saving again updates the same row.</summary>
    public Guid Id { get; } = Guid.NewGuid();
    public string Kind { get; }
    public string Label { get; set; }

    /// <summary>Unix ms.</summary>
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private long Clock() => _start + _clock.ElapsedMilliseconds;

    /// <summary>Opens a span; dispose it to close. Children recorded meanwhile nest under it.</summary>
    public IDisposable Span(string name, string category = TraceCategory.Operation)
    {
        int id;
        var parent = _current.Value;
        lock (_gate) id = _nextId++;
        var open = new OpenSpan(this, id, parent, name, category, Clock());
        _current.Value = id;
        return open;
    }

    private void Close(OpenSpan s)
    {
        var node = new TraceNode(s.Id, s.Parent, s.Name, s.Category, s.Start, Math.Max(0, Clock() - s.Start));
        lock (_gate) _nodes.Add(node);
        _current.Value = s.Parent;
    }

    /// <summary>Attaches a Playwright script's nodes under the current span.</summary>
    public void AddScript(ScriptTraceResult script)
    {
        lock (_gate)
        {
            var map = new Dictionary<int, int>();
            foreach (var n in script.Nodes) map[n.Id] = _nextId++;
            var parent = _current.Value;
            foreach (var n in script.Nodes)
                _nodes.Add(n with { Id = map[n.Id], Parent = n.Parent is { } p && map.TryGetValue(p, out var q) ? q : parent });
            foreach (var (k, v) in script.Bodies) _bodies[k] = v;
        }
    }

    /// <summary>Snapshot, with spans still open shown as running until now.</summary>
    public TraceDoc Snapshot()
    {
        lock (_gate)
        {
            var now = Clock();
            var nodes = _nodes.ToList();
            foreach (var s in _open.Values)
                nodes.Add(new TraceNode(s.Id, s.Parent, s.Name + " (running)", s.Category, s.Start, Math.Max(0, now - s.Start)));
            var end = nodes.Count == 0 ? now : Math.Max(now, nodes.Max(n => n.Start + n.Ms));
            return new TraceDoc(Kind, Label, _start, end - _start, nodes.OrderBy(n => n.Start).ThenBy(n => n.Id).ToList(),
                new Dictionary<string, TraceBody>(_bodies));
        }
    }

    private readonly Dictionary<int, OpenSpan> _open = new();

    private sealed class OpenSpan : IDisposable
    {
        private readonly TraceRecorder _owner;
        private bool _done;
        public int Id { get; }
        public int? Parent { get; }
        public string Name { get; }
        public string Category { get; }
        public long Start { get; }

        public OpenSpan(TraceRecorder owner, int id, int? parent, string name, string category, long start)
        {
            _owner = owner; Id = id; Parent = parent; Name = name; Category = category; Start = start;
            lock (owner._gate) owner._open[id] = this;
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            lock (_owner._gate) _owner._open.Remove(Id);
            _owner.Close(this);
        }
    }
}

/// <summary>
/// The trace of the operation running on this async flow, if any. Use cases
/// set it; the script runner (a singleton) attaches each script it runs, so
/// no runner result type has to carry a trace and a run that throws still
/// leaves its trace behind.
/// </summary>
public static class TraceContext
{
    private static readonly AsyncLocal<TraceRecorder?> CurrentRecorder = new();

    public static TraceRecorder? Current => CurrentRecorder.Value;

    /// <summary>Starts a trace for this async flow (and the flows it starts).</summary>
    public static TraceRecorder Begin(string kind, string label)
    {
        var r = new TraceRecorder(kind, label);
        CurrentRecorder.Value = r;
        return r;
    }

    /// <summary>A span on the current trace, or a no-op when nothing is traced.</summary>
    public static IDisposable Span(string name, string category = TraceCategory.Operation)
        => Current?.Span(name, category) ?? NoOp.Instance;

    private sealed class NoOp : IDisposable
    {
        public static readonly NoOp Instance = new();
        public void Dispose() { }
    }
}
