namespace Tropicast.Station.Audio.Linux.Tests;

public sealed class PulseDeviceTests
{
    private const string Sources = """
        [
          {"index":1,"name":"usb-input","description":"USB mixer","sample_specification":"s16le 2ch 44100Hz"},
          {"index":2,"name":"sink-a.monitor","description":"Monitor of speaker A","sample_specification":"float32le 2ch 48000Hz"},
          {"index":3,"name":"monitor-b","description":"Monitor of speaker B","sample_specification":"s32le 2ch 48000Hz"}
        ]
        """;
    private const string Sinks = """
        [{"name":"sink-a","monitor_source":"sink-a.monitor"},{"name":"sink-b","monitor_source":3}]
        """;
    private const string Info = """{"default_source_name":"usb-input","default_sink_name":"sink-b"}""";

    [Fact]
    public void Classifies_named_and_numeric_monitors_with_separate_input_and_output_defaults()
    {
        var devices = PulseDevices.Parse(Sources, Sinks, Info);
        Assert.Equal(3, devices.Count);
        Assert.Equal(AudioDeviceKind.Input, devices[0].Kind);
        Assert.True(devices[0].IsDefault);
        Assert.Equal(new AudioFormat(44100, 2), devices[0].NativeFormat);
        Assert.Equal(AudioDeviceKind.Loopback, devices[1].Kind);
        Assert.False(devices[1].IsDefault);
        Assert.Equal(AudioDeviceKind.Loopback, devices[2].Kind);
        Assert.True(devices[2].IsDefault);
    }

    [Fact]
    public void Default_sink_changes_relabel_monitor_without_changing_identity()
    {
        var before = PulseDevices.Parse(Sources, Sinks, Info);
        var after = PulseDevices.Parse(Sources, Sinks, Info.Replace("sink-b", "sink-a", StringComparison.Ordinal));
        Assert.Equal(before.Select(d => d.Id), after.Select(d => d.Id));
        Assert.True(after[1].IsDefault);
        Assert.False(after[2].IsDefault);
    }

    [Fact]
    public void Default_source_changes_do_not_relabel_default_sink_monitor()
    {
        var devices = PulseDevices.Parse(Sources, Sinks, Info.Replace("usb-input", "sink-a.monitor", StringComparison.Ordinal));
        Assert.False(devices[0].IsDefault);
        Assert.False(devices[1].IsDefault);
        Assert.True(devices[2].IsDefault);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""[{"name":"broken"}]""")]
    [InlineData("""[{"index":1,"name":"input","description":null,"sample_specification":"s16le 2ch 48000Hz"}]""")]
    [InlineData("""[{"index":1,"name":"input","description":"Input","sample_specification":"s16le 9ch 48000Hz"}]""")]
    [InlineData("""[{"index":1,"name":"input","description":"Input","sample_specification":"s16le 2ch 9999999999999Hz"}]""")]
    public void Malformed_device_json_is_not_an_empty_success(string sources)
        => Assert.Throws<IOException>(() => PulseDevices.Parse(sources, Sinks, Info));

    [Fact]
    public void Duplicate_source_names_are_explicit_errors()
        => Assert.Throws<IOException>(() => PulseDevices.Parse(Sources.Replace("monitor-b", "usb-input", StringComparison.Ordinal), Sinks, Info));

    [Theory]
    [InlineData("Event 'new' on source #12", true)]
    [InlineData("Event 'remove' on sink #42", true)]
    [InlineData("Event 'change' on server #0", true)]
    [InlineData("Event 'change' on card #1", true)]
    [InlineData("Event 'new' on source-output #15", false)]
    [InlineData("Event 'change' on sink-input #5", false)]
    [InlineData("Event 'remove' on client #2", false)]
    public void Only_device_topology_events_trigger_refresh(string line, bool expected)
        => Assert.Equal(expected, PulseDevices.IsDeviceEvent(line));
}
