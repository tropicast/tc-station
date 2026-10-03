using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Tropicast.Station.Encoding;

public sealed partial class FfmpegBroadcastEncoder : IBroadcastEncoder, IDisposable
{
    private readonly FfmpegExecutable _executable;
    private readonly ILogger<FfmpegBroadcastEncoder> _logger;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _shutdown = new();
    private IEncoderSession? _active;
    private bool _disposed;
    public FfmpegBroadcastEncoder(ILogger<FfmpegBroadcastEncoder> logger) : this(new FfmpegExecutable(), logger) { }
    internal FfmpegBroadcastEncoder(FfmpegExecutable executable, ILogger<FfmpegBroadcastEncoder> logger)
    {
        _executable = executable;
        _logger = logger;
    }

    public async Task<IEncoderSession> StartAsync(BroadcastTarget target, EncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Profile.ContentType != "audio/mpeg")
        {
            throw new ArgumentException("The MP3 encoder requires an audio/mpeg connection profile.", nameof(target));
        }
        options ??= EncoderOptions.FromProfile(target.Profile);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is { Completion.IsCompleted: false })
            {
                throw new InvalidOperationException("An encoder is already publishing. Stop it before starting another stream.");
            }
            if (_active is not null)
            {
                await ReleaseAsync(_active).ConfigureAwait(false);
                _active = null;
            }
            var effectiveTarget = new BroadcastTarget(target.Profile with
            {
                BitrateKbps = options.BitrateKbps, SampleRate = options.Format.SampleRate, Channels = options.Format.Channels,
            }, target.Password);
            var connection = await TropicastSourceConnection.ConnectAsync(effectiveTarget, linked.Token).ConfigureAwait(false);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                _active = new FfmpegSession(_executable.Start(options), connection, options);
                return _active;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReleaseAsync(IEncoderSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            LogShutdownFailure(_logger);
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _gate.Wait();
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_active is not null)
                {
                    ReleaseAsync(_active).GetAwaiter().GetResult();
                    _active = null;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Encoder shutdown reported a publishing failure.")]
    private static partial void LogShutdownFailure(ILogger logger);
}
