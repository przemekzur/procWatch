using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ProcLens;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string StartupKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "ProcLens";

    private readonly NotifyIcon _trayIcon;
    private readonly Icon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly Options _options;
    private CancellationTokenSource? _collectorCancellation;
    private Task<int>? _collectorTask;
    private bool _stopping;

    public TrayApplicationContext(Options options, bool openDashboard)
    {
        _options = options;
        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _pauseItem = new ToolStripMenuItem("Pause collection", null, (_, _) => ToggleCollection());
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            Checked = IsStartupEnabled(),
            CheckOnClick = false
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open dashboard", null, (_, _) => OpenDashboard());
        menu.Items.Add(_pauseItem);
        menu.Items.Add("Open data folder", null, (_, _) => OpenDataFolder());
        menu.Items.Add("Diagnostics", null, (_, _) => ShowDiagnostics());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit ProcLens", null, async (_, _) => await ExitAsync());

        _icon = TrayIconFactory.Create();
        _trayIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "ProcLens — starting",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => OpenDashboard();

        StartCollector();
        if (openDashboard)
            _ = OpenDashboardWhenReadyAsync();
    }

    private void StartCollector()
    {
        _collectorCancellation = new CancellationTokenSource();
        _collectorTask = Task.Run(() => Program.CollectAsync(_options, _collectorCancellation.Token));
        _statusItem.Text = "Collecting";
        _pauseItem.Text = "Pause collection";
        _trayIcon.Text = "ProcLens — collecting";
        _ = ObserveCollectorAsync(_collectorTask);
    }

    private async Task ObserveCollectorAsync(Task<int> task)
    {
        try
        {
            var result = await task;
            if (_stopping || _collectorCancellation?.IsCancellationRequested == true) return;
            Ui(() =>
            {
                _statusItem.Text = $"Collector stopped (code {result})";
                _trayIcon.Text = "ProcLens — collector stopped";
                _pauseItem.Text = "Resume collection";
            });
        }
        catch (Exception ex)
        {
            if (_stopping) return;
            Ui(() =>
            {
                _statusItem.Text = "Collector error";
                _trayIcon.Text = "ProcLens — collector error";
                _trayIcon.ShowBalloonTip(5000, "ProcLens collector stopped", ex.Message, ToolTipIcon.Error);
                _pauseItem.Text = "Resume collection";
            });
        }
    }

    private async void ToggleCollection()
    {
        if (_collectorTask is { IsCompleted: false })
        {
            _collectorCancellation?.Cancel();
            try { await _collectorTask; } catch { }
            _statusItem.Text = "Collection paused";
            _pauseItem.Text = "Resume collection";
            _trayIcon.Text = "ProcLens — paused";
            return;
        }
        StartCollector();
    }

    private void ToggleStartup()
    {
        var enable = !IsStartupEnabled();
        SetStartup(enable);
        _startupItem.Checked = enable;
    }

    internal static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, false);
        return key?.GetValue(StartupValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    internal static void SetStartup(bool enabled, string? executablePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupKeyPath, true);
        if (enabled)
        {
            var executable = executablePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate ProcLens executable.");
            key.SetValue(StartupValueName, $"\"{executable}\" tray --background");
        }
        else
        {
            key.DeleteValue(StartupValueName, false);
        }
    }

    private async Task OpenDashboardWhenReadyAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if ((await client.GetAsync($"http://127.0.0.1:{_options.DashboardPort}/health")).IsSuccessStatusCode)
                    break;
            }
            catch { }
            await Task.Delay(150);
        }
        Ui(OpenDashboard);
    }

    private void OpenDashboard() => Program.OpenDashboard(_options);

    private void OpenDataFolder()
    {
        Directory.CreateDirectory(_options.DataDirectory);
        Process.Start(new ProcessStartInfo(_options.DataDirectory) { UseShellExecute = true });
    }

    private void ShowDiagnostics()
    {
        Directory.CreateDirectory(_options.DataDirectory);
        var database = Path.Combine(_options.DataDirectory, "proclens.db");
        var size = File.Exists(database) ? new FileInfo(database).Length / 1024d / 1024d : 0;
        var state = _collectorTask is { IsCompleted: false } ? "Collecting" : "Paused or stopped";
        MessageBox.Show(
            $"Status: {state}\nDashboard: http://127.0.0.1:{_options.DashboardPort}\n" +
            $"Start with Windows: {(IsStartupEnabled() ? "Yes" : "No")}\nDatabase: {size:F1} MB\n" +
            $"Retention: {_options.RetentionDays} days\nCommand lines stored: {(_options.CaptureCommandLines ? "Yes (opt-in)" : "No")}",
            "ProcLens diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task ExitAsync()
    {
        if (_stopping) return;
        _stopping = true;
        _collectorCancellation?.Cancel();
        if (_collectorTask is not null)
        {
            try { await _collectorTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _icon.Dispose();
        ExitThread();
    }

    private void Ui(Action action)
    {
        if (_trayIcon.ContextMenuStrip?.IsHandleCreated == true)
            _trayIcon.ContextMenuStrip.BeginInvoke(action);
        else
            action();
    }
}
