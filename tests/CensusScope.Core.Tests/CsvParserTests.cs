using CensusScope.Core.Parsing;

namespace CensusScope.Core.Tests;

public class CsvParserTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Parse_TorontoFixture_HasMoreThanTwentyRows()
    {
        var rows = CsvParser.Parse(File.ReadAllText(FixturePath("toronto_tier1.csv")));
        Assert.True(rows.Count > 20, $"Expected > 20 rows, got {rows.Count}");
    }

    [Fact]
    public void Parse_TorontoFixture_HeaderLookupByName_FindsExpectedObservation()
    {
        var rows = CsvParser.Parse(File.ReadAllText(FixturePath("toronto_tier1.csv")));

        var match = rows.Single(r =>
            r["CHARACTERISTIC"] == "230" &&
            r["GENDER"] == "1" &&
            r["STATISTIC"] == "1");

        Assert.Equal("74000", match["OBS_VALUE"]);
    }

    [Fact]
    public void Parse_QuotedFieldWithComma_PreservesComma()
    {
        var rows = CsvParser.Parse("a,b\n\"hello, world\",2");

        Assert.Single(rows);
        Assert.Equal("hello, world", rows[0]["a"]);
        Assert.Equal("2", rows[0]["b"]);
    }

    [Fact]
    public void Parse_EscapedQuotes_UnescapesToSingleQuote()
    {
        var rows = CsvParser.Parse("a,b\n\"she said \"\"hi\"\"\",x");

        Assert.Single(rows);
        Assert.Equal("she said \"hi\"", rows[0]["a"]);
    }

    [Fact]
    public void Parse_CrlfAndLf_ProduceIdenticalResults()
    {
        var lf = CsvParser.Parse("a,b\n1,2\n3,4\n");
        var crlf = CsvParser.Parse("a,b\r\n1,2\r\n3,4\r\n");

        Assert.Equal(2, lf.Count);
        Assert.Equal(2, crlf.Count);
        for (var i = 0; i < lf.Count; i++)
        {
            Assert.Equal(lf[i]["a"], crlf[i]["a"]);
            Assert.Equal(lf[i]["b"], crlf[i]["b"]);
        }
    }

    [Fact]
    public void Parse_QuotedFieldContainingNewline_KeptInsideOneRecord()
    {
        var rows = CsvParser.Parse("a,b\n\"line1\nline2\",2");

        Assert.Single(rows);
        Assert.Equal("line1\nline2", rows[0]["a"]);
    }
}
