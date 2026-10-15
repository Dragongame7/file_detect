using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace FolderSentinel.Tests;

public sealed class HistoryCsvTests
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-16T03:04:05Z");
    private static HistoryRow Row(string name = "日本語,フォルダ", string file = "sub\\資料,\"確定\".txt") =>
        new(123, "root", name, file, Time, Time.AddSeconds(4), "即時：OS送信済み / 定期：送信待ち", "C:\\監視\\");
    private static List<string[]> Parse(MemoryStream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, true, leaveOpen: true);
        using var parser = new TextFieldParser(reader) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var rows = new List<string[]>();
        while (!parser.EndOfData) rows.Add(parser.ReadFields()!);
        return rows;
    }
    [Fact] public void CsvRoundTripsJapaneseQuotesCommasAndNewlinesWithBomAndCrLf()
    {
        var row = Row("日本語,\"フォルダ\"\r\n改行");
        using var stream = new MemoryStream(); Assert.Equal(1, HistoryCsv.Write(stream, [row]));
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, stream.ToArray()[..3]);
        Assert.EndsWith("\r\n", Encoding.UTF8.GetString(stream.ToArray()));
        var rows = Parse(stream); Assert.Equal(2, rows.Count); Assert.Equal(7, rows[0].Length);
        Assert.Equal("123", rows[1][0]); Assert.Equal(row.RootName, rows[1][1]); Assert.Equal(row.RootPath, rows[1][2]);
        Assert.Equal(row.RelativePath, rows[1][3]); Assert.Equal(row.Notifications, rows[1][6]);
        Assert.Equal(Time, DateTimeOffset.Parse(rows[1][4])); Assert.Equal(Time.AddSeconds(4), DateTimeOffset.Parse(rows[1][5]));
        Assert.True(stream.CanRead);
    }
    [Theory]
    [InlineData("=HYPERLINK(\"https://example.com\")")]
    [InlineData("+1+2")]
    [InlineData("-1+2")]
    [InlineData("@SUM(1)")]
    [InlineData(" \t=1+2")]
    [InlineData("\r\n=1+2")]
    public void SpreadsheetFormulaLikeValuesAreWrittenAsText(string value)
    {
        using var stream = new MemoryStream(); HistoryCsv.Write(stream, [Row(value, value)]);
        var row = Parse(stream)[1]; Assert.Equal("'" + value, row[1]); Assert.Equal("'" + value, row[3]);
    }
    [Fact] public void EmptyExportStillHasHeaderAndUtf8Bom()
    {
        using var stream = new MemoryStream(); Assert.Equal(0, HistoryCsv.Write(stream, []));
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, stream.ToArray()[..3]);
        Assert.Equal(7, Assert.Single(Parse(stream)).Length);
    }
}
