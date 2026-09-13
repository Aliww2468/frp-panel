using System.Diagnostics;
using System.Security.Cryptography;

if (args.Length == 1 && args[0] == "hold") { Console.WriteLine("READY"); Console.ReadLine(); return; }
if (args.Length != 2) throw new ArgumentException("Pass a test installer built with /DInstallerTest=1 and an isolated install directory.");
string setup = Path.GetFullPath(args[0]), target = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.Combine(target, "client"));
string config = Path.Combine(target, "client", "frpc.toml");
if (File.Exists(config)) throw new InvalidOperationException("Use a fresh fixture directory to avoid touching existing configuration.");
File.WriteAllText(config, "serverAddr = \"\"\n# preserved-by-update-test\n");
byte[] original = File.ReadAllBytes(config);
string installedExe = Path.Combine(target, "desktop", "app", "FrpPanel.exe");

void Check(bool success, string message) { if (!success) throw new Exception(message); }
Process Holder()
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
    start.ArgumentList.Add("hold");
    var child = Process.Start(start)!;
    Check(child.StandardOutput.ReadLine() == "READY", "Fixture process did not start.");
    return child;
}

foreach (bool cancel in new[] { true, false })
{
    string nonce = Guid.NewGuid().ToString("N"), prefix = @"Local\FrpPanel-Update-" + nonce;
    using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-Ready");
    using var commit = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-Commit");
    using var abort = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-Abort");
    using var parent = Holder(); using var backend = Holder();
    var start = new ProcessStartInfo(setup) { UseShellExecute = false, CreateNoWindow = true };
    foreach (string argument in new[] { "/SP-", "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/TASKS=", "/GROUP=FRP Panel Update Test",
        "/DIR=" + target, "/FRPUPDATE=" + nonce, "/FRPPID=" + parent.Id, "/FRPBACKENDPID=" + backend.Id, "/FRPPORT=17621",
        "/LOG=" + Path.Combine(target, cancel ? "abort-test.log" : "commit-test.log") }) start.ArgumentList.Add(argument);
    using var installer = Process.Start(start)!;
    try
    {
        Check(ready.WaitOne(30000), "Installer did not signal readiness.");
        Check(!File.Exists(installedExe), "Installer modified files before handoff.");
        if (cancel)
        {
            abort.Set();
            Check(installer.WaitForExit(20000), "Cancelled installer did not exit.");
            Check(installer.ExitCode != 0 && !File.Exists(installedExe), "Cancelled update modified files.");
        }
        else
        {
            commit.Set();
            Thread.Sleep(600);
            Check(!File.Exists(installedExe), "Installer did not wait for the old processes.");
            parent.StandardInput.WriteLine(); parent.StandardInput.Flush();
            backend.StandardInput.WriteLine(); backend.StandardInput.Flush();
            Check(parent.WaitForExit(5000) && backend.WaitForExit(5000), "Fixture processes did not exit.");
            Check(installer.WaitForExit(30000) && installer.ExitCode == 0, "Committed update did not install.");
            Check(File.Exists(installedExe), "Updated executable missing.");
        }
        Check(File.ReadAllBytes(config).SequenceEqual(original), "Configuration was overwritten.");
    }
    finally
    {
        abort.Set();
        foreach (var fixture in new[] { parent, backend })
            if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(5000); }
        if (!installer.HasExited) installer.WaitForExit(5000);
    }
}
Console.WriteLine("PASS: cancellation leaves files untouched; commit waits for both processes; installation succeeds and preserves configuration.");
