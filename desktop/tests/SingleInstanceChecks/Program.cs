using System.Diagnostics;
using FrpPanel;

if (args.Length > 0)
{
    using var start = EventWaitHandle.OpenExisting(args[1]);
    Console.WriteLine("READY");
    start.WaitOne();
    using var instance = SingleInstance.TryAcquire(args[0], () => false);
    Console.WriteLine(instance == null ? "BLOCKED" : "ACQUIRED");
    if (instance != null) Console.ReadLine();
    return;
}

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

string key = @"Local\FrpPanel-InstanceTest-" + Guid.NewGuid().ToString("N");
using var startGate = new EventWaitHandle(false, EventResetMode.ManualReset, key + "-start");
Process StartChild(string directory)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) {
        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
        RedirectStandardInput = true, RedirectStandardOutput = true
    };
    info.ArgumentList.Add(key);
    info.ArgumentList.Add(key + "-start");
    return Process.Start(info)!;
}

var children = new List<Process>();
try
{
    for (int i = 0; i < 8; i++)
    {
        var child = StartChild(i % 2 == 0 ? Environment.CurrentDirectory : Path.GetTempPath());
        children.Add(child);
        Check(child.StandardOutput.ReadLine() == "READY", "Child failed to initialize.");
    }
    startGate.Set();
    var results = children.Select(p => (Process: p, State: p.StandardOutput.ReadLine())).ToList();
    Check(results.Count(r => r.State == "ACQUIRED") == 1, "Concurrent starts must have exactly one owner.");
    Check(results.Count(r => r.State == "BLOCKED") == 7, "All duplicate starts must be blocked.");
    var owner = results.Single(r => r.State == "ACQUIRED").Process;
    foreach (var duplicate in results.Where(r => r.State == "BLOCKED"))
        Check(duplicate.Process.WaitForExit(5000), "Duplicate did not exit.");

    // Keep a handle open so terminating the owner leaves an abandoned mutex.
    using var keepHandle = new Mutex(false, key);
    owner.Kill();
    Check(owner.WaitForExit(5000), "Owner did not exit.");
    using (var recovered = SingleInstance.TryAcquire(key, () => false))
        Check(recovered != null, "A crash must not permanently block startup.");
    using (var legacy = SingleInstance.TryAcquire(key, () => true))
        Check(legacy == null, "An older running version must prevent startup.");
    using (var clean = SingleInstance.TryAcquire(key, () => false))
        Check(clean != null, "Normal disposal must release the lock.");
    Console.WriteLine("PASS: concurrent starts, different working directories, duplicate exit, crash recovery, legacy detection and lock release.");
}
finally
{
    foreach (var child in children)
    {
        if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); }
        child.Dispose();
    }
}
