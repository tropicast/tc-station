using System.Diagnostics;

namespace Tropicast.Station.Audio.Linux;

internal sealed class PulseProcess : IDisposable
{
    private readonly Process _process;
    private readonly Task _diagnostics;
    private readonly object _sync = new();
    private bool _disposed;

    internal PulseProcess(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = start };
        try
        {
            _process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _process.Dispose();
            throw new IOException($"Cannot start {executable}. Install pulseaudio-utils (Debian/Ubuntu) or libpulse (Arch) and run PulseAudio or pipewire-pulse.");
        }

        _diagnostics = DrainDiagnosticsAsync();
    }

    internal Stream Output => _process.StandardOutput.BaseStream;
    internal StreamReader Lines => _process.StandardOutput;

    private async Task DrainDiagnosticsAsync()
    {
        // Drain without retaining native diagnostics (which may contain paths/device names).
        var buffer = new char[1024];
        while (await _process.StandardError.ReadAsync(buffer).ConfigureAwait(false) > 0)
        {
        }
    }

    internal async Task<int> WaitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await _diagnostics.ConfigureAwait(false);
        return _process.ExitCode;
    }

    internal void Stop()
    {
        lock (_sync)
        {
            if (_disposed || _process.HasExited)
            {
                return;
            }

            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (_process.HasExited)
            {
                // The child exited between the state check and kill.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new IOException("Could not stop the PulseAudio client process.");
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _process.WaitForExit();
            _diagnostics.GetAwaiter().GetResult();
            _process.Dispose();
            _disposed = true;
        }
    }

    internal static async Task<string> CommandAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var child = new PulseProcess("pactl", arguments);
        try
        {
            var output = child.Lines.ReadToEndAsync(deadline.Token);
            if (await child.WaitAsync(deadline.Token).ConfigureAwait(false) != 0)
            {
                throw new IOException("PulseAudio command failed. Check that PulseAudio or pipewire-pulse is running and accessible to this user.");
            }

            return await output.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("PulseAudio did not respond within 10 seconds. Check the audio service.");
        }
    }
}
