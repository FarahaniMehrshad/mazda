using System.Collections.Concurrent;
using System.Threading.Channels;
using Mazda.Application.Common.Abstractions;

namespace Mazda.Infrastructure.Runner;

/// <summary>
/// In-process registry holding the live log buffer + subscriber channels for
/// each running script execution. Singleton — shared between the runner that
/// writes lines and the SSE endpoint that streams them out.
/// </summary>
public sealed class InMemoryExecutionRegistry : IExecutionRegistry
{
    private sealed class State
    {
        public readonly List<string> Lines = new();
        public readonly List<Channel<string>> Subscribers = new();
        public readonly object Gate = new();
        public bool Completed;
    }

    private readonly ConcurrentDictionary<Guid, State> _byGeneration = new();

    private State GetOrCreate(Guid id) => _byGeneration.GetOrAdd(id, _ => new State());

    public void Reset(Guid generationId)
    {
        var s = GetOrCreate(generationId);
        List<Channel<string>> stragglers;
        lock (s.Gate)
        {
            s.Lines.Clear();
            s.Completed = false;
            // Any lingering subscriber from a previous run gets closed cleanly.
            stragglers = s.Subscribers.ToList();
            s.Subscribers.Clear();
        }
        foreach (var ch in stragglers) ch.Writer.TryComplete();
    }

    public void Append(Guid generationId, string line)
    {
        var s = GetOrCreate(generationId);
        List<Channel<string>> subs;
        lock (s.Gate)
        {
            if (s.Completed) return;
            s.Lines.Add(line);
            subs = s.Subscribers.ToList();
        }
        foreach (var ch in subs)
            ch.Writer.TryWrite(line);
    }

    public void Complete(Guid generationId, RunnerCompletion _)
    {
        if (!_byGeneration.TryGetValue(generationId, out var s)) return;
        List<Channel<string>> subs;
        lock (s.Gate)
        {
            if (s.Completed) return;
            s.Completed = true;
            subs = s.Subscribers.ToList();
            s.Subscribers.Clear();
        }
        foreach (var ch in subs)
            ch.Writer.TryComplete();
    }

    public IReadOnlyList<string> GetLog(Guid generationId)
    {
        if (!_byGeneration.TryGetValue(generationId, out var s)) return Array.Empty<string>();
        lock (s.Gate) return s.Lines.ToList();
    }

    public bool IsRunning(Guid generationId)
    {
        if (!_byGeneration.TryGetValue(generationId, out var s)) return false;
        lock (s.Gate) return !s.Completed;
    }

    public bool Exists(Guid id) => _byGeneration.ContainsKey(id);

    public void Forget(Guid id)
    {
        if (!_byGeneration.TryRemove(id, out var s)) return;
        List<Channel<string>> subs;
        lock (s.Gate)
        {
            s.Completed = true;
            subs = s.Subscribers.ToList();
            s.Subscribers.Clear();
            s.Lines.Clear();
        }
        foreach (var ch in subs) ch.Writer.TryComplete();
    }

    public async IAsyncEnumerable<string> Subscribe(
        Guid generationId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var s = GetOrCreate(generationId);

        // Snapshot existing lines first so a late subscriber sees the whole log.
        List<string> backlog;
        Channel<string>? channel = null;
        bool alreadyCompleted;
        lock (s.Gate)
        {
            backlog = s.Lines.ToList();
            alreadyCompleted = s.Completed;
            if (!alreadyCompleted)
            {
                channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                });
                s.Subscribers.Add(channel);
            }
        }

        foreach (var line in backlog)
        {
            ct.ThrowIfCancellationRequested();
            yield return line;
        }

        if (alreadyCompleted || channel is null) yield break;

        try
        {
            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var line))
                    yield return line;
            }
        }
        finally
        {
            lock (s.Gate) s.Subscribers.Remove(channel);
        }
    }
}
