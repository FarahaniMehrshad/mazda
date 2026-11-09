using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mazda.Application.Common.Abstractions;
using Mazda.Application.Modules.Recordings;
using Mazda.Domain.Modules.Recordings;

namespace Mazda.Infrastructure.Runner;

/// <summary>
/// Owns the long-lived recorder processes ("learn by watching").
///
/// Per session: spawns the recorder spec through <see cref="IScriptRunner"/>
/// with a line sink, parses <c>__MZ_REC__</c> lines, sanitizes them, batches
/// them into <c>recording_events</c> (fresh DI scope per batch — this runs
/// outside any request), and mirrors a compact copy into
/// <see cref="IExecutionRegistry"/> (keyed by session id) for the live SSE
/// feed. When the process exits it builds the episode summary and persists
/// the terminal status BEFORE completing the registry stream — same ordering
/// guarantee as run execution, so a client that refetches on SSE `end` sees
/// the final state.
/// </summary>
public sealed class RecordingHost : IRecordingHost
{
    private sealed class Active
    {
        public required CancellationTokenSource Cts { get; init; }
        public Task Run { get; set; } = Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<Guid, Active> _active = new();
    private readonly IScriptRunner _runner;
    private readonly IExecutionRegistry _registry;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RecordingHost> _log;

    private static readonly TimeSpan GracefulStop = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FeedRetention = TimeSpan.FromMinutes(2);
    private const int BatchSize = 100;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromSeconds(1);

    public RecordingHost(
        IScriptRunner runner,
        IExecutionRegistry registry,
        IServiceScopeFactory scopes,
        ILogger<RecordingHost> log)
    {
        _runner = runner;
        _registry = registry;
        _scopes = scopes;
        _log = log;
    }

    /// <summary>
    /// Called on API shutdown (Program.cs): ask every recorder to flush. The
    /// boot-time SQL marks anything still live as Failed if we don't make it.
    /// </summary>
    public void StopAll()
    {
        foreach (var id in _active.Keys) RequestStop(id);
    }

    public bool IsActive(Guid sessionId) => _active.ContainsKey(sessionId);

    public bool RequestStop(Guid sessionId)
    {
        if (!_active.TryGetValue(sessionId, out var a)) return false;
        try { a.Cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    public void Start(RecordingStartSpec spec)
    {
        var active = new Active { Cts = new CancellationTokenSource() };
        if (!_active.TryAdd(spec.SessionId, active))
            throw new InvalidOperationException("Recording already running.");

        _registry.Reset(spec.SessionId);
        active.Run = Task.Run(() => RunAsync(spec, active.Cts.Token));
    }

    private async Task RunAsync(RecordingStartSpec spec, CancellationToken stopToken)
    {
        var sessionId = spec.SessionId;
        var channel = Channel.CreateUnbounded<RecordingEvent>(new UnboundedChannelOptions { SingleReader = true });
        var counters = new Counters();
        var writer = Task.Run(() => WriteBatchesAsync(sessionId, channel.Reader, counters));

        RunnerCompletion completion = new(RunnerExitKind.Errored, -1, string.Empty);
        try
        {
            var script = RecorderScriptBuilder.Build(spec);
            completion = await _runner.RunAsync(
                sessionId,
                script,
                new ScriptRunOptions(
                    OnLine: line => OnLine(sessionId, line, channel.Writer, counters),
                    StreamToRegistry: false,
                    CaptureFullLog: false,
                    GracefulStopTimeout: GracefulStop,
                    PlaywrightConfigOverride: RecorderScriptBuilder.PlaywrightConfig),
                stopToken);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Recorder {Session} crashed", sessionId);
            counters.LastError = ex.Message;
        }
        finally
        {
            channel.Writer.TryComplete();
            try { await writer; } catch (Exception ex) { _log.LogError(ex, "Recorder {Session} event writer failed", sessionId); }

            try { await FinalizeAsync(sessionId, completion, counters); }
            catch (Exception ex) { _log.LogError(ex, "Recorder {Session} finalize failed", sessionId); }

            // Only after the terminal state is persisted.
            _registry.Complete(sessionId, completion);
            if (_active.TryRemove(sessionId, out var a)) a.Cts.Dispose();
            // The live feed is only for watching; once finished, the stored
            // events are the record. Keep it briefly for late subscribers
            // (they get the backlog + end), then free it — a 30-minute
            // session can hold thousands of lines.
            _ = Task.Delay(FeedRetention).ContinueWith(_ => _registry.Forget(sessionId), TaskScheduler.Default);
        }
    }

    private sealed class Counters
    {
        public int Events;
        public int Actions;
        public string? EndReason;
        public bool Limited;
        public string? LastError;
        public readonly Queue<string> Tail = new();   // last non-event lines, for failure diagnostics
    }

    private void OnLine(Guid sessionId, string line, ChannelWriter<RecordingEvent> sink, Counters c)
    {
        var idx = line.IndexOf(RecorderScriptBuilder.LinePrefix, StringComparison.Ordinal);
        if (idx < 0)
        {
            // Playwright / Node chatter. Keep a short tail so a crash has a
            // reason, and surface error-looking lines on the live feed.
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (c.Tail)
            {
                c.Tail.Enqueue(line.Length > 500 ? line[..500] : line);
                while (c.Tail.Count > 30) c.Tail.Dequeue();
            }
            if (line.Contains("Error", StringComparison.Ordinal) || line.Contains("✘", StringComparison.Ordinal))
                _registry.Append(sessionId, line);
            return;
        }

        JsonObject? evt;
        try { evt = JsonNode.Parse(line[(idx + RecorderScriptBuilder.LinePrefix.Length)..]) as JsonObject; }
        catch (JsonException) { return; }
        if (evt is null) return;

        PayloadSanitizer.SanitizeEvent(evt);

        var kind = EpisodeBuilder.Str(evt["kind"]) ?? "unknown";
        var seq = EpisodeBuilder.Int(evt["seq"]) ?? 0;
        long t = evt["t"] is JsonValue tv && tv.TryGetValue<long>(out var tl) ? tl : 0;
        var actionId = EpisodeBuilder.Str(evt["actionId"]);

        Interlocked.Increment(ref c.Events);
        if (kind == "action") Interlocked.Increment(ref c.Actions);
        if (kind == "limit") c.Limited = true;
        if (kind == "end") c.EndReason = EpisodeBuilder.Str((evt["data"] as JsonObject)?["reason"]);

        var json = evt.ToJsonString();
        sink.TryWrite(new RecordingEvent(sessionId, seq, t, kind, actionId, json));
        _registry.Append(sessionId, RecorderScriptBuilder.LinePrefix + Compact(evt));
    }

    /// <summary>Live-feed copy: no bodies, effect lists capped — the feed is for humans.</summary>
    private static string Compact(JsonObject evt)
    {
        var copy = (JsonObject)evt.DeepClone();
        if (copy["data"] is JsonObject d)
        {
            d.Remove("reqBody");
            d.Remove("resBody");
            foreach (var key in new[] { "appeared", "disappeared", "changed", "text" })
                if (d[key] is JsonArray arr)
                    while (arr.Count > 5) arr.RemoveAt(arr.Count - 1);
            if (d["target"] is JsonObject t) t.Remove("ancestors");
        }
        return copy.ToJsonString();
    }

    private async Task WriteBatchesAsync(Guid sessionId, ChannelReader<RecordingEvent> reader, Counters c)
    {
        var batch = new List<RecordingEvent>(BatchSize);
        var lastFlush = DateTime.UtcNow;

        async Task Flush()
        {
            if (batch.Count == 0) return;
            try
            {
                using var scope = _scopes.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IRecordingRepository>();
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await repo.AddEventsAsync(batch);
                await uow.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Losing a batch is bad but must not kill the recorder.
                _log.LogError(ex, "Recorder {Session}: failed to persist {Count} events", sessionId, batch.Count);
                c.LastError ??= "Some events could not be saved: " + ex.Message;
            }
            batch.Clear();
            lastFlush = DateTime.UtcNow;
        }

        while (true)
        {
            bool more;
            try
            {
                using var tick = new CancellationTokenSource(BatchInterval);
                more = await reader.WaitToReadAsync(tick.Token);
            }
            catch (OperationCanceledException)
            {
                await Flush();
                continue;
            }
            if (!more) break;
            while (reader.TryRead(out var e))
            {
                batch.Add(e);
                if (batch.Count >= BatchSize) await Flush();
            }
            if (DateTime.UtcNow - lastFlush >= BatchInterval) await Flush();
        }
        await Flush();
    }

    private async Task FinalizeAsync(Guid sessionId, RunnerCompletion completion, Counters c)
    {
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecordingRepository>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var session = await repo.FindByIdUnscopedAsync(sessionId);
        if (session is null) return;   // deleted mid-recording

        var events = await repo.ListAllEventsAsync(sessionId);
        EpisodeBuildResult? episodes = null;
        try { episodes = EpisodeBuilder.Build(events); }
        catch (Exception ex) { _log.LogError(ex, "Recorder {Session}: episode build failed", sessionId); }

        var cleanEnd = c.EndReason is "stopped" or "limit" or "tab-closed";
        if (cleanEnd)
        {
            var note = c.EndReason switch
            {
                "tab-closed" => "The tab was closed; recording ended early.",
                "limit" => "A recording cap (duration, events or size) was reached.",
                _ => c.LastError,
            };
            session.MarkCompleted(
                episodes?.Json ?? string.Empty,
                events.Count,
                episodes?.ActionCount ?? c.Actions,
                c.Limited || (episodes?.Trimmed ?? false),
                note);
        }
        else
        {
            string tail;
            lock (c.Tail) tail = string.Join("\n", c.Tail.TakeLast(8));
            var reason = c.EndReason == "disconnected"
                ? "Lost the connection to Chrome."
                : completion.Kind == RunnerExitKind.Cancelled
                    ? "The recorder did not stop in time and was killed."
                    : c.LastError ?? "The recorder exited unexpectedly.";
            session.MarkFailed(
                string.IsNullOrWhiteSpace(tail) ? reason : $"{reason}\n{tail}",
                events.Count,
                episodes?.ActionCount ?? c.Actions,
                episodes?.Json);
        }
        await uow.SaveChangesAsync();
    }
}
