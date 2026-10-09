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
    // One publisher per mount: MP3 and Opus outputs of one capture run side by side.
    private readonly Dictionary<string, IEncoderSession> _active = new(StringComparer.Ordinal);
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
        options ??= EncoderOptions.FromProfile(target.Profile);
        if (target.Profile.ContentType != options.ContentType)
        {
            throw new ArgumentException($"The {options.CodecName} encoder requires an {options.ContentType} connection profile.", nameof(target));
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var mount = target.Profile.Mount;
            if (_active.TryGetValue(mount, out var previous))
            {
                if (!previous.Completion.IsCompleted)
                {
                    throw new InvalidOperationException("An encoder is already publishing to this mount. Stop it before starting another stream.");
                }
                _active.Remove(mount);
                await ReleaseAsync(previous).ConfigureAwait(false);
            }
            var effectiveTarget = new BroadcastTarget(target.Profile with
            {
                BitrateKbps = options.BitrateKbps, Channels = options.Format.Channels,
                // Ice-Audio-Info reports the encoded rate: Opus is always 48 kHz.
                SampleRate = options.Codec == EncoderCodec.Opus ? 48000 : options.Format.SampleRate,
            }, target.Password);
            var connection = await TropicastSourceConnection.ConnectAsync(effectiveTarget, linked.Token).ConfigureAwait(false);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                var session = new FfmpegSession(_executable.Start(options), connection, options);
                _active[mount] = session;
                return session;
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
                foreach (var session in _active.Values)
                {
                    ReleaseAsync(session).GetAwaiter().GetResult();
                }
                _active.Clear();
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
