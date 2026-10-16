using System.Security.Principal;

namespace CashGear.Backup.Desktop;

/// <summary>
/// One app per user per Windows session. A second start signals the first to show its window, then exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    /// <summary>"Local\" is per session; the user's SID keeps "run as another user" in the same session apart.</summary>
    private static string BaseName { get; } = $@"Local\CashGear.Backup.Desktop.{WindowsIdentity.GetCurrent().User?.Value}";

    /// <summary>Null when another instance already runs.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, BaseName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, BaseName + ".Activate"));
    }

    /// <summary>Asks the running instance to show its window.</summary>
    public static void ActivateFirst()
    {
        if (EventWaitHandle.TryOpenExisting(BaseName + ".Activate", out var activate))
        {
            using (activate)
            {
                activate.Set();
            }
        }
    }

    /// <summary>Runs <paramref name="action"/> (on a pool thread) each time another start asks for the window.</summary>
    public void OnActivate(Action action) =>
        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => action(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
