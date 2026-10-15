using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FolderSentinel.Desktop.Services;
using Microsoft.Win32;

namespace FolderSentinel.Desktop;

public partial class MainWindow : Window
{
    private readonly App app;
    private readonly ObservableCollection<FolderRow> roots = [];
    private readonly ObservableCollection<NotificationSchedule> schedules = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private List<HistoryRow> history = [];
    private string? editingId;
    private string fingerprint = "";
    private bool changingSelection, refreshing, allowClose;
    private int historyRevision;
    private bool resettingHistory;

    public MainWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        VersionLabel.Text = "VERSION " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        RootsList.ItemsSource = roots; SchedulesList.ItemsSource = schedules;
        DataPath.Text = app.DataDirectory;
        StartupBox.IsChecked = StartupService.IsEnabled();
        NotificationStatus.Text = app.Notifications.RegistrationError is null ? "通知テストで、Windowsへの送信を確認できます。" : "通知の準備に失敗しました: " + app.Notifications.RegistrationError;
        timer.Tick += async (_, _) => await RefreshAsync(); timer.Start();
        Loaded += async (_, _) => await RefreshAsync();
    }
    private string Fingerprint() => JsonSerializer.Serialize(new { NameInput.Text, Path = PathInput.Text, EnabledBox.IsChecked, Immediate = ImmediateBox.IsChecked, Scheduled = ScheduledBox.IsChecked, Schedules = schedules }, Json.Options);
    private bool DiscardChanges()
    {
        return Editor.Visibility != Visibility.Visible || Fingerprint() == fingerprint || MessageBox.Show(this, "未保存の変更があります。変更を破棄して移動しますか？", "編集内容の確認", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }
    private async Task RefreshAsync()
    {
        if (refreshing || allowClose) return; refreshing = true;
        try
        {
            var revision = historyRevision;
            var snapshot = await Task.Run(() => (Roots: app.Store.GetRoots(), History: app.Store.GetHistory()));
            changingSelection = true;
            try
            {
                foreach (var removed in roots.Where(x => snapshot.Roots.All(r => r.Id != x.Id)).ToArray()) roots.Remove(removed);
                foreach (var root in snapshot.Roots)
                {
                    var row = roots.FirstOrDefault(x => x.Id == root.Id);
                    if (row is null) { row = new FolderRow(root); roots.Add(row); }
                    row.Update(root, app.Monitors.States.GetValueOrDefault(root.Id), app.Monitors.Paused);
                }
            }
            finally { changingSelection = false; }
            if (Editor.Visibility == Visibility.Collapsed && roots.Count > 0) RootsList.SelectedItem = roots[0];
            FolderCount.Text = $"登録フォルダ {roots.Count}";
            if (revision == historyRevision && !resettingHistory) history = snapshot.History;
            if (HistoryPage.Visibility == Visibility.Visible) FilterHistory();
            if (editingId is not null) UpdateRuntime();
            StartupBox.IsChecked = StartupService.IsEnabled();
            RefreshPause();
            if (app.Sender.LastError is not null) NotificationStatus.Text = "通知を再試行しています: " + app.Sender.LastError;
        }
        catch (Exception ex) { app.Log.Write("画面の更新に失敗", ex); ShowMessage("状態を取得できませんでした。" + ex.Message, true); }
        finally { refreshing = false; }
    }
    public void ShowMessage(string message, bool error = false) { MessageBar.Text = message; MessageBar.Foreground = new SolidColorBrush(error ? Color.FromRgb(177, 70, 73) : Color.FromRgb(69, 103, 129)); }
    private void SetPage(string page)
    {
        FoldersPage.Visibility = page == "folders" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch { "history" => "追加・通知の履歴", "settings" => "アプリの設定", _ => "監視フォルダ" };
        PageSubtitle.Text = page switch { "history" => "ファイルの追加と通知の記録を、このPCに。", "settings" => "起動方法とデスクトップ通知を確認します。", _ => "" };
        PageSubtitle.Visibility = PageSubtitle.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (button, selected) in new[] { (FoldersNav, page == "folders"), (HistoryNav, page == "history"), (SettingsNav, page == "settings") })
        { button.Background = selected ? new SolidColorBrush(Color.FromRgb(48, 76, 108)) : Brushes.Transparent; button.Foreground = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(197, 210, 225)); }
        if (page == "history") FilterHistory();
    }
    private void ShowFolders(object sender, RoutedEventArgs e) => SetPage("folders");
    private void ShowHistory(object sender, RoutedEventArgs e) => SetPage("history");
    private void ShowSettings(object sender, RoutedEventArgs e) => SetPage("settings");
    private void AddRootClick(object sender, RoutedEventArgs e)
    {
        if (!DiscardChanges()) return;
        changingSelection = true; RootsList.SelectedItem = null; changingSelection = false;
        SetPage("folders"); LoadEditor(new WatchRoot { Schedules = [new NotificationSchedule()] }, true); NameInput.Focus();
    }
    private void RootSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingSelection || RootsList.SelectedItem is not FolderRow selected) return;
        if (!DiscardChanges())
        { changingSelection = true; RootsList.SelectedItem = roots.FirstOrDefault(x => x.Id == editingId); changingSelection = false; return; }
        LoadEditor(selected.Root.Copy(), false);
    }
    private void LoadEditor(WatchRoot root, bool isNew)
    {
        editingId = isNew ? null : root.Id;
        Editor.Visibility = Visibility.Visible; EmptyEditor.Visibility = Visibility.Collapsed;
        EditorTitle.Text = isNew ? "新しい監視フォルダ" : "フォルダの設定";
        NameInput.Text = root.Name; PathInput.Text = root.Path; PathInput.IsReadOnly = !isNew;
        BrowseButton.IsEnabled = isNew; EnabledBox.IsChecked = root.Enabled;
        ImmediateBox.IsChecked = root.Mode is NotificationMode.Immediate or NotificationMode.Both;
        ScheduledBox.IsChecked = root.Mode is NotificationMode.Scheduled or NotificationMode.Both;
        schedules.Clear(); foreach (var schedule in root.Schedules) schedules.Add(schedule);
        DeleteButton.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
        OpenButton.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
        SchedulePanel.IsEnabled = ScheduledBox.IsChecked == true;
        UpdateRuntime(); fingerprint = Fingerprint();
        ShowMessage(isNew ? "初回確認で見つかった既存ファイルは通知しません。" : "設定を変更したら「設定を保存」を押してください。");
    }
    private void UpdateRuntime()
    {
        var state = editingId is null ? null : app.Monitors.States.GetValueOrDefault(editingId);
        var row = roots.FirstOrDefault(x => x.Id == editingId);
        RuntimeStatus.Text = editingId is null ? "保存後に初回確認を開始します。" : row is { Root.Enabled: false } ? "監視を無効にしています。" : state?.Status ?? "監視を準備しています。";
        RuntimeDetail.Text = state?.Detail ?? "";
        if (state is not null)
        {
            var next = row?.Root.Mode == NotificationMode.Immediate ? "即時通知のみ" : state.NextDueUtc?.ToLocalTime().ToString("M/d HH:mm") ?? "なし";
            RuntimeTimes.Text = $"確認済み {state.FileCount:N0}件 · 最終確認 {state.LastScanUtc?.ToLocalTime().ToString("M/d HH:mm:ss") ?? "—"}\n次回の定期通知 {next}";
        }
        else RuntimeTimes.Text = "";
    }
    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "監視するフォルダを選択", Multiselect = false };
        if (dialog.ShowDialog(this) == true) { PathInput.Text = dialog.FolderName; if (string.IsNullOrWhiteSpace(NameInput.Text)) NameInput.Text = Path.GetFileName(dialog.FolderName.TrimEnd('\\')); }
    }
    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (ImmediateBox.IsChecked != true && ScheduledBox.IsChecked != true) { ShowMessage("通知のタイミングを1つ以上選択してください。", true); return; }
        if (string.IsNullOrWhiteSpace(PathInput.Text) || !Path.IsPathFullyQualified(PathInput.Text)) { ShowMessage("フォルダの場所を絶対パスで指定してください。", true); return; }
        var root = new WatchRoot
        {
            Id = editingId ?? Guid.NewGuid().ToString("N"), Name = NameInput.Text.Trim(), Path = PathInput.Text.Trim(), Enabled = EnabledBox.IsChecked == true,
            Mode = ImmediateBox.IsChecked == true ? ScheduledBox.IsChecked == true ? NotificationMode.Both : NotificationMode.Immediate : NotificationMode.Scheduled,
            Schedules = schedules.ToList()
        };
        SaveButton.IsEnabled = false;
        try
        {
            await Task.Run(() => app.Store.SaveRoot(root, DateTimeOffset.UtcNow));
            editingId = root.Id; fingerprint = Fingerprint();
            await RefreshAsync();
            changingSelection = true; RootsList.SelectedItem = roots.FirstOrDefault(x => x.Id == root.Id); changingSelection = false;
            LoadEditor(root.Copy(), false); app.Monitors.RequestScan(root.Id);
            ShowMessage("設定を保存しました。監視状態は上の欄で確認できます。");
        }
        catch (Exception ex) { ShowMessage(ex.Message, true); }
        finally { SaveButton.IsEnabled = true; }
    }
    private async void DeleteClick(object sender, RoutedEventArgs e)
    {
        if (editingId is null || MessageBox.Show(this, "このフォルダの登録と、アプリ内の検出・通知履歴を削除します。監視先のファイルは削除しません。", "登録を削除", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var id = editingId; editingId = null;
        await Task.Run(() => app.Store.DeleteRoot(id));
        changingSelection = true; RootsList.SelectedItem = null; changingSelection = false;
        Editor.Visibility = Visibility.Collapsed; EmptyEditor.Visibility = Visibility.Visible;
        await RefreshAsync(); ShowMessage("フォルダの登録を削除しました。");
    }
    private void OpenClick(object sender, RoutedEventArgs e) { if (editingId is not null) app.OpenFolder(editingId); }
    private void AddScheduleClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ScheduleWindow(new NotificationSchedule()) { Owner = this };
        if (dialog.ShowDialog() == true) schedules.Add(dialog.Result!);
    }
    private void EditScheduleClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not NotificationSchedule schedule) return;
        var copy = JsonSerializer.Deserialize<NotificationSchedule>(JsonSerializer.Serialize(schedule, Json.Options), Json.Options)!;
        var dialog = new ScheduleWindow(copy) { Owner = this };
        if (dialog.ShowDialog() == true) schedules[schedules.IndexOf(schedule)] = dialog.Result!;
    }
    private void RemoveScheduleClick(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is NotificationSchedule schedule) schedules.Remove(schedule); }
    private void ScheduleToggle(object sender, RoutedEventArgs e) { if (SchedulePanel is not null) SchedulePanel.IsEnabled = ScheduledBox.IsChecked == true; }
    private void ScanClick(object sender, RoutedEventArgs e) { app.Monitors.RequestScan(); ShowMessage("すべての監視フォルダの確認を予約しました。"); }
    public void RefreshPause()
    {
        PauseButton.Content = app.Monitors.Paused ? "監視を再開" : "監視を一時停止";
        GlobalStatus.Text = app.Monitors.Paused ? "● 監視を一時停止中" : roots.Count == 0 ? "● フォルダ未登録" : $"● {roots.Count(x => x.Root.Enabled)}フォルダを監視";
        GlobalStatus.Foreground = app.Monitors.Paused ? new SolidColorBrush(Color.FromRgb(242, 199, 115)) : new SolidColorBrush(Color.FromRgb(131, 221, 192));
    }
    private void PauseClick(object sender, RoutedEventArgs e) => app.TogglePaused();
    private void HistorySearchChanged(object sender, TextChangedEventArgs e) { if (HistoryGrid is not null) FilterHistory(); }
    private void FilterHistory()
    {
        var search = HistorySearch.Text.Trim();
        var filtered = history.Where(x => FolderSentinel.Core.HistorySearch.Matches(x, search)).ToList();
        HistoryGrid.ItemsSource = filtered;
        HistoryCount.Text = $"{filtered.Count:N0}件を表示（最新1,000件から検索）。CSVは検索条件に一致する全件を出力します。\n行をダブルクリックするとフォルダが開きます。";
    }
    private async void ExportHistoryClick(object sender, RoutedEventArgs e)
    {
        var search = HistorySearch.Text.Trim();
        var dialog = new SaveFileDialog
        {
            Title = "監視履歴をCSVで出力", Filter = "CSVファイル (*.csv)|*.csv", DefaultExt = ".csv", AddExtension = true,
            FileName = $"FolderSentinel-history-{DateTime.Now:yyyyMMdd-HHmmss}.csv", OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var fileName = dialog.FileName;
        ExportHistoryButton.IsEnabled = ResetHistoryButton.IsEnabled = false;
        ShowMessage("CSVを出力しています…");
        try
        {
            var count = await Task.Run(() =>
            {
                // Write beside the target, then replace it only after a successful export.
                var temporary = Path.Combine(Path.GetDirectoryName(fileName)!, $".FolderSentinel-{Guid.NewGuid():N}.tmp");
                try
                {
                    long exported;
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        exported = app.Store.ExportHistoryCsv(output, search);
                    File.Move(temporary, fileName, overwrite: true);
                    return exported;
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            });
            ShowMessage($"{count:N0}件の履歴をCSVに出力しました。保存先: {fileName}");
        }
        catch (Exception ex) { app.Log.Write("履歴のCSV出力に失敗", ex); ShowMessage("CSVを出力できませんでした。" + ex.Message, true); }
        finally { ExportHistoryButton.IsEnabled = ResetHistoryButton.IsEnabled = true; }
    }
    private async void ResetHistoryClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
            "すべての監視フォルダの追加・通知履歴と、未送信の通知を削除します。元に戻せません。\n\n監視設定・既存ファイルの判定情報・監視先のファイルは保持します。Windowsの通知センターに送信済みの通知は残ります。\n必要な履歴は先にCSV出力してください。",
            "監視履歴をリセット", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        ExportHistoryButton.IsEnabled = ResetHistoryButton.IsEnabled = false;
        resettingHistory = true; historyRevision++;
        try
        {
            await app.Sender.ResetHistoryAsync();
            history = []; FilterHistory();
            NotificationStatus.Text = app.Notifications.RegistrationError is null ? "通知テストで、Windowsへの送信を確認できます。" : "通知の準備に失敗しました: " + app.Notifications.RegistrationError;
            ShowMessage("追加・通知履歴と未送信通知をリセットしました。監視は引き続き動作します。");
        }
        catch (Exception ex) { app.Log.Write("履歴のリセットに失敗", ex); ShowMessage("履歴をリセットできませんでした。" + ex.Message, true); }
        finally
        {
            resettingHistory = false; historyRevision++;
            ExportHistoryButton.IsEnabled = ResetHistoryButton.IsEnabled = true;
        }
        await RefreshAsync();
    }
    private void HistoryDoubleClick(object sender, MouseButtonEventArgs e) { if (HistoryGrid.SelectedItem is HistoryRow row) app.OpenFolder(row.RootId); }
    private void StartupClick(object sender, RoutedEventArgs e)
    {
        try { StartupService.SetEnabled(StartupBox.IsChecked == true); app.Store.SetPreference("startup-configured", "true"); ShowMessage(StartupBox.IsChecked == true ? "自動起動を有効にしました。" : "自動起動を無効にしました。"); }
        catch (Exception ex) { StartupBox.IsChecked = StartupService.IsEnabled(); ShowMessage("自動起動を変更できませんでした。" + ex.Message, true); }
    }
    private void TestNotificationClick(object sender, RoutedEventArgs e)
    {
        try { app.Notifications.Test(); ShowMessage("Windowsへテスト通知を送信しました。表示されない場合はWindowsの通知設定を確認してください。"); }
        catch (Exception ex) { ShowMessage(ex.Message, true); }
    }
    private void ExitClick(object sender, RoutedEventArgs e) { if (DiscardChanges()) app.ExitApplication(); }
    public void AllowClose() { allowClose = true; timer.Stop(); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!allowClose) { e.Cancel = true; Hide(); } }
}

public sealed class FolderRow : ObservableObject
{
    public WatchRoot Root { get; private set; }
    public string Id => Root.Id;
    public string Name => Root.Name;
    public string Path => Root.Path;
    public string ModeLabel => Root.ModeLabel;
    private string status = "準備中";
    public string Status { get => status; private set => SetProperty(ref status, value); }
    private Brush statusBrush = Brushes.Gray;
    public Brush StatusBrush { get => statusBrush; private set => SetProperty(ref statusBrush, value); }
    public FolderRow(WatchRoot root) => Root = root;
    public void Update(WatchRoot root, RootRuntime? state, bool paused)
    {
        Root = root; OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Path)); OnPropertyChanged(nameof(ModeLabel));
        Status = !root.Enabled ? "無効" : paused ? "一時停止" : state?.Status ?? "準備中";
        StatusBrush = !root.Enabled || paused ? Brushes.Gray : state?.Healthy == true ? new SolidColorBrush(Color.FromRgb(36, 139, 111)) : state is null ? Brushes.Gray : new SolidColorBrush(Color.FromRgb(193, 131, 40));
    }
}
