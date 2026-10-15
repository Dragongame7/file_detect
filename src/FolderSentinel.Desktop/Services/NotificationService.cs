using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace FolderSentinel.Desktop.Services;

public sealed class NotificationService : IDisposable
{
    public event Action<string?>? Invoked;
    public string? RegistrationError { get; private set; }
    private readonly AppLog log;
    private bool registered;
    public NotificationService(AppLog log) => this.log = log;
    public void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            AppNotificationManager.Default.Register(); registered = true;
        }
        catch (Exception ex) { RegistrationError = ex.Message; log.Write("Windows通知を登録できませんでした", ex); }
    }
    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var values = args.Argument.Split('&').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
        Invoked?.Invoke(values.TryGetValue("root", out var value) ? Uri.UnescapeDataString(value) : null);
    }
    public void Send(OutboxItem item)
    {
        if (!registered) throw new InvalidOperationException("Windows通知が利用できません: " + RegistrationError);
        var p = item.Payload;
        var names = p.FileNames.Take(2).Select(Path.GetFileName);
        var notification = new AppNotificationBuilder()
            .AddArgument("root", p.RootId)
            .AddText($"{p.RootName} に {p.EventIds.Count:N0}件の追加")
            .AddText(string.Join("、", names) + (p.EventIds.Count > 2 ? $" ほか{p.EventIds.Count - 2}件" : ""))
            .AddText(p.Kind == "定期" ? "定期レポート · クリックしてフォルダを開く" : "クリックしてフォルダを開く")
            .BuildNotification();
        notification.Tag = item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        notification.Group = "FolderSentinel";
        AppNotificationManager.Default.Show(notification);
        if (notification.Id == 0) throw new InvalidOperationException("Windowsへの通知送信を確認できませんでした。Windowsの通知設定を確認してください。");
    }
    public void Test()
    {
        if (!registered) throw new InvalidOperationException("Windows通知が利用できません: " + RegistrationError);
        var notification = new AppNotificationBuilder().AddText("Folder Sentinel").AddText("通知の準備ができています。新しいファイルをここでお知らせします。").BuildNotification();
        AppNotificationManager.Default.Show(notification);
        if (notification.Id == 0) throw new InvalidOperationException("通知が送信されませんでした。Windowsの通知設定を確認してください。");
    }
    public void RemoveRegistration() { AppNotificationManager.Default.UnregisterAll(); registered = false; }
    public void Dispose()
    {
        if (registered) try { AppNotificationManager.Default.Unregister(); } catch (Exception ex) { log.Write("通知登録の終了処理", ex); }
    }
}

public sealed class NotificationDispatcher(StateStore store, NotificationService notifications, AppLog log) : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim deliveryGate = new(1, 1);
    private Task? task;
    public bool Paused { get; set; }
    public string? LastError { get; private set; }
    public void Start() => task = Task.Run(async () =>
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (!Paused)
                    foreach (var item in store.GetOutbox(DateTimeOffset.UtcNow))
                    {
                        if (stop.IsCancellationRequested) break;
                        await deliveryGate.WaitAsync(stop.Token);
                        try
                        {
                            if (Paused || !store.IsBatchPending(item.Id)) continue;
                            try { notifications.Send(item); store.CompleteBatch(item.Id); LastError = null; }
                            catch (Exception ex) { LastError = ex.Message; log.Write("通知送信に失敗", ex); store.FailBatch(item.Id, item.Attempts, ex.Message, DateTimeOffset.UtcNow); }
                        }
                        finally { deliveryGate.Release(); }
                    }
                await Task.Delay(2000, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { LastError = ex.Message; log.Write("通知送信処理が停止", ex); }
    });
    public Task ResetHistoryAsync() => Task.Run(async () =>
    {
        await deliveryGate.WaitAsync(stop.Token);
        try { store.ResetHistory(); LastError = null; }
        finally { deliveryGate.Release(); }
    });
    public async ValueTask DisposeAsync() { stop.Cancel(); if (task is not null) await task; stop.Dispose(); }
}
