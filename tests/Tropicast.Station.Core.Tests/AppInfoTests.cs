namespace Tropicast.Station.Core.Tests;

public sealed class AppInfoTests
{
    [Fact]
    public void Reads_product_and_strips_commit_metadata_from_version()
    {
        var info = new AppInfo(typeof(AppInfo).Assembly);

        Assert.Equal("Tropicast Station", info.ProductName);
        Assert.Equal("0.1.0", info.Version);
    }

    [Fact]
    public void Uses_assembly_metadata_from_any_assembly()
    {
        var info = new AppInfo(typeof(object).Assembly);

        Assert.False(string.IsNullOrWhiteSpace(info.ProductName));
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.DoesNotContain('+', info.Version);
    }
}
