using System.IO;
using System.Text;
using System.Text.Json;

namespace GhostHunterPC.Services;

public enum ReportKind
{
    DeviceScan,
    FileInvestigator,
    Duplicates,
    SystemMonitor,
    BaselineChanges
}

public sealed record ReportRow(string Category, string Item, string Value);
public sealed record ReportDocument(string Report, DateTimeOffset CreatedAt, IReadOnlyList<ReportRow> Rows);

public sealed class ReportExportService
{
    public void ExportJson(string path, ReportDocument report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);
        File.WriteAllText(Path.GetFullPath(path), JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        }), new UTF8Encoding(false));
    }

    public void ExportCsv(string path, ReportDocument report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);
        var content = new StringBuilder();
        content.AppendLine("Category,Item,Value");
        var rows = new[]
        {
            new ReportRow("Metadata", "Report", report.Report),
            new ReportRow("Metadata", "Created at (UTC)", report.CreatedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture))
        }.Concat(report.Rows);
        foreach (var row in rows)
        {
            content.Append(Csv(row.Category)).Append(',')
                .Append(Csv(row.Item)).Append(',')
                .Append(Csv(row.Value)).AppendLine();
        }

        File.WriteAllText(Path.GetFullPath(path), content.ToString(), new UTF8Encoding(true));
    }

    private static string Csv(string value)
    {
        var firstNonWhitespace = value.AsSpan().TrimStart();
        if (!firstNonWhitespace.IsEmpty && firstNonWhitespace[0] is '=' or '+' or '-' or '@')
        {
            value = $"'{value}";
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
