using System.Text;

namespace CensusScope.Core.Parsing;

/// <summary>
/// Minimal RFC-4180 CSV parser: quoted fields, escaped double quotes ("" inside quotes),
/// commas and newlines inside quoted fields, and both \r\n and \n record terminators.
/// </summary>
public static class CsvParser
{
    /// <summary>
    /// Parses CSV text whose first record is a header row. Each subsequent record becomes a
    /// dictionary keyed by header name. Rows shorter than the header simply omit the trailing
    /// keys; blank lines are skipped.
    /// </summary>
    /// <param name="csv">Raw CSV text including the header record.</param>
    /// <returns>One dictionary per data row, keyed by header column name.</returns>
    public static List<Dictionary<string, string>> Parse(string csv)
    {
        var records = SplitRecords(csv);
        var result = new List<Dictionary<string, string>>();
        if (records.Count < 2)
            return result;

        var header = records[0];
        for (var r = 1; r < records.Count; r++)
        {
            var fields = records[r];
            var row = new Dictionary<string, string>(header.Count, StringComparer.Ordinal);
            var count = Math.Min(header.Count, fields.Count);
            for (var c = 0; c < count; c++)
                row[header[c]] = fields[c];
            result.Add(row);
        }
        return result;
    }

    /// <summary>Splits CSV text into records of fields, honoring quoting rules.</summary>
    private static List<List<string>> SplitRecords(string csv)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        void EndField()
        {
            record.Add(field.ToString());
            field.Clear();
        }

        void EndRecord()
        {
            EndField();
            // Skip blank lines (a record consisting of a single empty field).
            if (record.Count != 1 || record[0].Length != 0)
                records.Add(record);
            record = [];
        }

        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i++; // escaped quote
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    EndField();
                    break;
                case '\r':
                    if (i + 1 < csv.Length && csv[i + 1] == '\n')
                        i++;
                    EndRecord();
                    break;
                case '\n':
                    EndRecord();
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        // Flush a final record not terminated by a newline.
        if (field.Length > 0 || record.Count > 0)
            EndRecord();

        return records;
    }
}
