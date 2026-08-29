using CensusScope.Core.Providers;

namespace CensusScope.Core.Tests;

public class CensusValuesTests
{
    [Fact]
    public void ParseEstimate_NumericValue_ReturnsValueWithoutFlag()
    {
        var (value, flag) = CensusValues.ParseEstimate("123.5");

        Assert.Equal(123.5, value);
        Assert.Null(flag);
    }

    [Fact]
    public void ParseEstimate_CannotComputeJam_ReturnsNullValueWithFlag()
    {
        var (value, flag) = CensusValues.ParseEstimate("-666666666");

        Assert.Null(value);
        Assert.NotNull(flag);
    }

    [Fact]
    public void ParseEstimate_JamValues_ReturnNullWithDistinctFlags()
    {
        var (v1, insufficientSample) = CensusValues.ParseEstimate("-999999999");
        var (v2, notApplicable) = CensusValues.ParseEstimate("-888888888");
        var (v3, cannotCompute) = CensusValues.ParseEstimate("-666666666");

        Assert.Null(v1);
        Assert.Null(v2);
        Assert.Null(v3);
        Assert.NotNull(insufficientSample);
        Assert.NotNull(notApplicable);
        Assert.NotNull(cannotCompute);
        Assert.NotEqual(insufficientSample, notApplicable);
        Assert.NotEqual(insufficientSample, cannotCompute);
        Assert.NotEqual(notApplicable, cannotCompute);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void ParseEstimate_MissingOrNonNumeric_ReturnsNullValue(string? raw)
    {
        var (value, flag) = CensusValues.ParseEstimate(raw);

        Assert.Null(value);
        Assert.NotNull(flag);
    }

    [Fact]
    public void ParseMoe_ControlledJam_ReturnsNull()
    {
        Assert.Null(CensusValues.ParseMoe("-555555555"));
    }

    [Fact]
    public void ParseMoe_NumericValue_ReturnsValue()
    {
        Assert.Equal(42d, CensusValues.ParseMoe("42"));
    }
}
