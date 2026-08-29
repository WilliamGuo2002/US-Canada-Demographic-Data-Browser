using System.Globalization;
using System.Text;
using CensusScope.Core.Models;

namespace CensusScope.Core.Parsing;

/// <summary>Serializes a <see cref="QueryResult"/> to RFC 4180 CSV.</summary>
public static class CsvWriter
{
    /// <summary>
    /// Writes the result as CSV.
    /// </summary>
    /// <remarks>
    /// Two decisions matter for downstream use:
    /// <list type="bullet">
    /// <item>Values are written as RAW NUMBERS, not the formatted strings shown on screen, and the
    /// margin of error gets its own column — so the file can be summed, divided and charted without
    /// stripping currency symbols. A suppressed value is an empty cell with the reason in a
    /// companion flag column, never a sentinel like -666666666.</item>
    /// <item>Every row carries its source code (ACS variable id / Census characteristic id in
    /// profile mode, GEOID / DGUID in comparison mode) as the first column, so the file joins to
    /// boundary files and other extracts without matching on display names.</item>
    /// </list>
    /// The provenance block (title, source, universe, notes, export time) is written as leading
    /// <c>#</c> comment lines; set <paramref name="includeMetadata"/> to false for a bare table that
    /// loads into Excel or pandas with no skiprows argument.
    /// </remarks>
    public static string Write(QueryResult result, bool includeMetadata = true, DateTimeOffset? exportedAt = null)
    {
        var sb = new StringBuilder();

        if (includeMetadata)
        {
            AppendComment(sb, "Title", result.Title);
            AppendComment(sb, "Source", result.SourceAttribution);
            AppendComment(sb, "Universe", result.Universe);
            if (!string.IsNullOrWhiteSpace(result.Notes))
                AppendComment(sb, "Notes", result.Notes);
            AppendComment(sb, "Exported",
                (exportedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            sb.AppendLine("#");
        }

        var anyMoe = result.Rows.Any(r => r.Cells.Any(c => c.Moe is not null));
        var anyFlag = result.Rows.Any(r => r.Cells.Any(c => c.Flag is not null));

        var header = new List<string> { "Code", "Label" };
        foreach (var columnHeader in result.ColumnHeaders)
        {
            header.Add(columnHeader);
            if (anyMoe) header.Add(columnHeader + " MOE");
            if (anyFlag) header.Add(columnHeader + " Flag");
        }
        sb.AppendLine(string.Join(',', header.Select(Escape)));

        foreach (var row in result.Rows)
        {
            var fields = new List<string> { row.Code ?? "", row.Label };
            for (var i = 0; i < result.ColumnHeaders.Count; i++)
            {
                var cell = i < row.Cells.Count ? row.Cells[i] : null;
                fields.Add(Number(cell?.Value));
                if (anyMoe) fields.Add(Number(cell?.Moe));
                if (anyFlag) fields.Add(cell?.Flag ?? "");
            }
            sb.AppendLine(string.Join(',', fields.Select(Escape)));
        }

        return sb.ToString();
    }

    private static void AppendComment(StringBuilder sb, string label, string value) =>
        sb.AppendLine("# " + label + ": " + value.Replace("\r", " ").Replace("\n", " "));

    private static string Number(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture) ?? "";

    /// <summary>Quotes a field when it contains a comma, quote, or line break (RFC 4180 §2.6-2.7).</summary>
    private static string Escape(string field)
    {
        if (field.Length == 0)
            return field;
        var mustQuote = field.Contains(',') || field.Contains('"')
            || field.Contains('\n') || field.Contains('\r')
            || field[0] == ' ' || field[^1] == ' ';
        return mustQuote ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }
}
