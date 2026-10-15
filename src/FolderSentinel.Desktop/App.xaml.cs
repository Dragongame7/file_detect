using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using System.Text.Json;
using FolderSentinel.Desktop.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace FolderSentinel.Desktop;

public partial class App : Application
{
    public StateStore Store { get; private set; } = null!;
    public MonitorManager Monitors { get; private set; } = null!;
    public NotificationService Notifications { get; private set; } = null!;
    public NotificationDispatcher Sender { get; private set; } = null!;
    public AppLog Log { get; private set; } = null!;
    public string DataDirectory { get; private set; } = "";
    public bool SuppressStartupRegistration { get; private set; }
    private SingleInstance? instance;
    private Forms.NotifyIcon? tray;
    private MainWindow? window;
    private bool exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataOption = Array.IndexOf(e.Args, "--data-dir");
        DataDirectory = dataOption >= 0 && dataOption + 1 < e.Args.Length ? Path.GetFullPath(e.Args[dataOption + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderSentinel");
        Directory.CreateDirectory(DataDirectory); Log = new AppLog(Path.Combine(DataDirectory, "logs"));
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Write("バックグラウンド処理の例外", args.Exception); args.SetObserved(); };
        try
        {
            if (e.Args.Contains("--diagnostics") && dataOption < 0) throw new ArgumentException("--diagnostics には --data-dir を指定してください。");
            SuppressStartupRegistration = e.Args.Contains("--no-startup") || dataOption >= 0;
            instance = new SingleInstance(DataDirectory);
            bool notificationLaunch = e.Args.Any(x => x.Contains("AppNotificationActivated", StringComparison.OrdinalIgnoreCase));
            Notifications = new NotificationService(Log);
            Notifications.Invoked += root => Dispatcher.BeginInvoke(async () =>
            {
                if (!instance.IsPrimary)
                {
                    try { await instance.SendAsync(root is null ? "show" : "root:" + root); } catch (Exception ex) { Log.Write("通知操作の転送に失敗", ex); }
                    Shutdown();
                }
                else if (window is null) _pendingAction = root is null ? "show" : "root:" + root;
                else HandleAction(root is null ? "show" : "root:" + root);
            });
            Notifications.Register();
            if (e.Args.Contains("--unregister-notifications")) { Notifications.RemoveRegistration(); Shutdown(); return; }
            if (!instance.IsPrimary)
            {
                if (!notificationLaunch) { await instance.SendAsync("show"); Shutdown(); }
                else { var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) }; timer.Tick += (_, _) => { timer.Stop(); Shutdown(); }; timer.Start(); }
                return;
            }
            Store = new StateStore(Path.Combine(DataDirectory, "state.db"));
            Monitors = new MonitorManager(Store, Log); Sender = new NotificationDispatcher(Store, Notifications, Log);
            window = new MainWindow(this); MainWindow = window;
            SetupTray();
            instance.Listen(action => Dispatcher.BeginInvoke(() => HandleAction(action)), Log);
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.TimeChanged += TimeChanged;
            if (!SuppressStartupRegistration && Store.GetPreference("startup-configured") is null)
            {
                try { StartupService.SetEnabled(true); Store.SetPreference("startup-configured", "true"); }
                catch (Exception ex) { Log.Write("自動起動を登録できませんでした", ex); }
            }
            Monitors.Start(); Sender.Start();
            TimeChanged(null, EventArgs.Empty);
            if (!e.Args.Contains("--background") && !e.Args.Contains("--diagnostics") && !notificationLaunch) ShowMain();
            if (_pendingAction is not null) HandleAction(_pendingAction);
            Log.Write("アプリ起動 " + Environment.ProcessPath);
            if (e.Args.Contains("--diagnostics"))
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    using var process = Process.GetCurrentProcess();
                    var modules = process.Modules.Cast<ProcessModule>()
                        .Where(m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) || m.ModuleName.Equals("hostfxr.dll", StringComparison.OrdinalIgnoreCase) || m.ModuleName.Equals("Microsoft.WindowsAppRuntime.dll", StringComparison.OrdinalIgnoreCase) || m.ModuleName.Equals("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase))
                        .Select(m => new { m.ModuleName, m.FileName }).ToList();
                    File.WriteAllText(Path.Combine(DataDirectory, "diagnostics.json"), JsonSerializer.Serialize(new { Runtime = Environment.Version.ToString(), NotificationReady = Notifications.RegistrationError is null, Notifications.RegistrationError, StartupRegistered = StartupService.IsEnabled(), Modules = modules }, new JsonSerializerOptions { WriteIndented = true }));
                    ExitApplication();
                };
                timer.Start();
            }
        }
        catch (Exception ex)
        {
            Log.Write("起動に失敗", ex);
            MessageBox.Show("アプリを起動できませんでした。\n\n" + ex.Message + "\n\nログ: " + Path.Combine(DataDirectory, "logs"), "Folder Sentinel", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private string? _pendingAction;
    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("設定画面を開く", null, (_, _) => Dispatcher.Invoke(ShowMain));
        menu.Items.Add("今すぐ確認", null, (_, _) => Monitors.RequestScan());
        menu.Items.Add("監視を一時停止／再開", null, (_, _) => Dispatcher.Invoke(TogglePaused));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        tray = new Forms.NotifyIcon { Text = "Folder Sentinel · フォルダ追加通知", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMain);
    }
    public void ShowMain() { window?.Show(); if (window is not null) { window.WindowState = WindowState.Normal; window.Activate(); } }
    public void OpenFolder(string rootId)
    {
        var root = Store.GetRoots().FirstOrDefault(x => x.Id == rootId); if (root is null) { ShowMain(); return; }
        try { var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false }; info.ArgumentList.Add(root.Path); Process.Start(info); }
        catch (Exception ex) { Log.Write("フォルダを開けませんでした", ex); ShowMain(); window?.ShowMessage("フォルダを開けませんでした。" + ex.Message, true); }
    }
    private void HandleAction(string action)
    {
        if (action.StartsWith("root:", StringComparison.Ordinal) && Guid.TryParseExact(action[5..], "N", out _)) OpenFolder(action[5..]);
        else ShowMain();
    }
    public void TogglePaused() { Monitors.Paused = !Monitors.Paused; Sender.Paused = Monitors.Paused; window?.RefreshPause(); }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) Monitors.RequestScan(); }
    private void TimeChanged(object? sender, EventArgs e)
    {
        TimeZoneInfo.ClearCachedData();
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var root in Store.GetRoots())
                {
                    if (root.Schedules.All(x => x.TimeZoneId == TimeZoneInfo.Local.Id)) continue;
                    foreach (var schedule in root.Schedules) schedule.TimeZoneId = TimeZoneInfo.Local.Id;
                    Store.SaveRoot(root, DateTimeOffset.UtcNow);
                }
                Monitors.RequestScan();
            }
            catch (Exception ex) { Log.Write("時刻設定の変更を反映できませんでした", ex); }
        });
    }
    public async void ExitApplication()
    {
        if (exiting) return; exiting = true;
        window?.AllowClose(); tray?.Dispose();
        SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.TimeChanged -= TimeChanged;
        try { await Sender.DisposeAsync(); await Monitors.DisposeAsync(); Store.Dispose(); }
        catch (Exception ex) { Log.Write("終了処理", ex); }
        Log.Write("アプリ終了"); Shutdown();
    }
    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        Log.Write("画面操作に失敗", args.Exception); args.Handled = true;
        if (window is not null) window.ShowMessage("操作を完了できませんでした。" + args.Exception.Message, true);
    }
    protected override void OnExit(ExitEventArgs e) { tray?.Dispose(); Notifications?.Dispose(); instance?.Dispose(); base.OnExit(e); }
}
