using CensusScope.Core.Models;
using CensusScope.Core.Parsing;

namespace CensusScope.Core.Tests;

/// <summary>
/// The exported file is the product's data deliverable, so these pin the two properties that
/// make it usable downstream: raw numbers rather than display strings, and a join key per row.
/// </summary>
public class CsvWriterTests
{
    private static QueryResult Result(params ResultRow[] rows) => new()
    {
        Title = "Sex by age — Alameda County, California",
        SourceAttribution = "U.S. Census Bureau, ACS 2020-2024 5-Year, Table B01001",
        Universe = "Total population",
        Notes = "Estimates carry a 90% margin of error.",
        ColumnHeaders = ["Estimate"],
        Rows = rows,
    };

    private static ResultRow Row(string code, string label, DataCell cell, int indent = 0) =>
        new() { Label = label, Code = code, Indent = indent, Cells = [cell] };

    [Fact]
    public void WritesRawNumbers_NotFormattedDisplayStrings()
    {
        var csv = CsvWriter.Write(Result(
            Row("B19013_001E", "Median household income", new DataCell(104589, 2145, null, "currency"))));

        Assert.Contains("104589", csv, StringComparison.Ordinal);
        Assert.Contains("2145", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("$104,589", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRowCarriesItsSourceCode()
    {
        var csv = CsvWriter.Write(Result(
            Row("06001", "Alameda County", new DataCell(1_649_060, null, null)),
            Row("06075", "San Francisco County", new DataCell(808_988, null, null))));

        var lines = csv.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        Assert.Equal("Code,Label,Estimate", lines.First(l => l.StartsWith("Code", StringComparison.Ordinal)));
        Assert.Contains(lines, l => l.StartsWith("06001,Alameda County,", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("06075,San Francisco County,", StringComparison.Ordinal));
    }

    [Fact]
    public void SuppressedValue_IsEmptyWithTheReasonInAFlagColumn_NotASentinel()
    {
        var csv = CsvWriter.Write(Result(
            Row("B01001_001E", "Total", new DataCell(null, null, "insufficient sample"))));

        Assert.DoesNotContain("-666666666", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("-999999999", csv, StringComparison.Ordinal);
        Assert.Contains("insufficient sample", csv, StringComparison.Ordinal);
        Assert.Contains("Estimate Flag", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldsContainingCommasOrQuotes_AreEscapedPerRfc4180()
    {
        var csv = CsvWriter.Write(Result(
            Row("3520005", "Toronto, City", new DataCell(2_794_356, null, null)),
            Row("X", "The \"big\" one", new DataCell(1, null, null))));

        Assert.Contains("\"Toronto, City\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"The \"\"big\"\" one\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataBlock_CarriesTitleSourceAndUniverse_AndCanBeOmitted()
    {
        var withMeta = CsvWriter.Write(Result(Row("A", "Row", new DataCell(1, null, null))));
        Assert.Contains("# Title: Sex by age", withMeta, StringComparison.Ordinal);
        Assert.Contains("# Source: U.S. Census Bureau", withMeta, StringComparison.Ordinal);
        Assert.Contains("# Universe: Total population", withMeta, StringComparison.Ordinal);

        var bare = CsvWriter.Write(Result(Row("A", "Row", new DataCell(1, null, null))), includeMetadata: false);
        Assert.StartsWith("Code,Label", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("#", bare, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTripsThroughTheProjectsOwnCsvParser()
    {
        var csv = CsvWriter.Write(
            Result(Row("3520005", "Toronto, City", new DataCell(2_794_356, null, null))),
            includeMetadata: false);

        var parsed = CsvParser.Parse(csv);

        Assert.Single(parsed);
        Assert.Equal("3520005", parsed[0]["Code"]);
        Assert.Equal("Toronto, City", parsed[0]["Label"]);
        Assert.Equal("2794356", parsed[0]["Estimate"]);
    }
}
