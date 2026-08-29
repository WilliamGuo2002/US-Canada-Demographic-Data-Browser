using CensusScope.Core.Parsing;

namespace CensusScope.Core.Tests;

public class SdmxCodelistParserTests
{
    private static string LoadFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CL_GEO_PR.xml"));

    [Fact]
    public void Parse_ClGeoPr_ReturnsFourteenCodes()
    {
        var codes = SdmxCodelistParser.Parse(LoadFixture());
        Assert.Equal(14, codes.Count);
    }

    [Fact]
    public void Parse_ClGeoPr_OntarioCodeHasEnglishName()
    {
        var codes = SdmxCodelistParser.Parse(LoadFixture());

        var ontario = codes.SingleOrDefault(c => c.Id == "2021A000235");
        Assert.NotNull(ontario);
        Assert.Equal("Ontario", ontario.NameEn);
    }

    [Fact]
    public void Parse_ClGeoPr_CanadaCodePresent()
    {
        var codes = SdmxCodelistParser.Parse(LoadFixture());

        var canada = codes.SingleOrDefault(c => c.Id == "2021A000011124");
        Assert.NotNull(canada);
        Assert.Equal("Canada", canada.NameEn);
    }
}
