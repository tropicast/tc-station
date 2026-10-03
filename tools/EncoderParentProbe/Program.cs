using System.Diagnostics;

var start = new ProcessStartInfo(args[0])
{
    UseShellExecute = false,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
};
foreach (var argument in new[]
{
    "-hide_banner", "-nostdin", "-loglevel", "warning",
    "-f", "f32le", "-ar", "48000", "-ac", "2", "-probesize", "32", "-analyzeduration", "0",
    "-i", "pipe:0", "-c:a", "libmp3lame", "-f", "mp3", "pipe:1",
})
{
    start.ArgumentList.Add(argument);
}
using var encoder = Process.Start(start) ?? throw new IOException("Could not start encoder probe.");
Console.WriteLine(encoder.Id);
await Console.Out.FlushAsync();
var output = encoder.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
var diagnostics = encoder.StandardError.ReadToEndAsync();
var packet = new byte[960 * 8];
while (!encoder.HasExited)
{
    await encoder.StandardInput.BaseStream.WriteAsync(packet);
    await Task.Delay(20);
}
await output;
await diagnostics;
