using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace FrpPanel;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            using var instance = SingleInstance.TryAcquire();
            if (instance == null)
            {
                MessageBox.Show("FRP Panel 已在运行，请从系统托盘打开。\n\n本次启动已取消。",
                    "软件已在运行", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string workspace = FindWorkspace(args);
            int portIndex = Array.IndexOf(args, "--port");
            int port = 17600;
            if (portIndex >= 0 && (portIndex + 1 >= args.Length ||
                !int.TryParse(args[portIndex + 1], out port) || port < 1024 || port > 65535))
                throw new ArgumentException("面板端口必须在 1024 到 65535 之间。");
            string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspace.ToUpperInvariant())))[..16];
            Application.Run(new PanelWindow(workspace, id, args.Contains("--background"), port));
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "FRP Panel 无法启动", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static string FindWorkspace(string[] args)
    {
        int index = Array.IndexOf(args, "--workspace");
        if (index >= 0 && index + 1 < args.Length)
        {
            string explicitPath = Path.GetFullPath(args[index + 1]);
            if (!File.Exists(Path.Combine(explicitPath, "panel", "server.py")))
                throw new InvalidOperationException("找不到 panel/server.py，请保留软件与项目文件的目录结构。");
            return explicitPath.TrimEnd(Path.DirectorySeparatorChar);
        }
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "panel", "server.py")) && Directory.Exists(Path.Combine(directory.FullName, "client")))
                return directory.FullName;
        throw new InvalidOperationException("找不到面板目录，请使用根目录的“启动软件.cmd”。");
    }
}

internal sealed class PanelWindow : Form
{
    readonly string BaseUrl;
    readonly int panelPort;
    readonly string workspace, stateDir, startupName;
    readonly NotifyIcon tray;
    readonly WebView2 view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(248, 249, 251) };
    readonly Label loading = new() { Dock = DockStyle.Fill, Text = "正在启动本地控制台…", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Gray };
    readonly ToolStripMenuItem statusItem = new("正在连接…") { Enabled = false };
    readonly ToolStripMenuItem startItem = new("启动客户端");
    readonly ToolStripMenuItem stopItem = new("停止客户端");
    readonly ToolStripMenuItem startupItem = new("开机自启（登录后驻留托盘）") { CheckOnClick = false };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 6000 };
    readonly HttpClient http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(24) };
    Process? backend;
    UpdateDialog? updateDialog;
    bool exiting, busy, pollBusy, initialized, announced, background;
    string token = "";
    string theme = "light";

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    void ApplyWindowTheme(string selected, bool persist = false)
    {
        if (selected is not ("light" or "dark" or "sand" or "blue")) return;
        bool changed = theme != selected;
        theme = selected;
        var (surface, foreground) = selected switch
        {
            "dark" => ("#19212e", "#e7edf6"),
            "sand" => ("#fffcf5", "#453d32"),
            "blue" => ("#f9fcff", "#283e57"),
            _ => ("#ffffff", "#252a34")
        };
        BackColor = loading.BackColor = view.DefaultBackgroundColor = ColorTranslator.FromHtml(surface);
        loading.ForeColor = ColorTranslator.FromHtml(foreground);
        if (IsHandleCreated)
        {
            int dark = selected == "dark" ? 1 : 0;
            // Older Windows versions ignore unsupported DWM attributes.
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                int caption = ColorTranslator.ToWin32(BackColor);
                int text = ColorTranslator.ToWin32(loading.ForeColor);
                int noBorder = unchecked((int)0xfffffffe);
                DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
                DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));
                DwmSetWindowAttribute(Handle, 34, ref noBorder, sizeof(int));
            }
        }
        // Cache only the theme name so the next launch starts in the same color.
        if (persist && changed)
        {
            try { File.WriteAllText(Path.Combine(stateDir, "theme.txt"), theme); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log(error.Message); }
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowTheme(theme);
    }

    public PanelWindow(string root, string id, bool startHidden, int port)
    {
        panelPort = port; BaseUrl = $"http://127.0.0.1:{port}";
        workspace = root; background = startHidden;
        stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrpPanel", id);
        Directory.CreateDirectory(stateDir);
        startupName = "FrpPanel-" + id;
        try { ApplyWindowTheme(File.ReadAllText(Path.Combine(stateDir, "theme.txt")).Trim()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ApplyWindowTheme("light"); }
        Text = "FRP Panel · 本地控制台";
        Size = new Size(1240, 880); MinimumSize = new Size(840, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        Controls.Add(view); Controls.Add(loading);
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开控制台", null, (_, _) => ShowPanel());
        menu.Items.Add(statusItem); menu.Items.Add(new ToolStripSeparator());
        startItem.Click += async (_, _) => await ControlClient("start");
        stopItem.Click += async (_, _) => await ControlClient("stop");
        menu.Items.Add(startItem); menu.Items.Add(stopItem);
        menu.Items.Add(new ToolStripSeparator());
        startupItem.Click += (_, _) => ToggleStartup();
        menu.Items.Add(startupItem);
        menu.Items.Add("打开配置目录", null, (_, _) => Process.Start(new ProcessStartInfo(Path.Combine(workspace, "client")) { UseShellExecute = true }));
        menu.Items.Add("检查更新", null, (_, _) => ShowUpdates());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出软件并停止转发", null, async (_, _) => await ExitPanel());
        menu.Opening += (_, _) => startupItem.Checked = StartupEnabled();
        tray = new NotifyIcon { Icon = Icon, Text = "FRP Panel · 启动中", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ShowPanel();
        Shown += async (_, _) => await InitializePanel();
        FormClosing += (_, e) =>
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideToTray(); }
        };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };
        timer.Tick += async (_, _) => await PollStatus();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (background && !IsHandleCreated)
        {
            CreateHandle();
            base.SetVisibleCore(false);
            _ = InitializePanel();
            return;
        }
        base.SetVisibleCore(value);
    }

    async Task<bool> IsBackendReady()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var response = await http.GetAsync(BaseUrl + "/api/health", timeout.Token);
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            if (root.GetProperty("app").GetString() != "frp-panel" ||
                !string.Equals(Path.GetFullPath(root.GetProperty("workspace").GetString()!), workspace, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{panelPort} 端口属于另一个面板目录，请先退出那个面板。");
            return root.GetProperty("desktopApi").GetInt32() >= 1;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) { return false; }
        catch (JsonException) { return false; }
    }

    async Task EnsureBackend()
    {
        if (await IsBackendReady()) return;
        if (backend != null && !backend.HasExited) return;
        string python = Path.Combine(AppContext.BaseDirectory, "python", "python.exe");
        if (!File.Exists(python)) throw new FileNotFoundException("缺少随软件附带的 Python 运行文件，请重新运行 desktop/build.ps1。");
        var info = new ProcessStartInfo(python) { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(workspace, "panel", "server.py"));
        info.ArgumentList.Add("--port"); info.ArgumentList.Add(panelPort.ToString());
        backend = Process.Start(info) ?? throw new InvalidOperationException("无法启动后台面板。");
        backend.OutputDataReceived += (_, _) => { };
        backend.ErrorDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
        backend.BeginOutputReadLine(); backend.BeginErrorReadLine();
        for (int attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(250);
            if (backend.HasExited) throw new InvalidOperationException($"后台面板启动失败，{panelPort} 端口可能已占用。请查看桌面日志：" + stateDir);
            if (await IsBackendReady()) return;
        }
        throw new InvalidOperationException("后台面板启动超时，请检查 panel/server.py 和 client/frpc.toml。");
    }

    async Task InitializePanel()
    {
        if (initialized) return;
        initialized = true;
        try
        {
            await EnsureBackend();
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(stateDir, "WebView2"));
            await view.EnsureCoreWebView2Async(environment);
            view.CoreWebView2.Settings.IsStatusBarEnabled = false;
            view.CoreWebView2.Settings.AreDevToolsEnabled = false;
            view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            view.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            view.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            view.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var url) || url.Scheme != "http" || url.Host != "127.0.0.1" || url.Port != panelPort)
                    e.Cancel = true;
            };
            view.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            view.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            view.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) ||
                    source.GetLeftPart(UriPartial.Authority) != BaseUrl) return;
                try
                {
                    string message = e.TryGetWebMessageAsString();
                    if (message.StartsWith("theme:", StringComparison.Ordinal))
                        ApplyWindowTheme(message[6..], persist: true);
                    else if (message == "update:open") ShowUpdates();
                }
                catch (ArgumentException) { }
            };
            view.CoreWebView2.NavigationCompleted += (_, e) => { if (e.IsSuccess) loading.Visible = false; };
            view.Source = new Uri(BaseUrl);
            await PollStatus(); timer.Start();
            if (background) HideToTray();
        }
        catch (Exception error)
        {
            Log(error.ToString()); loading.Text = "启动失败\n\n" + error.Message;
            tray.Text = "FRP Panel · 启动失败";
            ShowPanel();
        }
    }

    async Task<JsonElement> State()
    {
        using var response = await http.GetAsync(BaseUrl + "/api/state");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        token = json.RootElement.GetProperty("token").GetString()!;
        return json.RootElement.Clone();
    }

    async Task PollStatus()
    {
        if (pollBusy || busy || exiting) return;
        pollBusy = true;
        try
        {
            var state = await State();
            bool configured = state.GetProperty("configured").GetBoolean();
            bool active = state.GetProperty("connected").GetBoolean() || state.GetProperty("processRunning").GetBoolean();
            string label = !configured ? "待添加连接配置" : active ? "客户端运行中" : "客户端已停止";
            statusItem.Text = label; tray.Text = "FRP Panel · " + label;
            startItem.Enabled = configured && !active; stopItem.Enabled = active;
        }
        catch (Exception error)
        {
            statusItem.Text = "后台暂时未连接"; tray.Text = "FRP Panel · 后台未连接";
            startItem.Enabled = stopItem.Enabled = false;
            Log(error.Message);
        }
        finally { pollBusy = false; }
    }

    async Task Post(string path)
    {
        await State();
        using var message = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path) { Content = JsonContent.Create(new { }) };
        message.Headers.Add("X-Panel-Token", token);
        using var response = await http.SendAsync(message);
        if (!response.IsSuccessStatusCode)
        {
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            throw new InvalidOperationException(data.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "操作未完成");
        }
    }

    async Task ControlClient(string action)
    {
        if (busy) return; busy = true;
        try { await Post("/api/control/" + action); tray.ShowBalloonTip(2000, "FRP Panel", action == "start" ? "客户端启动请求已发送。" : "客户端停止请求已发送。", ToolTipIcon.Info); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { busy = false; await PollStatus(); }
    }

    void ShowPanel()
    {
        background = false; Show(); WindowState = FormWindowState.Normal; Activate();
    }

    void ShowUpdates()
    {
        if (busy || exiting) return;
        ShowPanel();
        if (updateDialog != null && !updateDialog.IsDisposed) { updateDialog.Activate(); return; }
        updateDialog = new UpdateDialog(Path.Combine(stateDir, "Updates"), BackColor, loading.ForeColor, InstallUpdate);
        updateDialog.Show(this);
    }

    async Task InstallUpdate(DownloadedUpdate update)
    {
        if (busy || exiting) throw new InvalidOperationException("正在执行其他操作，请稍后重试。");
        string expectedApp = Path.GetFullPath(Path.Combine(workspace, "desktop", "app")).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(expectedApp, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("程序与配置位于不同目录，请使用安装包手动更新。");
        busy = true;
        string nonce = Guid.NewGuid().ToString("N");
        string eventName = @"Local\FrpPanel-Update-" + nonce;
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName + "-Ready");
        using var commit = new EventWaitHandle(false, EventResetMode.ManualReset, eventName + "-Commit");
        using var abort = new EventWaitHandle(false, EventResetMode.ManualReset, eventName + "-Abort");
        Process? shutdownBackend = null;
        bool backendStopped = false;
        try
        {
            using (var input = File.OpenRead(update.Path))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input)).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包已发生变化，请重新下载。");
            if (!Version.TryParse(FileVersionInfo.GetVersionInfo(update.Path).FileVersion, out var fileVersion) ||
                new Version(fileVersion.Major, fileVersion.Minor, Math.Max(0, fileVersion.Build)) != update.Release.Version)
                throw new InvalidDataException("安装包版本与更新信息不一致。");
            if (!await IsBackendReady()) throw new InvalidOperationException("后台暂时未连接，请恢复连接后重试更新。");
            using var health = JsonDocument.Parse(await http.GetStringAsync(BaseUrl + "/api/health"));
            int backendPid = health.RootElement.GetProperty("pid").GetInt32();
            shutdownBackend = Process.GetProcessById(backendPid);
            shutdownBackend.EnableRaisingEvents = true;
            var start = new ProcessStartInfo(update.Path) { UseShellExecute = false };
            foreach (string argument in new[] { "/SP-", "/SILENT", "/NORESTART", "/NOCLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS",
                "/DIR=" + workspace, "/FRPUPDATE=" + nonce, "/FRPPID=" + Environment.ProcessId,
                "/FRPBACKENDPID=" + backendPid, "/FRPPORT=" + panelPort, "/LOG=" + Path.Combine(Path.GetDirectoryName(update.Path)!, "install.log") })
                start.ArgumentList.Add(argument);
            using var installer = Process.Start(start) ?? throw new InvalidOperationException("无法启动安装程序。");
            var timeout = Stopwatch.StartNew();
            while (!ready.WaitOne(0))
            {
                if (installer.HasExited || timeout.Elapsed > TimeSpan.FromSeconds(40))
                    throw new InvalidOperationException("安装程序未准备好，当前软件保持运行，请重试。");
                await Task.Delay(100);
            }
            // The installer is waiting on our process handles and will not touch files
            // unless shutdown succeeds and we explicitly commit this handoff.
            await Post("/api/desktop/exit");
            backendStopped = true;
            if (installer.HasExited) throw new InvalidOperationException("安装已取消，请重试更新。");
            commit.Set();
            exiting = true; timer.Stop(); tray.Visible = false; Close();
        }
        catch
        {
            abort.Set();
            if (backendStopped && !exiting)
            {
                try
                {
                    await shutdownBackend!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    backend?.Dispose(); backend = null;
                    await EnsureBackend();
                }
                catch (Exception recoveryError) { Log(recoveryError.Message); }
            }
            throw;
        }
        finally { shutdownBackend?.Dispose(); busy = false; }
    }

    void HideToTray()
    {
        Hide();
        if (!announced) { announced = true; tray.ShowBalloonTip(3500, "FRP Panel 已在后台运行", "关闭窗口不会停止转发。双击系统托盘中的 FRP Panel 图标即可恢复窗口。", ToolTipIcon.Info); }
    }

    bool StartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue(startupName) is string value && value.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase);
    }

    void ToggleStartup()
    {
        try
        {
            bool enabled = StartupEnabled();
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled) key.DeleteValue(startupName, false);
            else key.SetValue(startupName, $"\"{Environment.ProcessPath}\" --workspace \"{workspace}\" --background");
            startupItem.Checked = !enabled;
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "无法修改开机自启"); }
    }

    async Task ExitPanel()
    {
        if (busy || exiting) return; busy = true;
        try
        {
            if (await IsBackendReady()) await Post("/api/desktop/exit");
            else if (backend != null && !backend.HasExited)
                throw new InvalidOperationException("后台仍在运行但暂时无法连接，请稍后重试退出。");
            exiting = true; timer.Stop(); tray.Visible = false; Close();
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "未退出：请先确认客户端已停止", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { busy = false; }
    }

    void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(stateDir, "desktop.log"), DateTime.Now.ToString("s") + " " + text + Environment.NewLine); }
        catch (IOException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); tray.Dispose(); view.Dispose(); http.Dispose(); backend?.Dispose(); }
        base.Dispose(disposing);
    }
}
