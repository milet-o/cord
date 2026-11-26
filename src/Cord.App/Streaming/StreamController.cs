using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Cord.App.Audio;
using Cord.App.Platform;
using Cord.App.Video;
using Cord.Core.Consoles;
using Cord.Core.Settings;
using Cord.Media;
using Cord.Protocol;
using Cord.Protocol.Av;
using Cord.Protocol.Crypto;
using Cord.Protocol.Input;
using Cord.Protocol.Session;
using Cord.Protocol.Stream;

namespace Cord.App.Streaming;

internal enum StreamState
{
    Idle,
    Connecting,
    Streaming,
    Ended,
}

internal sealed record StreamStatistics(
    double Fps,
    double VideoMbps,
    long FecRecoveries,
    string? Decoder,
    int Width,
    int Height,
    int RttMs,
    double LossPercent,
    double JitterMs,
    int FramesLost,
    LatencySnapshot Latency,
    string FrameGenStatus,
    long GeneratedFrames,
    int DetectedSourceFps);

/// <summary>
/// Runs one Remote Play session: connects, decodes video on a dedicated high-priority thread,
/// plays audio, offloads replay/webshare buffering off the hot path, and forwards controller input.
/// </summary>
internal sealed class StreamController : IStreamSink, IAsyncDisposable
{
    private readonly VideoPresenter _presenter;
    private readonly HapticsOutput _haptics;
    private readonly ConsoleWebShare? _webShare;
    private readonly string? _hostId;
    private readonly PooledFrameQueue _frames = new(capacity: 4);
    private readonly PooledFrameQueue _sideChannelFrames = new(capacity: 8);
    private readonly LatencyTracker _latency = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _stopLock = new();
    private readonly ReplayBuffer _replayBuffer = new();
    private readonly Stopwatch _replayClock = Stopwatch.StartNew();
    private readonly Stopwatch _statsClock = Stopwatch.StartNew();

    private Task? _stopping;
    private RemotePlaySession? _session;
    private VideoDecoder? _decoder;
    private OpusAudioDecoder? _audioDecoder;
    private AudioOutput? _audio;
    private Thread? _decodeThread;
    private Task? _sideChannelTask;
    private TaskCompletionSource<string?>? _passcodeRequest;
    private VideoCodecKind _codec;
    private bool _hardwareDecoding;
    private float _audioVolume = 1.0f;
    private volatile bool _decodeOnRenderThread;
    private long _videoBytes;
    private long _fecRecoveries;
    private long _framesAtLastStats;
    private long _bytesAtLastStats;
    private StreamStatistics? _statistics;

    public StreamController(VideoPresenter presenter, HapticsOutput haptics, ConsoleWebShare? webShare = null, string? hostId = null)
    {
        _presenter = presenter;
        _haptics = haptics;
        _webShare = webShare;
        _hostId = hostId;
    }

    public StreamState State { get; private set; }

    public bool AudioOnly { get; private set; }

    public string? HostId => _hostId;

    public string? ConsoleName { get; private set; }

    public int RttMs => _session?.Network.RttMilliseconds ?? 0;

    public string? Message { get; private set; }

    public bool IsStopping => _stop.IsCancellationRequested;

    public bool PasscodeRequested => _passcodeRequest is { Task.IsCompleted: false };

    public bool PasscodeRetry { get; private set; }

    public bool CanSaveReplayClip => _replayBuffer.HasClipData;

    public LatencyTracker Latency => _latency;

    /// <summary>Gamepad feedback from the console, applied by the UI thread.</summary>
    public ConcurrentQueue<Action<Input.GamepadInput>> GamepadFeedback { get; } = new();

    public void Start(RpCryptTables tables, IPAddress address, StoredConsole console, AppSettings settings, bool dualSense)
    {
        AudioOnly = false;
        ConsoleName = console.Nickname;
        State = StreamState.Connecting;
        Message = $"Connecting to {console.Nickname}...";
        _codec = settings.Quality.Codec == VideoCodec.H264 ? VideoCodecKind.H264 : VideoCodecKind.Hevc;
        _hardwareDecoding = settings.HardwareDecoding;
        _audioVolume = Math.Clamp(settings.AudioVolume, 0f, 1f);
        var options = settings.Quality.ToStreamOptions(dualSense, settings.MaxReportedPacketLoss);
        _ = Task.Run(() => ConnectAsync(tables, address, console, options));
    }

    /// <summary>Starts a headless Audio + Controller session without opening or decoding video.</summary>
    public void StartAudioOnly(RpCryptTables tables, IPAddress address, StoredConsole console, AppSettings settings, bool dualSense)
    {
        AudioOnly = true;
        ConsoleName = console.Nickname;
        State = StreamState.Connecting;
        Message = $"Connecting Audio + Pad to {console.Nickname}...";
        _codec = VideoCodecKind.H264;
        _hardwareDecoding = false;
        _audioVolume = Math.Clamp(settings.AudioVolume, 0f, 1f);
        var options = StreamOptions.ForAudioAndController(dualSense);
        _ = Task.Run(() => ConnectAsync(tables, address, console, options));
    }

    public void AnswerPasscode(string? passcode) => _passcodeRequest?.TrySetResult(passcode);

    public void UpdateInput(in ControllerState state)
    {
        _latency.RecordInputPoll();
        _session?.Stream.UpdateController(state);
    }

    public void SetAudioVolume(float volume)
    {
        _audioVolume = Math.Clamp(volume, 0f, 1f);
        _audio?.SetVolume(_audioVolume);
    }

    public string SaveReplayClip() => _replayBuffer.SaveMp4();

    public async Task PutConsoleToRestAsync(CancellationToken cancellationToken = default)
    {
        if (_session is { } session)
            await session.Ctrl.GoToRestAsync(cancellationToken);
        await StopAsync();
    }

    public StreamStatistics? Statistics(FrameGenMode frameGenMode = FrameGenMode.Off, float refreshHz = 60f)
    {
        var elapsed = _statsClock.Elapsed.TotalSeconds;
        if (elapsed >= 1 && _session is not null)
        {
            var frames = _presenter.FramesPresented;
            var bytes = Interlocked.Read(ref _videoBytes);
            var (width, height) = _presenter.VideoSize;
            _statistics = new StreamStatistics(
                (frames - _framesAtLastStats) / elapsed,
                (bytes - _bytesAtLastStats) * 8 / elapsed / 1_000_000,
                Interlocked.Read(ref _fecRecoveries),
                DescribeDecoder(),
                width,
                height,
                _session.Network.RttMilliseconds,
                _session.Stream.LossPercent,
                _session.Stream.JitterMs,
                _session.Stream.FramesLost,
                _latency.Snapshot(),
                _presenter.FrameGenStatus(frameGenMode, refreshHz),
                _presenter.GeneratedFramesPresented,
                _presenter.DetectedSourceFps);
            _framesAtLastStats = frames;
            _bytesAtLastStats = bytes;
            _statsClock.Restart();
        }

        return _statistics;
    }

    /// <summary>Ends the session; safe to call several times and from any thread.</summary>
    public Task StopAsync()
    {
        lock (_stopLock)
            return _stopping ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        _webShare?.DetachLocalStream();
        _passcodeRequest?.TrySetResult(null);
        await _stop.CancelAsync();
        _frames.Complete();
        _sideChannelFrames.Complete();
        if (_session is not null)
            await _session.DisposeAsync();
        _session = null;
        if (_decodeThread is not null && _decodeThread.IsAlive)
            _decodeThread.Join(TimeSpan.FromSeconds(2));
        if (_sideChannelTask is not null)
            await _sideChannelTask;
        _replayBuffer.Reset();
        if (State != StreamState.Ended)
        {
            // The user ended the stream: nothing to report.
            State = StreamState.Ended;
            Message = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _frames.Dispose();
        _sideChannelFrames.Dispose();
        _decoder?.Dispose();
        _audioDecoder?.Dispose();
        _audio?.Dispose();
        _stop.Dispose();
    }

    void IStreamSink.OnStreamInfo(IReadOnlyList<VideoStreamProfile> profiles, AudioFormat audio)
    {
        var channels = audio.Channels is >= 1 and <= 8 ? audio.Channels : 2;
        var sampleRate = audio.SampleRate > 0 ? audio.SampleRate : 48000;
        _audioDecoder = new OpusAudioDecoder(sampleRate, channels);
        _audio = new AudioOutput(sampleRate, channels, _audioVolume, AudioOnly ? 40 : 80);

        if (AudioOnly)
        {
            _decodeOnRenderThread = false;
            return;
        }

        // Only fall back to render-thread decoding when the shared D3D11 device is single-threaded (SDL_Renderer mode).
        _decodeOnRenderThread = _hardwareDecoding && _presenter.SharedDevice != 0 && !_presenter.SupportsMultithreadedDecode;
        if (!_decodeOnRenderThread)
        {
            var accel = _hardwareDecoding ? HardwareAcceleration.Auto : HardwareAcceleration.None;
            var sharedDevice = _hardwareDecoding && _presenter.SupportsMultithreadedDecode ? _presenter.SharedDevice : 0;
            _decoder = new VideoDecoder(_codec, new VideoDecoderOptions(accel, sharedDevice));
        }

        _replayClock.Restart();
        if (profiles.Count > 0)
            _replayBuffer.Configure(_codec, profiles[0].Width, profiles[0].Height, sampleRate, channels, profiles[0].Header);

        _webShare?.PublishStreamInfo(profiles, audio, _codec == VideoCodecKind.Hevc);

        _sideChannelTask = Task.Run(SideChannelLoop);
        if (!_decodeOnRenderThread)
        {
            _decodeThread = new Thread(DecodeLoop)
            {
                Name = "CordVideoDecode",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _decodeThread.Start();
        }
    }

    /// <summary>Called by the render loop: decodes queued frames only in SDL_Renderer single-threaded D3D11 mode.</summary>
    public void DecodePending()
    {
        if (!_decodeOnRenderThread || _stop.IsCancellationRequested)
            return;
        _decoder ??= new VideoDecoder(_codec, new VideoDecoderOptions(HardwareAcceleration.Auto, _presenter.SharedDevice));
        while (_frames.TryDequeue(out var item))
        {
            try
            {
                Decode(_decoder, item);
            }
            finally
            {
                item.Return();
            }
        }
    }

    void IStreamSink.OnVideoFrame(ReadOnlyMemory<byte> frame, ReadOnlyMemory<byte>? streamHeader, bool recoveredWithFec)
    {
        if (AudioOnly)
            return;

        Interlocked.Add(ref _videoBytes, frame.Length);
        if (recoveredWithFec)
            Interlocked.Increment(ref _fecRecoveries);

        var headerArray = streamHeader?.ToArray();
        // Wake the video decoder first with zero extra CPU work on the UDP receive thread.
        _frames.EnqueueCopy(frame.Span, headerArray);
        _sideChannelFrames.EnqueueCopy(frame.Span, headerArray);
    }

    void IStreamSink.OnAudioFrame(ReadOnlySpan<byte> frame, bool haptics)
    {
        if (haptics)
        {
            _haptics.Play(frame);
            return;
        }

        if (!AudioOnly)
        {
            var tsUs = _replayClock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
            _replayBuffer.AddAudioFrame(frame, tsUs);
            _webShare?.PublishAudioFrame(frame);
        }

        if (_audioDecoder is null || _audio is null)
            return;
        _audio.Play(_audioDecoder.Decode(frame));
    }

    void IStreamSink.OnRumble(byte left, byte right)
    {
        _webShare?.PublishRumble(left, right);
        GamepadFeedback.Enqueue(g =>
        {
            if (_haptics.Available)
                g.Rumble(0, 0);
            else
                g.Rumble(left, right);
        });
    }

    void IStreamSink.OnTriggerEffects(TriggerEffect left, TriggerEffect right) =>
        GamepadFeedback.Enqueue(g => g.SetTriggerEffects(left, right));

    void IStreamSink.OnPadInfo(PadInfo info) => GamepadFeedback.Enqueue(g => g.SetLightbar(info.Red, info.Green, info.Blue));

    void IStreamSink.OnDisconnected(string reason)
    {
        if (!IsStopping)
            Message = StreamEndMessages.ForDisconnect(reason);
    }

    private async Task ConnectAsync(RpCryptTables tables, IPAddress address, StoredConsole console, StreamOptions options)
    {
        try
        {
            if (_webShare is not null && _hostId is not null && string.Equals(_webShare.HostId, _hostId, StringComparison.OrdinalIgnoreCase))
            {
                if (await _webShare.DetachWebOwnedSessionForLocalPlayAsync())
                    await Task.Delay(500, _stop.Token);
                if (!AudioOnly)
                {
                    _webShare.AttachLocalStream(
                        _hostId,
                        () =>
                        {
                            if (_session is { } active)
                                _ = active.Stream.RequestKeyFrameAsync();
                        },
                        ct => PutConsoleToRestAsync(ct),
                        () => _replayBuffer.HasClipData ? _replayBuffer.SaveMp4() : null);
                }
            }

            _session = await RemotePlaySession.ConnectAsync(
                tables, address, console.ToCredentials(), options, this, RequestPasscodeAsync, _stop.Token);
            State = StreamState.Streaming;
            Message = null;
            var reason = await _session.WaitForEndAsync();
            Message ??= StreamEndMessages.ForEnd(reason);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is RemotePlayException or IOException or System.Net.Sockets.SocketException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            Message = StreamEndMessages.ForFailure(e);
        }

        if (IsStopping)
            Message = null;
        State = StreamState.Ended;
        await StopAsync();
    }

    private string DescribeDecoder() => _decoder switch
    {
        null => "starting",
        { KeepsFramesOnGpu: true } => $"{_decoder.HardwareDevice} (zero-copy)",
        { HardwareDevice: { } device } => $"{device} (copy-back)",
        _ => "software",
    };

    private Task<string?> RequestPasscodeAsync(bool previousAttemptFailed, CancellationToken cancellationToken)
    {
        PasscodeRetry = previousAttemptFailed;
        _passcodeRequest = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => _passcodeRequest.TrySetResult(null));
        return _passcodeRequest.Task;
    }

    private void DecodeLoop()
    {
        using var mmcss = new MmcssThreadScope("Games");
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (!_frames.TryDequeueWait(out var item, _stop.Token))
                    return;

                try
                {
                    Decode(_decoder!, item);
                }
                finally
                {
                    item.Return();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SideChannelLoop()
    {
        try
        {
            var isHevc = _codec == VideoCodecKind.Hevc;
            while (!_stop.IsCancellationRequested)
            {
                if (!_sideChannelFrames.TryDequeueWait(out var item, _stop.Token))
                    return;

                try
                {
                    var span = item.Buffer.AsSpan(0, item.Length);
                    var isKey = ContainsKeyframeNal(span, isHevc);
                    var tsUs = _replayClock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
                    var hdr = item.Header is not null ? item.Header.AsSpan() : ReadOnlySpan<byte>.Empty;
                    _replayBuffer.AddVideoFrame(span, hdr, isKey, tsUs);
                    _webShare?.PublishVideoFrame(item.Buffer.AsMemory(0, item.Length), item.Header is not null ? item.Header.AsMemory() : null);
                }
                finally
                {
                    item.Return();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Decode(VideoDecoder decoder, in PooledVideoFrame item)
    {
        _presenter.MarkFrameDecodeTiming(item.EnqueuedTimestamp, Stopwatch.GetTimestamp());
        decoder.Decode(
            item.Buffer.AsSpan(0, item.Length),
            _presenter,
            item.Header is { } header ? header.AsSpan() : ReadOnlySpan<byte>.Empty);
    }

    private static bool ContainsKeyframeNal(ReadOnlySpan<byte> annexB, bool isHevc)
    {
        var i = 0;
        while (i + 3 < annexB.Length)
        {
            int startLen;
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
                startLen = 3;
            else if (i + 4 < annexB.Length && annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1)
                startLen = 4;
            else
            {
                i++;
                continue;
            }

            var headerIdx = i + startLen;
            if (headerIdx >= annexB.Length)
                break;

            if (isHevc)
            {
                var nalType = (annexB[headerIdx] >> 1) & 0x3F;
                if (nalType is 19 or 20 or 21)
                    return true;
            }
            else
            {
                var nalType = annexB[headerIdx] & 0x1F;
                if (nalType == 5)
                    return true;
            }

            i = headerIdx + 1;
        }

        return false;
    }
}
