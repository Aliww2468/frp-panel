using System.Diagnostics;

namespace FrpPanel;

internal sealed class SingleInstance : IDisposable
{
    // One desktop controller per Windows session, independent of install path and port.
    internal const string MutexName = @"Local\FrpPanel-SingleInstance";
    readonly Mutex mutex;
    bool disposed;

    SingleInstance(Mutex mutex) => this.mutex = mutex;

    internal static SingleInstance? TryAcquire(string name = MutexName, Func<bool>? legacyProbe = null)
    {
        var mutex = new Mutex(false, name);
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { mutex.Dispose(); return null; }
        var instance = new SingleInstance(mutex);
        try
        {
            if ((legacyProbe ?? LegacyProcessRunning)()) { instance.Dispose(); return null; }
            return instance;
        }
        catch { instance.Dispose(); throw; }
    }

    static bool LegacyProcessRunning()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName("FrpPanel"))
        {
            using (process)
            {
                try
                {
                    if (process.Id == current.Id || process.SessionId != current.SessionId || process.HasExited) continue;
                    var file = FileVersionInfo.GetVersionInfo(process.MainModule!.FileName);
                    // New versions coordinate through the mutex. Do not mistake another
                    // simultaneous launch's short-lived notification for an older instance.
                    if (file.ProductName == "FRP Panel" && Version.TryParse(file.FileVersion, out var version) &&
                        version >= new Version(1, 2, 1, 0)) continue;
                    return true;
                }
                catch (InvalidOperationException) { /* Process exited during inspection. */ }
                catch (System.ComponentModel.Win32Exception) { return true; }
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
