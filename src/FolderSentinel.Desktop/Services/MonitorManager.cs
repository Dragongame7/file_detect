using System.Collections.Concurrent;

namespace FolderSentinel.Desktop.Services;

public sealed class MonitorManager(StateStore store, AppLog log) : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, RootWorker> workers = [];
    private readonly SemaphoreSlim localScans = new(2);
    private readonly SemaphoreSlim networkScans = new(2);
    public ConcurrentDictionary<string, RootRuntime> States { get; } = new();
    private Task? loop;
    public bool Paused { get; set; }
    public void Start() => loop = Task.Run(RunAsync);
    public void RequestScan(string? id = null)
    {
        lock (workers) foreach (var pair in workers) if (id is null || id == pair.Key) pair.Value.RequestScan();
    }
    private async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var roots = store.GetRoots();
                lock (workers)
                {
                    foreach (var id in workers.Keys.Where(id => roots.All(x => x.Id != id || !x.Enabled)).ToArray())
                    { workers[id].Stop(); workers.Remove(id); States.TryRemove(id, out _); }
                    foreach (var root in roots.Where(x => x.Enabled))
                    {
                        if (!workers.ContainsKey(root.Id))
                        {
                            var worker = new RootWorker(root, store, log, States, () => Paused, root.Path.StartsWith("\\\\", StringComparison.Ordinal) ? networkScans : localScans, stop.Token);
                            workers.Add(root.Id, worker); worker.Start();
                        }
                    }
                }
                await Task.Delay(1000, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { log.Write("監視管理が停止しました", ex); }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (loop is not null) await loop;
        Task[] tasks;
        lock (workers) { foreach (var worker in workers.Values) worker.Stop(); tasks = workers.Values.Select(x => x.Completion).ToArray(); }
        // A stalled SMB call must not prevent the desktop process from exiting.
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(2000));
        stop.Dispose();
    }
}

internal sealed class RootWorker
{
    private readonly WatchRoot initial;
    private readonly StateStore store;
    private readonly AppLog log;
    private readonly ConcurrentDictionary<string, RootRuntime> states;
    private readonly Func<bool> paused;
    private readonly SemaphoreSlim scanSlots;
    private readonly CancellationTokenSource stop;
    private readonly WindowsFileScanner scanner = new();
    private FileSystemWatcher? watcher;
    private long changes = 1, processed;
    private DateTimeOffset nextScan = DateTimeOffset.MinValue;
    private DateTimeOffset lastScan;
    private bool healthy, startupReconciled, rootWasAccessible;
    public Task Completion { get; private set; } = Task.CompletedTask;

    public RootWorker(WatchRoot root, StateStore store, AppLog log, ConcurrentDictionary<string, RootRuntime> states, Func<bool> paused, SemaphoreSlim scanSlots, CancellationToken token)
    { initial = root; this.store = store; this.log = log; this.states = states; this.paused = paused; this.scanSlots = scanSlots; stop = CancellationTokenSource.CreateLinkedTokenSource(token); }
    public void Start() => Completion = Task.Run(RunAsync);
    public void RequestScan() => Interlocked.Increment(ref changes);
    public void Stop() { stop.Cancel(); watcher?.Dispose(); }
    private void CreateWatcher(string path)
    {
        if (watcher is not null) return;
        try
        {
            watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 32 * 1024
            };
            watcher.Created += (_, _) => RequestScan(); watcher.Deleted += (_, _) => RequestScan(); watcher.Renamed += (_, _) => RequestScan();
            watcher.Error += (_, args) => { log.Write($"監視イベントの再照合を予約: {initial.Name}", args.GetException()); RequestScan(); Interlocked.Exchange(ref watcher, null)?.Dispose(); };
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { watcher?.Dispose(); watcher = null; log.Write($"変更イベント監視を開始できません: {initial.Name}", ex); }
    }
    private async Task RunAsync()
    {
        int failures = 0;
        try
        {
            var snapshot = store.GetSnapshot(initial.Id);
            while (!stop.IsCancellationRequested)
            {
                var root = store.GetRoots().FirstOrDefault(x => x.Id == initial.Id);
                if (root is null || !root.Enabled) return;
                if (paused()) { Publish("一時停止", "監視を再開すると追加分を確認します。", false); await Task.Delay(1000, stop.Token); RequestScan(); continue; }
                var now = DateTimeOffset.UtcNow;
                var due = root.Mode == NotificationMode.Immediate ? null : store.GetNextDue(root.Id);
                bool dueNeedsScan = due is not null && due <= now && lastScan < due;
                bool needsScan = changes != processed || now >= nextScan || snapshot.Pending.Count > 0 || dueNeedsScan;
                if (needsScan)
                {
                    long version = Interlocked.Read(ref changes);
                    Publish(snapshot.Initialized ? "確認中" : "初回確認中", "フォルダ内のファイルを確認しています。", false, snapshot);
                    CreateWatcher(root.Path);
                    await scanSlots.WaitAsync(stop.Token);
                    ScanResult result;
                    try { result = scanner.Scan(root.Path, stop.Token); }
                    finally { scanSlots.Release(); }
                    stop.Token.ThrowIfCancellationRequested();
                    now = DateTimeOffset.UtcNow;
                    if (!result.RootAccessible)
                    {
                        healthy = false; rootWasAccessible = false; failures++;
                        nextScan = now.AddSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(failures, 6))));
                        processed = version;
                        Interlocked.Exchange(ref watcher, null)?.Dispose();
                        Publish("接続不可", result.Error ?? "フォルダにアクセスできません。再接続を試みます。", false, snapshot);
                    }
                    else
                    {
                        // After an outage, require fresh stability observations rather than counting time offline.
                        if (!rootWasAccessible && snapshot.Pending.Count > 0) snapshot = snapshot with { Pending = snapshot.Pending.Select(x => x with { StableSinceUtc = now, Samples = 0 }).ToArray() };
                        rootWasAccessible = true;
                        var diff = DiffEngine.Compare(snapshot, result, now);
                        if (!store.SaveScan(root.Id, diff)) return;
                        snapshot = diff.Snapshot; healthy = result.UnavailablePrefixes.Count == 0 && snapshot.Initialized;
                        failures = 0; lastScan = now; processed = version;
                        nextScan = now.AddSeconds(watcher is null ? 30 : root.Path.StartsWith("\\\\", StringComparison.Ordinal) ? 1800 : 900);
                        if (!startupReconciled && healthy && snapshot.Pending.Count == 0) startupReconciled = true;
                        Publish(!snapshot.Initialized ? "初回確認中" : !healthy ? "一部読み取り不可" : watcher is null ? "定期確認で監視中" : "監視中",
                            result.Error ?? (snapshot.Pending.Count > 0 ? $"{snapshot.Pending.Count}件のファイルの安定を確認しています。" : "新しいファイルを待っています。"), healthy, snapshot);
                    }
                }
                stop.Token.ThrowIfCancellationRequested();
                store.QueueNotifications(root, DateTimeOffset.UtcNow, healthy && startupReconciled);
                if (states.TryGetValue(root.Id, out var runtime)) states[root.Id] = runtime with { NextDueUtc = store.GetNextDue(root.Id) };
                await Task.Delay(snapshot.Pending.Count > 0 ? 2000 : 1000, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { log.Write($"監視処理が停止: {initial.Name}", ex); Publish("エラー", ex.Message + " フォルダを無効→有効にすると再試行します。", false); }
        finally { watcher?.Dispose(); }
    }
    private void Publish(string status, string detail, bool ok, Snapshot? snapshot = null)
    {
        snapshot ??= Snapshot.Empty;
        states[initial.Id] = new(initial.Id, status, detail, lastScan == default ? null : lastScan, store.GetNextDue(initial.Id), snapshot.Files.Count, snapshot.Pending.Count, ok);
    }
}
