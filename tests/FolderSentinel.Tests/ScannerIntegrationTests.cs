using FolderSentinel.Desktop.Services;

namespace FolderSentinel.Tests;

public sealed class ScannerIntegrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FolderSentinel.Scanner", Guid.NewGuid().ToString("N"));
    public ScannerIntegrationTests() => Directory.CreateDirectory(directory);
    [Fact] public void NativeFileIdentitySurvivesRenameAndRecursiveScanFindsFiles()
    {
        var original = Path.Combine(directory, "元の名前.txt"); File.WriteAllText(original, "data");
        var identity = FileIdentity.Read(original); Directory.CreateDirectory(Path.Combine(directory, "deep", "child"));
        var moved = Path.Combine(directory, "deep", "child", "新しい名前.txt"); File.Move(original, moved);
        Assert.Equal(identity, FileIdentity.Read(moved));
        var scan = new WindowsFileScanner().Scan(directory, CancellationToken.None); Assert.True(scan.RootAccessible); Assert.Empty(scan.UnavailablePrefixes); Assert.Equal(identity, Assert.Single(scan.Files).Identity);
    }
    [Fact] public void MissingRootIsDisconnectedNotEmpty()
    {
        var result = new WindowsFileScanner().Scan(Path.Combine(directory, "missing"), CancellationToken.None); Assert.False(result.RootAccessible); Assert.NotNull(result.Error);
    }
    [Fact] public async Task LiveWatcherPersistsOnlyAdditionsAcrossAppRestart()
    {
        var watch = Path.Combine(directory, "watch"); Directory.CreateDirectory(watch); File.WriteAllText(Path.Combine(watch, "existing.txt"), "baseline");
        using var store = new StateStore(Path.Combine(directory, "state.db"));
        var root = new WatchRoot { Path = watch, Name = "統合試験", Mode = NotificationMode.Immediate }; store.SaveRoot(root, DateTimeOffset.UtcNow);
        await using (var manager = new MonitorManager(store, new AppLog(Path.Combine(directory, "logs"))))
        {
            manager.Start(); await WaitUntil(() => store.GetSnapshot(root.Id).Initialized);
            var sub = Path.Combine(watch, "nested"); Directory.CreateDirectory(sub); var added = Path.Combine(sub, "new.txt"); File.WriteAllText(added, "new");
            await WaitUntil(() => store.GetHistory().Count == 1);
            File.Move(added, Path.Combine(sub, "renamed.txt")); File.AppendAllText(Path.Combine(watch, "existing.txt"), "update"); manager.RequestScan();
            await WaitUntil(() => store.GetSnapshot(root.Id).Files.Any(x => x.RelativePath.EndsWith("renamed.txt"))); Assert.Single(store.GetHistory());
        }
        File.Move(Path.Combine(watch, "existing.txt"), Path.Combine(watch, "offline-rename.txt")); File.WriteAllText(Path.Combine(watch, "offline-add.txt"), "offline");
        await using (var reopened = new MonitorManager(store, new AppLog(Path.Combine(directory, "logs"))))
        {
            reopened.Start(); await WaitUntil(() => store.GetHistory().Count == 2);
            Assert.Contains(store.GetHistory(), x => x.RelativePath == "offline-add.txt"); Assert.DoesNotContain(store.GetHistory(), x => x.RelativePath == "offline-rename.txt");
        }
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (!condition()) await Task.Delay(200, timeout.Token);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
