namespace FolderSentinel.Tests;

public class DiffTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-15T00:00:00Z");
    private static FileStamp File(string name, string id = "1", long size = 100, long modified = 1) => new(name, id, size, modified);
    private static ScanResult Scan(params FileStamp[] files) => new(files, [], true);
    private static DiffResult Step(Snapshot old, int second, params FileStamp[] files) => DiffEngine.Compare(old, Scan(files), Start.AddSeconds(second));

    [Fact] public void BaselineDoesNotNotifyExistingFiles()
    {
        var result = Step(Snapshot.Empty, 0, File("already.txt")); Assert.True(result.Snapshot.Initialized); Assert.Empty(result.Additions); Assert.Empty(result.Snapshot.Pending);
    }
    [Fact] public void AdditionWaitsForThreeStableSamplesAndIsEmittedOnce()
    {
        var first = Step(new(true, [], []), 0, File("new.txt")); Assert.Single(first.Snapshot.Pending);
        var second = Step(first.Snapshot, 2, File("new.txt")); Assert.Empty(second.Additions);
        var third = Step(second.Snapshot, 4, File("new.txt")); Assert.Single(third.Additions); Assert.Empty(third.Snapshot.Pending);
        Assert.Empty(Step(third.Snapshot, 6, File("new.txt")).Additions);
    }
    [Fact] public void CopyProgressResetsTheStabilityWindow()
    {
        var a = Step(new(true, [], []), 0, File("copy.bin", size: 1));
        var b = Step(a.Snapshot, 4, File("copy.bin", size: 2));
        var c = Step(b.Snapshot, 6, File("copy.bin", size: 2)); Assert.Empty(c.Additions);
        Assert.Single(Step(c.Snapshot, 8, File("copy.bin", size: 2)).Additions);
    }
    [Fact] public void RenameOrInternalMoveAfterRestartDoesNotNotify()
    {
        var old = new Snapshot(true, [File("before.txt")], []);
        var result = Step(old, 0, File(Path.Combine("sub", "after.txt"))); Assert.Empty(result.Additions); Assert.Empty(result.Snapshot.Pending);
    }
    [Fact] public void ExistingFileUpdatesAndAtomicReplacementAreNotAdditions()
    {
        var old = new Snapshot(true, [File("report.xlsx")], []);
        Assert.Empty(Step(old, 0, File("report.xlsx", size: 999)).Snapshot.Pending);
        Assert.Empty(Step(old, 0, File("report.xlsx", id: "replacement")).Snapshot.Pending);
    }
    [Fact] public void TemporarySaveRenamedOverExistingFileIsNotNew()
    {
        var old = new Snapshot(true, [File("report.xlsx")], []);
        var tmp = Step(old, 0, File("report.xlsx"), File("temp.tmp", "2"));
        var replaced = Step(tmp.Snapshot, 2, File("report.xlsx", "2")); Assert.Empty(replaced.Snapshot.Pending); Assert.Empty(replaced.Additions);
    }
    [Fact] public void RemovedThenReintroducedIdentityIsNew()
    {
        var old = new Snapshot(true, [File("old.txt")], []);
        var removed = Step(old, 0);
        Assert.Single(Step(removed.Snapshot, 2, File("returned.txt")).Snapshot.Pending);
    }
    [Fact] public void PendingAdditionFollowsRename()
    {
        var a = Step(new(true, [], []), 0, File("upload.tmp"));
        var b = Step(a.Snapshot, 2, File("complete.txt"));
        var c = Step(b.Snapshot, 4, File("complete.txt")); Assert.Equal("complete.txt", Assert.Single(c.Additions).File.RelativePath);
    }
    [Fact] public void PartialFailurePreservesOnlyAffectedSubtree()
    {
        var held = File(Path.Combine("denied", "a.txt")); var gone = File(Path.Combine("denied2", "b.txt"), "2");
        var result = DiffEngine.Compare(new(true, [held, gone], []), new([], ["denied"], true), Start);
        Assert.Equal(held, Assert.Single(result.Snapshot.Files));
    }
    [Fact] public void DisconnectionDoesNotEraseBaseline()
    {
        var old = new Snapshot(true, [File("known.txt")], []);
        var result = DiffEngine.Compare(old, new([], [""], false), Start); Assert.Same(old, result.Snapshot);
    }
    [Fact] public void PartialBaselineDoesNotBecomeInitialized()
    {
        var partial = DiffEngine.Compare(Snapshot.Empty, new([File("a")], ["denied"], true), Start); Assert.False(partial.Snapshot.Initialized);
        var complete = Step(partial.Snapshot, 5, File("a"), File("b", "2")); Assert.True(complete.Snapshot.Initialized); Assert.Empty(complete.Snapshot.Pending);
    }
    [Fact] public void ZeroLengthFileAndNestedFolderContentsAreIncluded()
    {
        var files = new[] { File(Path.Combine("folder", "empty.txt"), size: 0), File(Path.Combine("folder", "deep", "b.txt"), "2") };
        var a = Step(new(true, [], []), 0, files); var b = Step(a.Snapshot, 2, files); var c = Step(b.Snapshot, 4, files);
        Assert.Equal(2, c.Additions.Count);
    }
}
