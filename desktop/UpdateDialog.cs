using System.Runtime.InteropServices;

namespace FrpPanel;

internal sealed class UpdateDialog : Form
{
    readonly UpdateService service = new();
    readonly CancellationTokenSource lifetime = new();
    readonly Label status = new() { Dock = DockStyle.Top, Height = 52, Text = "正在检查更新…" };
    readonly TextBox notes = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None };
    readonly ProgressBar progress = new() { Dock = DockStyle.Bottom, Height = 8 };
    readonly Button action = new() { AutoSize = true, Text = "检查更新", Enabled = false };
    readonly string cache;
    readonly Version current;
    readonly Func<DownloadedUpdate, Task> install;
    AppRelease? release;
    DownloadedUpdate? downloaded;
    bool working, installing;

    public UpdateDialog(string cacheDirectory, Color surface, Color foreground, Func<DownloadedUpdate, Task> apply)
    {
        cache = cacheDirectory; install = apply;
        current = typeof(Program).Assembly.GetName().Version!;
        Text = "软件更新"; Size = new Size(510, 410); MinimumSize = Size;
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MaximizeBox = false; MinimizeBox = false;
        BackColor = notes.BackColor = surface; ForeColor = notes.ForeColor = foreground;
        action.FlatStyle = FlatStyle.Flat; action.BackColor = surface; action.ForeColor = foreground;
        Padding = new Padding(22);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        footer.Controls.Add(action);
        Controls.Add(notes); Controls.Add(progress); Controls.Add(footer); Controls.Add(status);
        action.Click += async (_, _) => await Act();
        Shown += async (_, _) => await Check();
        FormClosing += (_, e) => { if (installing && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; else lifetime.Cancel(); };
    }

    async Task Check()
    {
        working = true; action.Enabled = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            release = await service.Check(current, timeout.Token);
            if (IsDisposed) return;
            status.Text = $"当前版本 {current.ToString(3)}\n" + (release == null ? "已是最新版本。" : $"发现新版本 {release.Version} · {release.Size / 1048576.0:F1} MB");
            notes.Text = release?.Notes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) ?? "";
            action.Text = release == null ? "重新检查" : "下载更新";
        }
        catch (Exception error) { ShowError(error); }
        finally { working = false; if (!IsDisposed) action.Enabled = true; }
    }

    async Task Act()
    {
        if (working) return;
        if (release == null) { await Check(); return; }
        if (downloaded != null)
        {
            if (MessageBox.Show(this, "请先保存配置。更新将停止当前转发并退出软件，安装完成后重新打开。\n已保存的配置会保留，转发需要手动重新启动。\n\n现在安装更新吗？",
                "安装更新", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            installing = working = true; action.Enabled = false;
            try { status.Text = "正在退出后台并准备安装…"; await install(downloaded); }
            catch (Exception error) { installing = false; ShowError(error); }
            finally { working = false; if (!IsDisposed) action.Enabled = true; }
            return;
        }
        working = true; action.Enabled = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            var report = new Progress<int>(value => { if (!IsDisposed) { progress.Value = value; status.Text = $"正在下载 {release.Version} · {value}%"; } });
            downloaded = await service.Download(release, cache, report, timeout.Token);
            if (!IsDisposed) { status.Text = "下载完成，校验通过。"; action.Text = "安装并重启"; }
        }
        catch (Exception error) { ShowError(error); }
        finally { working = false; if (!IsDisposed) action.Enabled = true; }
    }

    void ShowError(Exception error)
    {
        if (IsDisposed || lifetime.IsCancellationRequested) return;
        status.Text = error is OperationCanceledException ? "连接超时，请重试。" : "操作未完成：" + error.Message;
        if (release == null) action.Text = "重新检查";
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        int surface = ColorTranslator.ToWin32(BackColor), text = ColorTranslator.ToWin32(ForeColor), border = unchecked((int)0xfffffffe);
        DwmSetWindowAttribute(Handle, 35, ref surface, sizeof(int));
        DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));
        DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed) { lifetime.Cancel(); lifetime.Dispose(); service.Dispose(); }
        base.Dispose(disposing);
    }
}
