using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SigmaFp.Camera;

/// <summary>
/// The single thread that may touch the camera (hard rule 1). Work items run one at a time.
/// A watchdog checks each item against its time limit; if one hangs (e.g. a native USB call
/// that never returns), <see cref="Hung"/> fires once and this worker must be abandoned:
/// the stuck thread is a background thread and is simply left behind.
/// </summary>
internal sealed class CameraWorker : IDisposable
{
    sealed record Item(string Name, Action Action, TimeSpan Limit);

    readonly BlockingCollection<Item> _queue = new();
    readonly Thread _thread;
    readonly Timer _watchdog;
    readonly ILogger _log;
    readonly List<Timer> _timers = new();
    readonly object _lock = new();

    string? _currentName;
    long _currentStart;
    TimeSpan _currentLimit;
    bool _hung, _disposed;

    /// <summary>Raised once, from a timer thread, when a work item exceeds its limit.</summary>
    public event Action<string, TimeSpan>? Hung;

    public int ManagedThreadId => _thread.ManagedThreadId;
    public bool IsHung => _hung;

    public CameraWorker(ILogger log, string name)
    {
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
        _watchdog = new Timer(_ => CheckWatchdog(), null, 250, 250);
    }

    public bool IsWorkerThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    public void Post(string name, Action action, TimeSpan limit)
    {
        lock (_lock)
        {
            if (_disposed || _hung) return;
            _queue.Add(new Item(name, action, limit));
        }
    }

    public void PostDelayed(TimeSpan delay, string name, Action action, TimeSpan limit)
    {
        lock (_lock)
        {
            if (_disposed || _hung) return;
            Timer? t = null;
            t = new Timer(_ =>
            {
                lock (_lock) { _timers.Remove(t!); }
                t!.Dispose();
                Post(name, action, limit);
            }, null, delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
            _timers.Add(t);
        }
    }

    /// <summary>Runs <paramref name="fn"/> on the worker and waits for it (from a non-worker thread).</summary>
    public T Invoke<T>(string name, Func<T> fn, TimeSpan limit, TimeSpan wait)
    {
        if (IsWorkerThread) return fn();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(name, () =>
        {
            try { tcs.SetResult(fn()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, limit);
        if (_hung || _disposed) throw new CameraCommException($"{name}: camera worker is not available");
        if (Task.WhenAny(tcs.Task, Task.Delay(wait)).GetAwaiter().GetResult() != tcs.Task)
            throw new TimeoutException($"{name}: no result within {wait.TotalSeconds:0} s");
        return tcs.Task.GetAwaiter().GetResult();
    }

    public void Invoke(string name, Action fn, TimeSpan limit, TimeSpan wait) =>
        Invoke(name, () => { fn(); return 0; }, limit, wait);

    void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            lock (_lock) { _currentName = item.Name; _currentLimit = item.Limit; _currentStart = Stopwatch.GetTimestamp(); }
            try { item.Action(); }
            catch (Exception ex) { _log.LogError(ex, "Unhandled error in camera work item {Item}", item.Name); }
            lock (_lock) { _currentName = null; }
            if (_hung) return; // replaced while we were stuck; never touch the camera again
        }
    }

    void CheckWatchdog()
    {
        string? name; TimeSpan elapsed, limit;
        lock (_lock)
        {
            if (_hung || _disposed || _currentName is null) return;
            name = _currentName;
            limit = _currentLimit;
            elapsed = Stopwatch.GetElapsedTime(_currentStart);
            if (elapsed <= limit) return;
            _hung = true;
        }
        _log.LogError("Camera worker hung: '{Item}' running for {Elapsed:0.0} s (limit {Limit:0} s); abandoning thread", name, elapsed.TotalSeconds, limit.TotalSeconds);
        Hung?.Invoke(name, elapsed);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var t in _timers) t.Dispose();
            _timers.Clear();
            _queue.CompleteAdding();
        }
        _watchdog.Dispose();
    }
}
