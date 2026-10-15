using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FolderSentinel.Core;

public sealed class StateStore : IDisposable
{
    private readonly object gate = new();
    private readonly SqliteConnection db;
    private readonly string databasePath;
    public StateStore(string file)
    {
        databasePath = System.IO.Path.GetFullPath(file);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        Execute("""
            CREATE TABLE IF NOT EXISTS roots(id TEXT PRIMARY KEY, config TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS snapshots(root_id TEXT PRIMARY KEY REFERENCES roots(id) ON DELETE CASCADE, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS additions(id INTEGER PRIMARY KEY AUTOINCREMENT, root_id TEXT NOT NULL REFERENCES roots(id) ON DELETE CASCADE, path TEXT NOT NULL, detected TEXT NOT NULL, confirmed TEXT NOT NULL, instant_batch INTEGER);
            CREATE INDEX IF NOT EXISTS ix_additions_root ON additions(root_id,id);
            CREATE TABLE IF NOT EXISTS schedules(id TEXT PRIMARY KEY, root_id TEXT NOT NULL REFERENCES roots(id) ON DELETE CASCADE, signature TEXT NOT NULL, cursor INTEGER NOT NULL, next_due TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS outbox(id INTEGER PRIMARY KEY AUTOINCREMENT, root_id TEXT NOT NULL REFERENCES roots(id) ON DELETE CASCADE, payload TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'Pending', attempts INTEGER NOT NULL DEFAULT 0, retry_at TEXT NOT NULL, error TEXT);
            CREATE TABLE IF NOT EXISTS batch_items(batch_id INTEGER NOT NULL REFERENCES outbox(id) ON DELETE CASCADE, event_id INTEGER NOT NULL REFERENCES additions(id) ON DELETE CASCADE, PRIMARY KEY(batch_id,event_id));
            CREATE INDEX IF NOT EXISTS ix_batch_items_event ON batch_items(event_id);
            CREATE TABLE IF NOT EXISTS preferences(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            PRAGMA user_version=1;
            """);
    }

    private SqliteCommand Command(string sql, params (string, object?)[] args)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private void Execute(string sql, params (string, object?)[] args) { using var cmd = Command(sql, args); cmd.ExecuteNonQuery(); }
    private object? Scalar(string sql, params (string, object?)[] args) { using var cmd = Command(sql, args); return cmd.ExecuteScalar(); }
    private static string Timestamp(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string time) => DateTimeOffset.Parse(time, CultureInfo.InvariantCulture);
    private long MaxEvent(string rootId) => Convert.ToInt64(Scalar("SELECT COALESCE(MAX(id),0) FROM additions WHERE root_id=$id", ("$id", rootId)));

    public List<WatchRoot> GetRoots()
    {
        lock (gate)
        {
            using var cmd = Command("SELECT config FROM roots ORDER BY rowid"); using var r = cmd.ExecuteReader();
            var roots = new List<WatchRoot>(); while (r.Read()) roots.Add(JsonSerializer.Deserialize<WatchRoot>(r.GetString(0), Json.Options)!); return roots;
        }
    }

    public void SaveRoot(WatchRoot root, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(root.Name)) throw new ArgumentException("表示名を入力してください。");
        root.Path = PathRules.Normalize(root.Path);
        if (root.Mode != NotificationMode.Immediate && root.Schedules.Count == 0) throw new ArgumentException("定期通知の予定を追加してください。");
        if (root.Schedules.Select(x => x.Id).Distinct().Count() != root.Schedules.Count) throw new ArgumentException("通知予定が重複しています。");
        foreach (var schedule in root.Schedules) ScheduleCalculator.Validate(schedule);
        lock (gate)
        {
            foreach (var other in GetRoots())
            {
                if (other.Id != root.Id && PathRules.Overlaps(root.Path, other.Path)) throw new ArgumentException("登録済みのフォルダと監視範囲が重なっています。");
                if (other.Id == root.Id && !string.Equals(other.Path, root.Path, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("監視先の変更は、新しいフォルダとして登録してください。");
            }
            using var tx = db.BeginTransaction();
            Execute("INSERT INTO roots(id,config) VALUES($id,$config) ON CONFLICT(id) DO UPDATE SET config=excluded.config", ("$id", root.Id), ("$config", JsonSerializer.Serialize(root, Json.Options)));
            var existing = new List<(string Id, string Signature)>();
            using (var cmd = Command("SELECT id,signature FROM schedules WHERE root_id=$id", ("$id", root.Id)))
            using (var r = cmd.ExecuteReader()) while (r.Read()) existing.Add((r.GetString(0), r.GetString(1)));
            foreach (var old in existing.Where(x => root.Schedules.All(s => s.Id != x.Id))) Execute("DELETE FROM schedules WHERE id=$id", ("$id", old.Id));
            foreach (var schedule in root.Schedules)
            {
                var old = existing.FirstOrDefault(x => x.Id == schedule.Id);
                if (old == default)
                    Execute("INSERT INTO schedules(id,root_id,signature,cursor,next_due) VALUES($id,$root,$sig,$cursor,$due)", ("$id", schedule.Id), ("$root", root.Id), ("$sig", schedule.Signature), ("$cursor", MaxEvent(root.Id)), ("$due", Timestamp(ScheduleCalculator.Next(schedule, now))));
                else if (old.Signature != schedule.Signature)
                    Execute("UPDATE schedules SET signature=$sig,next_due=$due WHERE id=$id", ("$id", schedule.Id), ("$sig", schedule.Signature), ("$due", Timestamp(ScheduleCalculator.Next(schedule, now))));
            }
            tx.Commit();
        }
    }

    public void DeleteRoot(string id) { lock (gate) Execute("DELETE FROM roots WHERE id=$id", ("$id", id)); }
    public Snapshot GetSnapshot(string id)
    {
        lock (gate) { var json = Scalar("SELECT json FROM snapshots WHERE root_id=$id", ("$id", id)) as string; return json is null ? Snapshot.Empty : JsonSerializer.Deserialize<Snapshot>(json, Json.Options)!; }
    }
    public bool SaveScan(string rootId, DiffResult diff)
    {
        lock (gate)
        {
            if (Scalar("SELECT id FROM roots WHERE id=$id", ("$id", rootId)) is null) return false;
            using var tx = db.BeginTransaction();
            Execute("INSERT INTO snapshots(root_id,json) VALUES($id,$json) ON CONFLICT(root_id) DO UPDATE SET json=excluded.json", ("$id", rootId), ("$json", JsonSerializer.Serialize(diff.Snapshot, Json.Options)));
            foreach (var addition in diff.Additions)
                Execute("INSERT INTO additions(root_id,path,detected,confirmed) VALUES($id,$path,$detected,$confirmed)", ("$id", rootId), ("$path", addition.File.RelativePath), ("$detected", Timestamp(addition.DetectedUtc)), ("$confirmed", Timestamp(addition.ConfirmedUtc)));
            tx.Commit(); return true;
        }
    }

    private long CreateBatch(WatchRoot root, string kind, string? scheduleId, List<(long Id, string Path)> events, DateTimeOffset now)
    {
        var payload = new NotificationPayload(root.Id, root.Name, root.Path, kind, scheduleId, events.Select(x => x.Id).ToList(), events.Select(x => x.Path).ToList());
        Execute("INSERT INTO outbox(root_id,payload,retry_at) VALUES($root,$payload,$now)", ("$root", root.Id), ("$payload", JsonSerializer.Serialize(payload, Json.Options)), ("$now", Timestamp(now)));
        long batchId = Convert.ToInt64(Scalar("SELECT last_insert_rowid()"));
        foreach (var item in events) Execute("INSERT INTO batch_items(batch_id,event_id) VALUES($batch,$event)", ("$batch", batchId), ("$event", item.Id));
        return batchId;
    }

    private List<(long Id, string Path)> GetEvents(string rootId, long after = 0, bool instant = false)
    {
        using var cmd = Command("SELECT id,path FROM additions WHERE root_id=$root AND id>$after" + (instant ? " AND instant_batch IS NULL" : "") + " ORDER BY id", ("$root", rootId), ("$after", after));
        using var reader = cmd.ExecuteReader(); var result = new List<(long, string)>(); while (reader.Read()) result.Add((reader.GetInt64(0), reader.GetString(1))); return result;
    }

    public void QueueNotifications(WatchRoot root, DateTimeOffset now, bool scanHealthy)
    {
        lock (gate)
        {
            if (Scalar("SELECT id FROM roots WHERE id=$id", ("$id", root.Id)) is null) return;
            using var tx = db.BeginTransaction();
            if (root.Mode is NotificationMode.Immediate or NotificationMode.Both)
            {
                var items = GetEvents(root.Id, instant: true);
                if (items.Count > 0)
                {
                    long batch = CreateBatch(root, "即時", null, items, now);
                    foreach (var item in items) Execute("UPDATE additions SET instant_batch=$batch WHERE id=$id", ("$batch", batch), ("$id", item.Id));
                }
            }
            if (scanHealthy && root.Mode is NotificationMode.Scheduled or NotificationMode.Both)
            {
                var states = new List<(string Id, long Cursor, DateTimeOffset Due)>();
                using (var cmd = Command("SELECT id,cursor,next_due FROM schedules WHERE root_id=$root", ("$root", root.Id)))
                using (var r = cmd.ExecuteReader()) while (r.Read()) states.Add((r.GetString(0), r.GetInt64(1), ParseTime(r.GetString(2))));
                foreach (var state in states.Where(x => x.Due <= now))
                {
                    var definition = root.Schedules.FirstOrDefault(x => x.Id == state.Id); if (definition is null) continue;
                    var items = GetEvents(root.Id, state.Cursor);
                    if (items.Count > 0) CreateBatch(root, "定期", definition.Id, items, now);
                    Execute("UPDATE schedules SET cursor=$cursor,next_due=$due WHERE id=$id", ("$cursor", Math.Max(state.Cursor, MaxEvent(root.Id))), ("$due", Timestamp(ScheduleCalculator.Next(definition, now))), ("$id", state.Id));
                }
            }
            tx.Commit();
        }
    }

    public DateTimeOffset? GetNextDue(string rootId)
    {
        lock (gate) { var value = Scalar("SELECT MIN(next_due) FROM schedules WHERE root_id=$id", ("$id", rootId)) as string; return value is null ? null : ParseTime(value); }
    }
    public List<OutboxItem> GetOutbox(DateTimeOffset now)
    {
        lock (gate)
        {
            using var cmd = Command("SELECT o.id,o.payload,o.attempts FROM outbox o JOIN roots r ON r.id=o.root_id WHERE status='Pending' AND json_extract(r.config,'$.Enabled')=1 AND retry_at<=$now ORDER BY o.id LIMIT 20", ("$now", Timestamp(now)));
            using var r = cmd.ExecuteReader(); var items = new List<OutboxItem>();
            while (r.Read()) items.Add(new(r.GetInt64(0), JsonSerializer.Deserialize<NotificationPayload>(r.GetString(1), Json.Options)!, r.GetInt32(2))); return items;
        }
    }
    public void CompleteBatch(long id) { lock (gate) Execute("UPDATE outbox SET status='Sent',error=NULL WHERE id=$id", ("$id", id)); }
    public bool IsBatchPending(long id)
    {
        lock (gate) return Scalar("SELECT o.id FROM outbox o JOIN roots r ON r.id=o.root_id WHERE o.id=$id AND o.status='Pending' AND json_extract(r.config,'$.Enabled')=1", ("$id", id)) is not null;
    }
    public void ResetHistory()
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            Execute("DELETE FROM outbox; DELETE FROM additions;");
            // Keep snapshots, pending file candidates, schedules and AUTOINCREMENT sequences.
            tx.Commit();
        }
    }
    public void FailBatch(long id, int attempts, string error, DateTimeOffset now)
    {
        lock (gate) Execute("UPDATE outbox SET attempts=$attempts,retry_at=$retry,error=$error WHERE id=$id", ("$id", id), ("$attempts", attempts + 1), ("$retry", Timestamp(now.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempts + 1, 8)) * 5)))), ("$error", error));
    }
    public List<HistoryRow> GetHistory(string? rootId = null, int limit = 1000)
    {
        lock (gate)
        {
            using var cmd = Command(HistoryQuery, ("$root", rootId), ("$limit", limit));
            using var reader = cmd.ExecuteReader();
            return ReadHistory(reader).ToList();
        }
    }
    private const string HistoryQuery = """
        SELECT a.id,a.root_id,a.path,a.detected,a.confirmed,
          COALESCE((SELECT group_concat(label,' / ') FROM (SELECT DISTINCT CASE WHEN o.status='Sent' THEN json_extract(o.payload,'$.Kind') || '：OS送信済み' ELSE json_extract(o.payload,'$.Kind') || '：送信待ち' END AS label FROM batch_items bi JOIN outbox o ON o.id=bi.batch_id WHERE bi.event_id=a.id)), '次回集計待ち'),
          json_extract(r.config,'$.Name'),json_extract(r.config,'$.Path')
        FROM additions a JOIN roots r ON r.id=a.root_id WHERE ($root IS NULL OR a.root_id=$root) ORDER BY a.id DESC LIMIT $limit
        """;
    private static IEnumerable<HistoryRow> ReadHistory(SqliteDataReader reader)
    {
        while (reader.Read()) yield return new(reader.GetInt64(0), reader.GetString(1), reader.GetString(6), reader.GetString(2), ParseTime(reader.GetString(3)), ParseTime(reader.GetString(4)), reader.GetString(5), reader.GetString(7));
    }
    public long ExportHistoryCsv(Stream output, string search = "")
    {
        // A separate WAL reader keeps one consistent export without blocking live monitoring.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = HistoryQuery;
        cmd.Parameters.AddWithValue("$root", DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", -1);
        using var reader = cmd.ExecuteReader();
        return HistoryCsv.Write(output, ReadHistory(reader), search);
    }
    public string? GetPreference(string key) { lock (gate) return Scalar("SELECT value FROM preferences WHERE key=$key", ("$key", key)) as string; }
    public void SetPreference(string key, string value) { lock (gate) Execute("INSERT INTO preferences(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$key", key), ("$value", value)); }
    public void Dispose() { lock (gate) db.Dispose(); }
}
