namespace FolderSentinel.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "FolderSentinel.Tests", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset start = DateTimeOffset.Parse("2026-10-15T00:00Z");
    private StateStore Open() => new(Path.Combine(directory, "state.db"));
    private WatchRoot Root() => new() { Name = "試験", Path = Path.Combine(directory, "watch"), Mode = NotificationMode.Both, Schedules = [new() { Frequency = ScheduleFrequency.Daily, Time = "09:00", TimeZoneId = "UTC" }] };
    private void Add(StateStore store, WatchRoot root, string path = "new.txt", DateTimeOffset? detected = null)
    {
        var file = new FileStamp(path, Guid.NewGuid().ToString(), 100, 1);
        store.SaveScan(root.Id, new(new(true, [file], []), [new(file, detected ?? start, start.AddSeconds(5))]));
    }
    [Fact] public void SettingsSnapshotAndPendingSurviveRestart()
    {
        var root = Root(); var stamp = new FileStamp("日本語.txt", "123", 0, 1);
        using (var store = Open()) { store.SaveRoot(root, start); store.SaveScan(root.Id, new(new(true, [stamp], [new(stamp, start, start, 1)]), [])); }
        using var reopened = Open(); Assert.Equal(root.Name, Assert.Single(reopened.GetRoots()).Name); Assert.Single(reopened.GetSnapshot(root.Id).Pending);
    }
    [Fact] public void ImmediateAndScheduledHaveIndependentDeliveryState()
    {
        using var store = Open(); var root = Root(); store.SaveRoot(root, start); Add(store, root);
        store.QueueNotifications(root, start.AddMinutes(1), true);
        var instant = Assert.Single(store.GetOutbox(start.AddMinutes(1))); Assert.Equal("即時", instant.Payload.Kind); store.CompleteBatch(instant.Id);
        store.QueueNotifications(root, start.AddHours(10), true);
        var scheduled = Assert.Single(store.GetOutbox(start.AddHours(10))); Assert.Equal("定期", scheduled.Payload.Kind); Assert.Equal(instant.Payload.EventIds, scheduled.Payload.EventIds);
        store.QueueNotifications(root, start.AddHours(10), true); Assert.Single(store.GetOutbox(start.AddHours(10)));
        Assert.Contains("OS送信済み", Assert.Single(store.GetHistory()).Notifications);
    }
    [Fact] public void FailedStartupScanDoesNotAdvanceScheduledCursor()
    {
        using var store = Open(); var root = Root(); root.Mode = NotificationMode.Scheduled; store.SaveRoot(root, start); Add(store, root);
        store.QueueNotifications(root, start.AddDays(3), false); Assert.Empty(store.GetOutbox(start.AddDays(3))); Assert.True(store.GetNextDue(root.Id) < start.AddDays(3));
        store.QueueNotifications(root, start.AddDays(3), true); Assert.Single(store.GetOutbox(start.AddDays(3))); Assert.True(store.GetNextDue(root.Id) > start.AddDays(3));
    }
    [Fact] public void MultipleSchedulesEachReceiveTheSameAddition()
    {
        using var store = Open(); var root = Root(); root.Mode = NotificationMode.Scheduled; root.Schedules.Add(new() { Frequency = ScheduleFrequency.Daily, Time = "12:00", TimeZoneId = "UTC" }); store.SaveRoot(root, start); Add(store, root);
        store.QueueNotifications(root, start.AddDays(1), true); var batches = store.GetOutbox(start.AddDays(1)); Assert.Equal(2, batches.Count); Assert.Equal(batches[0].Payload.EventIds, batches[1].Payload.EventIds);
    }
    [Fact] public void LateConfirmedFileIsNotLostBehindCursor()
    {
        using var store = Open(); var root = Root(); root.Mode = NotificationMode.Scheduled; store.SaveRoot(root, start); Add(store, root, "first.txt");
        store.QueueNotifications(root, start.AddHours(10), true); foreach (var batch in store.GetOutbox(start.AddHours(10))) store.CompleteBatch(batch.Id);
        Add(store, root, "slow-copy.txt", start.AddMinutes(-10));
        store.QueueNotifications(root, start.AddDays(1).AddHours(10), true); Assert.Equal("slow-copy.txt", Assert.Single(Assert.Single(store.GetOutbox(start.AddDays(2))).Payload.FileNames));
    }
    [Fact] public void OutboxFailuresArePersistedAndRetriedWithDelay()
    {
        var root = Root(); long id;
        using (var store = Open()) { store.SaveRoot(root, start); Add(store, root); store.QueueNotifications(root, start, true); var item = Assert.Single(store.GetOutbox(start)); id = item.Id; store.FailBatch(id, 0, "offline", start); Assert.Empty(store.GetOutbox(start)); }
        using var reopened = Open(); var retry = Assert.Single(reopened.GetOutbox(start.AddMinutes(1))); Assert.Equal(id, retry.Id); Assert.Equal(1, retry.Attempts);
    }
    [Fact] public void OverlappingRegistrationIsRejectedButSiblingIsAllowed()
    {
        using var store = Open(); var root = Root(); store.SaveRoot(root, start);
        var child = Root(); child.Path = Path.Combine(root.Path, "child"); Assert.Throws<ArgumentException>(() => store.SaveRoot(child, start));
        var sibling = Root(); sibling.Path = Path.Combine(directory, "watch2"); store.SaveRoot(sibling, start); Assert.Equal(2, store.GetRoots().Count);
    }
    [Fact] public void DeletedRootCannotBeResurrectedByInflightScan()
    {
        using var store = Open(); var root = Root(); store.SaveRoot(root, start); Add(store, root); store.QueueNotifications(root, start, true);
        store.DeleteRoot(root.Id); Assert.False(store.SaveScan(root.Id, new(Snapshot.Empty, []))); Assert.Empty(store.GetHistory()); Assert.Empty(store.GetOutbox(start));
    }
    [Fact] public void DisabledRootsDoNotStarveOtherRootsOutbox()
    {
        using var store = Open(); var disabled = Root(); disabled.Mode = NotificationMode.Immediate; store.SaveRoot(disabled, start);
        for (int i = 0; i < 25; i++) { Add(store, disabled, $"file-{i}.txt"); store.QueueNotifications(disabled, start, true); }
        disabled.Enabled = false; store.SaveRoot(disabled, start);
        var active = Root(); active.Path = Path.Combine(directory, "other"); active.Mode = NotificationMode.Immediate; store.SaveRoot(active, start); Add(store, active); store.QueueNotifications(active, start, true);
        Assert.Equal(active.Id, Assert.Single(store.GetOutbox(start)).Payload.RootId);
    }
    [Fact] public void EmptyOccurrenceAdvancesWithoutCreatingNotification()
    {
        using var store = Open(); var root = Root(); store.SaveRoot(root, start);
        store.QueueNotifications(root, start.AddHours(10), true); Assert.Empty(store.GetOutbox(start.AddHours(10))); Assert.True(store.GetNextDue(root.Id) > start.AddHours(10));
    }
    [Fact] public void EditingSchedulePreservesUnreportedAdditions()
    {
        using var store = Open(); var root = Root(); root.Mode = NotificationMode.Scheduled; store.SaveRoot(root, start); Add(store, root);
        root.Schedules[0].Time = "12:00"; store.SaveRoot(root, start.AddHours(1));
        store.QueueNotifications(root, start.AddHours(13), true); Assert.Single(Assert.Single(store.GetOutbox(start.AddHours(13))).Payload.EventIds);
    }
    [Fact] public void ResetClearsAllHistoryButPreservesConfigurationAndDetectionStateAcrossRestart()
    {
        var root = Root(); var other = Root(); other.Path = Path.Combine(directory, "watch2");
        var existing = new FileStamp("existing.txt", "existing", 100, 1);
        var pending = new FileStamp("still-copying.txt", "pending", 200, 2);
        DateTimeOffset? due;
        using (var store = Open())
        {
            store.SaveRoot(root, start); store.SaveRoot(other, start); store.SetPreference("startup-configured", "true");
            Add(store, root); Add(store, other);
            store.QueueNotifications(root, start, true); store.QueueNotifications(other, start, true);
            var batches = store.GetOutbox(start); store.CompleteBatch(batches[0].Id);
            store.SaveScan(root.Id, new(new(true, [existing, pending], [new(pending, start, start, 1)]), []));
            due = store.GetNextDue(root.Id);
            store.ResetHistory(); store.ResetHistory();
            Assert.Empty(store.GetHistory()); Assert.Empty(store.GetOutbox(start.AddDays(1)));
            Assert.All(batches, item => Assert.False(store.IsBatchPending(item.Id)));
        }
        using var reopened = Open();
        Assert.Equal(2, reopened.GetRoots().Count);
        Assert.Equal(root.Schedules[0].Time, reopened.GetRoots()[0].Schedules[0].Time);
        Assert.Equal("true", reopened.GetPreference("startup-configured"));
        Assert.Equal(due, reopened.GetNextDue(root.Id));
        var snapshot = reopened.GetSnapshot(root.Id);
        Assert.True(snapshot.Initialized); Assert.Equal(2, snapshot.Files.Count); Assert.Single(snapshot.Pending);
        var diff = DiffEngine.Compare(snapshot, new([existing, pending], [], true), start.AddSeconds(10));
        Assert.Empty(diff.Additions); Assert.Single(diff.Snapshot.Pending);
        reopened.SaveScan(root.Id, diff);
        diff = DiffEngine.Compare(diff.Snapshot, new([existing, pending], [], true), start.AddSeconds(12));
        Assert.Equal("still-copying.txt", Assert.Single(diff.Additions).File.RelativePath);
    }
    [Fact] public void NewEventsAfterResetKeepUniqueIdsAndReachImmediateAndScheduledNotifications()
    {
        using var store = Open(); var root = Root(); store.SaveRoot(root, start); Add(store, root, "old.txt");
        store.QueueNotifications(root, start.AddHours(10), true);
        var oldBatches = store.GetOutbox(start.AddHours(10)); var oldId = Assert.Single(store.GetHistory()).Id;
        Assert.Equal(2, oldBatches.Count);
        var due = store.GetNextDue(root.Id);
        store.ResetHistory();
        Assert.Equal(due, store.GetNextDue(root.Id));
        store.QueueNotifications(root, start.AddDays(1).AddHours(10), true);
        Assert.Empty(store.GetOutbox(start.AddDays(2)));
        Add(store, root, "after-reset.txt");
        Assert.True(Assert.Single(store.GetHistory()).Id > oldId);
        store.QueueNotifications(root, start.AddDays(2).AddHours(10), true);
        var newBatches = store.GetOutbox(start.AddDays(3)); Assert.Equal(2, newBatches.Count);
        Assert.All(newBatches, item =>
        {
            Assert.True(item.Id > oldBatches.Max(x => x.Id)); Assert.True(store.IsBatchPending(item.Id));
            Assert.Equal("after-reset.txt", Assert.Single(item.Payload.FileNames));
        });
    }
    [Fact] public void CsvExportsAllRowsBeyondDisplayLimitAndUsesSameSearch()
    {
        using var store = Open(); var root = Root(); store.SaveRoot(root, start);
        var files = Enumerable.Range(0, 1005).Select(i => new FileStamp($"file-{i:0000}.txt", $"id-{i}", 0, 1)).ToArray();
        store.SaveScan(root.Id, new(new(true, files, []), files.Select(file => new ConfirmedAddition(file, start, start)).ToArray()));
        Assert.Equal(1000, store.GetHistory().Count);
        using var all = new MemoryStream(); Assert.Equal(1005, store.ExportHistoryCsv(all));
        var text = System.Text.Encoding.UTF8.GetString(all.ToArray());
        Assert.Contains("file-0000.txt", text); Assert.Contains("file-1004.txt", text); Assert.Contains(root.Path, text);
        using var filtered = new MemoryStream(); Assert.Equal(1, store.ExportHistoryCsv(filtered, " FILE-0000 "));
        Assert.Contains("file-0000.txt", System.Text.Encoding.UTF8.GetString(filtered.ToArray()));
        using var byFolder = new MemoryStream(); Assert.Equal(1005, store.ExportHistoryCsv(byFolder, root.Name));
        using var noMatch = new MemoryStream(); Assert.Equal(0, store.ExportHistoryCsv(noMatch, "missing"));
        Assert.Single(System.Text.Encoding.UTF8.GetString(noMatch.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(1000, store.GetHistory().Count); Assert.Single(store.GetRoots());
    }
    [Fact] public void CsvKeepsOneSnapshotWhileMonitoringAndHistoryResetWriteToDatabase()
    {
        using var store = Open(); var root = Root(); store.SaveRoot(root, start);
        var files = Enumerable.Range(0, 1005).Select(i => new FileStamp($"{i:0000}-{new string('a', 180)}.txt", $"id-{i}", 0, 1)).ToArray();
        store.SaveScan(root.Id, new(new(true, files, []), files.Select(file => new ConfirmedAddition(file, start, start)).ToArray()));
        bool concurrentWrite = false;
        using var output = new CallbackStream(() =>
        {
            store.ResetHistory(); Add(store, root, "during-export.txt"); concurrentWrite = true;
        });
        Assert.Equal(1005, store.ExportHistoryCsv(output)); Assert.True(concurrentWrite);
        var text = System.Text.Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains(files[0].RelativePath, text); Assert.Contains(files[^1].RelativePath, text);
        Assert.DoesNotContain("during-export.txt", text);
        Assert.Equal("during-export.txt", Assert.Single(store.GetHistory()).RelativePath);
    }
    private sealed class CallbackStream(Action onFirstWrite) : MemoryStream
    {
        private bool called;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!called) { called = true; onFirstWrite(); }
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!called) { called = true; onFirstWrite(); }
            base.Write(buffer);
        }
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
