using System.Text.Json;
using System.Text.Json.Serialization;

namespace FolderSentinel.Core;

public enum NotificationMode { Immediate, Scheduled, Both }
public enum ScheduleFrequency { Daily, Weekly, Monthly }

public sealed class WatchRoot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public NotificationMode Mode { get; set; } = NotificationMode.Both;
    public List<NotificationSchedule> Schedules { get; set; } = [];
    public string ModeLabel => Mode switch { NotificationMode.Immediate => "即時通知", NotificationMode.Scheduled => "定期通知", _ => "即時 ＋ 定期" };
    public WatchRoot Copy() => JsonSerializer.Deserialize<WatchRoot>(JsonSerializer.Serialize(this, Json.Options), Json.Options)!;
}

public sealed class NotificationSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Weekly;
    public string Time { get; set; } = "09:00";
    public List<DayOfWeek> Days { get; set; } = [DayOfWeek.Monday];
    public int MonthDay { get; set; } = 1;
    public bool LastDay { get; set; }
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public string Summary => Frequency switch
    {
        ScheduleFrequency.Daily => $"毎日 {Time}",
        ScheduleFrequency.Weekly => $"毎週 {string.Join("・", Days.OrderBy(x => ((int)x + 6) % 7).Select(x => "日月火水木金土"[(int)x]))}曜日 {Time}",
        _ => $"毎月 {(LastDay ? "末日" : MonthDay + "日")} {Time}"
    };
    [JsonIgnore] public string Signature => JsonSerializer.Serialize(new { Frequency, Time, Days = Days.Order().ToArray(), MonthDay, LastDay, TimeZoneId });
}

public sealed record FileStamp(string RelativePath, string Identity, long Length, long LastWriteUtcTicks);
public sealed record PendingAddition(FileStamp File, DateTimeOffset DetectedUtc, DateTimeOffset StableSinceUtc, int Samples);
public sealed record ConfirmedAddition(FileStamp File, DateTimeOffset DetectedUtc, DateTimeOffset ConfirmedUtc);
public sealed record ScanResult(IReadOnlyList<FileStamp> Files, IReadOnlyList<string> UnavailablePrefixes, bool RootAccessible, string? Error = null);
public sealed record Snapshot(bool Initialized, IReadOnlyList<FileStamp> Files, IReadOnlyList<PendingAddition> Pending)
{
    public static Snapshot Empty { get; } = new(false, [], []);
}
public sealed record DiffResult(Snapshot Snapshot, IReadOnlyList<ConfirmedAddition> Additions);
public sealed record RootRuntime(string RootId, string Status, string Detail, DateTimeOffset? LastScanUtc, DateTimeOffset? NextDueUtc, int FileCount, int PendingCount, bool Healthy);
public sealed record HistoryRow(long Id, string RootId, string RootName, string RelativePath, DateTimeOffset DetectedUtc, DateTimeOffset ConfirmedUtc, string Notifications, string RootPath = "")
{
    public string DetectedLocal => DetectedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");
}
public sealed record NotificationPayload(string RootId, string RootName, string RootPath, string Kind, string? ScheduleId, List<long> EventIds, List<string> FileNames);
public sealed record OutboxItem(long Id, NotificationPayload Payload, int Attempts);

public static class Json
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

public static class PathRules
{
    public static string Normalize(string path) => System.IO.Path.GetFullPath(path.Trim()).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
    public static bool Overlaps(string a, string b) => Normalize(a).StartsWith(Normalize(b), StringComparison.OrdinalIgnoreCase) || Normalize(b).StartsWith(Normalize(a), StringComparison.OrdinalIgnoreCase);
    public static bool IsWithinRelative(string path, string prefix) => prefix.Length == 0 || path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
