namespace Tropicast.Station.Audio.Linux.Tests;

public sealed class PulseProcessTests
{
    [Fact]
    public void Missing_client_reports_required_packages_instead_of_native_diagnostics()
    {
        var error = Assert.Throws<IOException>(() => new PulseProcess($"tc_missing_{Guid.NewGuid():N}"));
        Assert.Contains("pulseaudio-utils", error.Message, StringComparison.Ordinal);
        Assert.Contains("pipewire-pulse", error.Message, StringComparison.Ordinal);
    }
}
