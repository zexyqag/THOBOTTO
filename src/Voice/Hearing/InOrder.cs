namespace THOBOTTO.Voice.Hearing;

// Work run in order per key (a channel's sentences, one after the other) while keys go side by side.
public sealed class InOrder
{
    private readonly Dictionary<ulong, Task> _last = [];
    private readonly Lock _lock = new();

    // Runs the work once the key's earlier work is done, whether that went well or not.
    public Task Run(ulong key, Func<Task> work)
    {
        lock (_lock)
        {
            var previous = _last.GetValueOrDefault(key) ?? Task.CompletedTask;
            var next = previous.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            _last[key] = next;
            _ = next.ContinueWith(_ => Forget(key, next), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return next;
        }
    }

    public int Pending
    {
        get
        {
            lock (_lock)
                return _last.Count;
        }
    }

    private void Forget(ulong key, Task done)
    {
        lock (_lock)
        {
            if (_last.TryGetValue(key, out var last) && last == done)
                _last.Remove(key);
        }
    }
}
