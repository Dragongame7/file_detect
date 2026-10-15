namespace FolderSentinel.Core;

public static class DiffEngine
{
    public static DiffResult Compare(Snapshot previous, ScanResult scan, DateTimeOffset now, TimeSpan? stableFor = null)
    {
        if (!scan.RootAccessible) return new(previous, []);
        var files = scan.Files.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        bool unavailable(string path) => scan.UnavailablePrefixes.Any(p => PathRules.IsWithinRelative(path, p));
        foreach (var old in previous.Files.Where(x => unavailable(x.RelativePath))) files.TryAdd(old.RelativePath, old);
        // Baseline is complete only once every reachable directory has been enumerated successfully.
        if (!previous.Initialized)
            return new(new Snapshot(scan.UnavailablePrefixes.Count == 0, files.Values.ToArray(), []), []);

        var oldByPath = previous.Files.ToDictionary(x => x.RelativePath, StringComparer.OrdinalIgnoreCase);
        var oldIdentities = previous.Files.Select(x => x.Identity).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
        var pendingById = previous.Pending.GroupBy(x => x.File.Identity).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var pending = new List<PendingAddition>();
        var additions = new List<ConfirmedAddition>();
        var wait = stableFor ?? TimeSpan.FromSeconds(4);
        foreach (var file in files.Values)
        {
            if (unavailable(file.RelativePath))
            {
                var held = previous.Pending.FirstOrDefault(x => x.File.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase));
                if (held is not null) pending.Add(held with { StableSinceUtc = now, Samples = 0 });
                continue;
            }
            // Existing paths are updates/replacements. A pending new file is the exception.
            if (oldByPath.TryGetValue(file.RelativePath, out var old) && old.Identity != file.Identity) continue;
            if (pendingById.TryGetValue(file.Identity, out var candidate))
            {
                bool same = candidate.File.Length == file.Length && candidate.File.LastWriteUtcTicks == file.LastWriteUtcTicks;
                candidate = candidate with { File = file, StableSinceUtc = same ? candidate.StableSinceUtc : now, Samples = same ? candidate.Samples + 1 : 1 };
                if (candidate.Samples >= 3 && now - candidate.StableSinceUtc >= wait)
                    additions.Add(new(file, candidate.DetectedUtc, now));
                else pending.Add(candidate);
            }
            else if (!oldByPath.ContainsKey(file.RelativePath) && !oldIdentities.Contains(file.Identity))
                pending.Add(new(file, now, now, 1));
        }
        return new(new Snapshot(true, files.Values.ToArray(), pending), additions);
    }
}
