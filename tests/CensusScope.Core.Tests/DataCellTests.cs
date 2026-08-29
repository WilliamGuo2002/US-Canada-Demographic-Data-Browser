using CensusScope.Core.Models;

namespace CensusScope.Core.Tests;

public class DataCellTests
{
    [Fact]
    public void Display_CountWithMoe_FormatsThousandsAndPlusMinus()
    {
        var cell = new DataCell(1234.0, 56.0, null, "count");
        Assert.Equal("1,234 ±56", cell.Display);
    }

    [Fact]
    public void Display_NullValueWithFlag_ShowsFlag()
    {
        var cell = new DataCell(null, null, "suppressed", "count");
        Assert.Equal("suppressed", cell.Display);
    }

    [Fact]
    public void Display_NullValueWithoutFlag_ShowsEmDash()
    {
        var cell = new DataCell(null, null, null, "count");
        Assert.Equal("—", cell.Display);
    }

    [Fact]
    public void Display_Currency_FormatsWithDollarSignAndThousands()
    {
        var cell = new DataCell(74000.0, null, null, "currency");
        Assert.Equal("$74,000", cell.Display);
    }

    [Fact]
    public void Display_Percent_AppendsPercentSign()
    {
        var cell = new DataCell(13.2, null, null, "percent");
        Assert.Equal("13.2%", cell.Display);
    }

    [Fact]
    public void Display_CountWithoutMoe_HasNoPlusMinus()
    {
        var cell = new DataCell(500.0, null, null, "count");
        Assert.Equal("500", cell.Display);
    }
}
