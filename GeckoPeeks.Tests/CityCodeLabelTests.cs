using GeckoPeeks.Routing;
using GeckoPeeks.Text;
using Xunit;

namespace GeckoPeeks.Tests;

public class CityCodeLabelTests
{
    [Fact]
    public void Zayo_core_switch_label_is_skipped()
    {
        var city = CityCode.Extract("ae27.cs3.msp1.us.eth.zayo.com");
        Assert.NotEqual("cs3", city);
        Assert.Equal("msp1", city);
        Assert.Equal("den1", CityCode.Extract("ae5.cs1.den1.us.eth.zayo.com"));
    }

    [Fact]
    public void Known_four_letter_code_is_returned()
    {
        Assert.Equal("kanc", CityCode.Extract("kanc-b1-link.ip.twelve99.net"));
        Assert.Equal("palo", CityCode.Extract("palo-b2-link.ip.twelve99.net"));
    }

    [Fact]
    public void Unknown_four_letter_token_is_not_a_city()
    {
        Assert.Equal("", CityCode.Extract("link.ip.example.net"));
    }

    [Fact]
    public void New_codes_have_display_names()
    {
        Assert.Equal("Kansas City", CityNames.Display("kanc"));
        Assert.Equal("Palo Alto", CityNames.Display("palo"));
        Assert.True(CityNames.IsKnownCode("kanc"));
    }
}
