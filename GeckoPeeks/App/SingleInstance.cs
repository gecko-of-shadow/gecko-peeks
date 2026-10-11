namespace GeckoPeeks.App;

/// <summary>
/// Named mutex plus an auto-reset event. A second launch signals the first
/// and returns without starting another scheduler.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\GeckoPeeks.SingleInstance";
    public const string ShowEventName = @"Local\GeckoPeeks.ShowWindow";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _show;
    private int _stop;

    private SingleInstance(Mutex? mutex, EventWaitHandle? show, bool isFirst)
    {
        _mutex = mutex;
        _show = show;
        IsFirst = isFirst;
    }

    public bool IsFirst { get; }

    public static SingleInstance Acquire()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
            if (!created)
            {
                try
                {
                    using var existing = EventWaitHandle.OpenExisting(ShowEventName);
                    existing.Set();
                }
                catch
                {
                    // The first instance is not listening yet. Exiting still avoids a second scheduler.
                }

                mutex.Dispose();
                return new SingleInstance(null, null, false);
            }

            var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            return new SingleInstance(mutex, show, true);
        }
        catch
        {
            // If the mutex cannot be created, keep the monitor running rather than quit silently.
            return new SingleInstance(null, null, true);
        }
    }

    public void Listen(Action showWindow)
    {
        if (_show == null)
            return;

        var handle = _show;
        Task.Run(() =>
        {
            while (Volatile.Read(ref _stop) == 0)
            {
                try
                {
                    if (!handle.WaitOne())
                        continue;
                }
                catch
                {
                    break;
                }

                if (Volatile.Read(ref _stop) != 0)
                    break;

                try
                {
                    showWindow();
                }
                catch
                {
                    // Showing the window must not tear down the monitor.
                }
            }
        });
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stop, 1);
        try
        {
            _show?.Set();
        }
        catch
        {
            // The listener exits on the next wait failure.
        }

        _show?.Dispose();
        _mutex?.Dispose();
    }
}
