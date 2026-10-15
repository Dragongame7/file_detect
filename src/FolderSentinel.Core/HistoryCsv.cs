using System.Globalization;
using System.Text;

namespace FolderSentinel.Core;

public static class HistorySearch
{
    public static bool Matches(HistoryRow row, string search) => search.Length == 0
        || row.RootName.Contains(search, StringComparison.OrdinalIgnoreCase)
        || row.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase);
}

public static class HistoryCsv
{
    public static long Write(Stream output, IEnumerable<HistoryRow> rows, string search = "")
    {
        using var writer = new StreamWriter(output, new UTF8Encoding(true), 65536, leaveOpen: true) { NewLine = "\r\n" };
        WriteRow(writer, ["検出ID", "フォルダ名", "監視フォルダ", "相対パス", "検出日時", "確定日時", "通知状況"]);
        long count = 0;
        search = search.Trim();
        foreach (var row in rows)
        {
            if (!HistorySearch.Matches(row, search)) continue;
            WriteRow(writer, [row.Id.ToString(CultureInfo.InvariantCulture), row.RootName, row.RootPath, row.RelativePath,
                row.DetectedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                row.ConfirmedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture), row.Notifications]);
            count++;
        }
        return count;
    }

    private static void WriteRow(TextWriter writer, IEnumerable<string> values)
    {
        // Excel must treat file names and folder labels as text, including formula-like names.
        writer.WriteLine(string.Join(",", values.Select(value =>
        {
            var first = value.AsSpan().TrimStart();
            if (!first.IsEmpty && first[0] is '=' or '+' or '-' or '@') value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        })));
    }
}
