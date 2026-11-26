using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Cord.Media;

/// <summary>
/// Rolling 30-second in-memory buffer of encoded H.264/HEVC access units and Opus audio packets
/// that remuxes directly to an MP4 clip with zero video/audio re-encoding.
/// </summary>
public sealed class ReplayBuffer
{
    private readonly long _maxDurationUs;
    private readonly Lock _lock = new();
    private readonly Queue<VideoSample> _video = new();
    private readonly Queue<AudioSample> _audio = new();

    private VideoCodecKind _codec = VideoCodecKind.H264;
    private int _width = 1920;
    private int _height = 1080;
    private int _audioSampleRate = 48000;
    private int _audioChannels = 2;
    private byte[] _latestHeader = [];

    public ReplayBuffer(TimeSpan? duration = null)
    {
        var d = duration ?? TimeSpan.FromSeconds(30);
        _maxDurationUs = Math.Max(1_000_000L, (long)d.TotalMicroseconds);
    }

    public bool HasClipData
    {
        get
        {
            lock (_lock)
                return _video.Any(v => v.IsKeyframe);
        }
    }

    public static string DefaultClipPath()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(videos))
            videos = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folder = Path.Combine(videos, "Cord");
        Directory.CreateDirectory(folder);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(folder, $"Cord-{stamp}.mp4");
    }

    public void Configure(VideoCodecKind codec, int width, int height, int audioSampleRate, int audioChannels, ReadOnlySpan<byte> streamHeader)
    {
        lock (_lock)
        {
            _codec = codec;
            if (width > 0)
                _width = width;
            if (height > 0)
                _height = height;
            if (audioSampleRate > 0)
                _audioSampleRate = audioSampleRate;
            if (audioChannels is >= 1 and <= 8)
                _audioChannels = audioChannels;
            if (!streamHeader.IsEmpty)
                _latestHeader = streamHeader.ToArray();
            _video.Clear();
            _audio.Clear();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _video.Clear();
            _audio.Clear();
            _latestHeader = [];
        }
    }

    public void AddVideoFrame(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> streamHeader, bool isKeyframe, long timestampUs)
    {
        if (frame.IsEmpty)
            return;

        lock (_lock)
        {
            if (!streamHeader.IsEmpty)
                _latestHeader = streamHeader.ToArray();

            var data = frame.ToArray();
            var header = !streamHeader.IsEmpty
                ? streamHeader.ToArray()
                : (isKeyframe && _latestHeader.Length > 0 ? _latestHeader : null);

            _video.Enqueue(new VideoSample(data, header, isKeyframe, timestampUs));
            PruneLocked(timestampUs);
        }
    }

    public void AddAudioFrame(ReadOnlySpan<byte> opusFrame, long timestampUs)
    {
        if (opusFrame.IsEmpty)
            return;

        lock (_lock)
        {
            _audio.Enqueue(new AudioSample(opusFrame.ToArray(), timestampUs));
            PruneLocked(timestampUs);
        }
    }

    public unsafe string SaveMp4(string? outputPath = null)
    {
        FfmpegLibrary.Load();

        VideoCodecKind codec;
        int width;
        int height;
        int sampleRate;
        int channels;
        byte[]? header;
        List<VideoSample> video;
        List<AudioSample> audio;

        lock (_lock)
        {
            var allVideo = _video.ToArray();
            var startIdx = Array.FindIndex(allVideo, static v => v.IsKeyframe);
            if (startIdx < 0)
                throw new InvalidOperationException("Ainda não há nenhum keyframe no buffer para salvar o clipe.");

            video = [.. allVideo[startIdx..]];
            var startUs = video[0].TimestampUs;
            var endUs = video[^1].TimestampUs;
            audio = [.. _audio.Where(a => a.TimestampUs >= startUs && a.TimestampUs <= endUs + 50_000)];

            codec = _codec;
            width = _width;
            height = _height;
            sampleRate = _audioSampleRate;
            channels = _audioChannels;
            header = video[0].Header ?? (_latestHeader.Length > 0 ? _latestHeader : null);
        }

        var path = string.IsNullOrWhiteSpace(outputPath) ? DefaultClipPath() : outputPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var tempPath = $"{path}.tmp.mp4";
        if (File.Exists(tempPath))
            File.Delete(tempPath);

        AVFormatContext* fmtCtx = null;
        AVPacket* pkt = null;
        try
        {
            var formatErr = ffmpeg.avformat_alloc_output_context2(&fmtCtx, null, "mp4", tempPath);
            if (formatErr < 0 || fmtCtx == null)
                throw new InvalidOperationException($"FFmpeg failed to create mp4 context: {FfmpegLibrary.ErrorText(formatErr)}");

            var videoStream = ffmpeg.avformat_new_stream(fmtCtx, null);
            if (videoStream == null)
                throw new InvalidOperationException("FFmpeg failed to allocate video stream.");
            videoStream->id = 0;
            videoStream->time_base = new AVRational { num = 1, den = 1_000_000 };

            var vpar = videoStream->codecpar;
            vpar->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            vpar->codec_id = codec == VideoCodecKind.Hevc ? AVCodecID.AV_CODEC_ID_HEVC : AVCodecID.AV_CODEC_ID_H264;
            vpar->width = width;
            vpar->height = height;
            vpar->codec_tag = 0;
            if (header is { Length: > 0 })
                AllocateExtradata(vpar, header);

            AVStream* audioStream = null;
            if (audio.Count > 0)
            {
                audioStream = ffmpeg.avformat_new_stream(fmtCtx, null);
                if (audioStream == null)
                    throw new InvalidOperationException("FFmpeg failed to allocate audio stream.");
                audioStream->id = 1;
                audioStream->time_base = new AVRational { num = 1, den = 1_000_000 };

                var apar = audioStream->codecpar;
                apar->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
                apar->codec_id = AVCodecID.AV_CODEC_ID_OPUS;
                apar->sample_rate = sampleRate;
                apar->frame_size = sampleRate / 100;
                ffmpeg.av_channel_layout_default(&apar->ch_layout, channels);
                apar->codec_tag = 0;
                AllocateExtradata(apar, BuildOpusHead(channels, sampleRate));
            }

            if ((fmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                var openErr = ffmpeg.avio_open(&fmtCtx->pb, tempPath, ffmpeg.AVIO_FLAG_WRITE);
                if (openErr < 0)
                    throw new InvalidOperationException($"FFmpeg failed to open clip file: {FfmpegLibrary.ErrorText(openErr)}");
            }

            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "movflags", "faststart", 0);
            try
            {
                var hdrErr = ffmpeg.avformat_write_header(fmtCtx, &opts);
                if (hdrErr < 0)
                    throw new InvalidOperationException($"FFmpeg failed to write mp4 header: {FfmpegLibrary.ErrorText(hdrErr)}");
            }
            finally
            {
                ffmpeg.av_dict_free(&opts);
            }

            pkt = ffmpeg.av_packet_alloc();
            if (pkt == null)
                throw new InvalidOperationException("FFmpeg failed to allocate AVPacket.");

            var srcTimeBase = new AVRational { num = 1, den = 1_000_000 };
            var baseUs = video[0].TimestampUs;
            long lastVideoPts = -1;
            long lastAudioPts = -1;

            var vi = 0;
            var ai = 0;
            while (vi < video.Count || ai < audio.Count)
            {
                var writeVideo = ai >= audio.Count || (vi < video.Count && video[vi].TimestampUs <= audio[ai].TimestampUs);
                if (writeVideo)
                {
                    var sample = video[vi++];
                    var ptsUs = Math.Max(lastVideoPts + 1, Math.Max(0, sample.TimestampUs - baseUs));
                    lastVideoPts = ptsUs;

                    byte[] packetBytes;
                    var firstHeader = sample.Header ?? header;
                    if (vi == 1 && firstHeader is { Length: > 0 } h && !StartsWithHeader(sample.Data, h))
                    {
                        packetBytes = new byte[h.Length + sample.Data.Length];
                        Buffer.BlockCopy(h, 0, packetBytes, 0, h.Length);
                        Buffer.BlockCopy(sample.Data, 0, packetBytes, h.Length, sample.Data.Length);
                    }
                    else
                    {
                        packetBytes = sample.Data;
                    }

                    var durationUs = vi < video.Count ? Math.Clamp(video[vi].TimestampUs - sample.TimestampUs, 1_000, 100_000) : 16_667;
                    WritePacket(fmtCtx, pkt, videoStream, packetBytes, ptsUs, durationUs, sample.IsKeyframe, srcTimeBase);
                }
                else
                {
                    var sample = audio[ai++];
                    var ptsUs = Math.Max(lastAudioPts + 1, Math.Max(0, sample.TimestampUs - baseUs));
                    lastAudioPts = ptsUs;
                    var durationUs = ai < audio.Count ? Math.Clamp(audio[ai].TimestampUs - sample.TimestampUs, 1_000, 60_000) : 10_000;
                    WritePacket(fmtCtx, pkt, audioStream!, sample.Data, ptsUs, durationUs, isKeyframe: true, srcTimeBase);
                }
            }

            var trailerErr = ffmpeg.av_write_trailer(fmtCtx);
            if (trailerErr < 0)
                throw new InvalidOperationException($"FFmpeg failed to finalize mp4 trailer: {FfmpegLibrary.ErrorText(trailerErr)}");
        }
        finally
        {
            if (pkt != null)
                ffmpeg.av_packet_free(&pkt);
            if (fmtCtx != null)
            {
                if ((fmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && fmtCtx->pb != null)
                    ffmpeg.avio_closep(&fmtCtx->pb);
                ffmpeg.avformat_free_context(fmtCtx);
            }
        }

        File.Move(tempPath, path, overwrite: true);
        return path;
    }

    private void PruneLocked(long latestTimestampUs)
    {
        var cutoff = latestTimestampUs - _maxDurationUs;
        if (cutoff <= 0)
            return;

        var items = _video.ToArray();
        var latestAnchorIndex = -1;
        var firstFutureKeyIndex = -1;
        for (var i = 0; i < items.Length; i++)
        {
            if (!items[i].IsKeyframe)
                continue;

            if (items[i].TimestampUs <= cutoff)
                latestAnchorIndex = i;
            else if (firstFutureKeyIndex < 0)
                firstFutureKeyIndex = i;
        }

        var dropCount = 0;
        if (latestAnchorIndex > 0)
        {
            dropCount = latestAnchorIndex;
        }
        else if (latestAnchorIndex < 0 && items.Length > 0)
        {
            if (firstFutureKeyIndex > 0 && latestTimestampUs - items[0].TimestampUs > _maxDurationUs + 10_000_000L)
            {
                dropCount = firstFutureKeyIndex;
            }
            else if (firstFutureKeyIndex < 0)
            {
                var hardCutoff = latestTimestampUs - (_maxDurationUs * 2);
                while (dropCount < items.Length - 1 && items[dropCount].TimestampUs < hardCutoff)
                {
                    if (items[dropCount].IsKeyframe)
                        break;
                    dropCount++;
                }
            }
        }

        for (var i = 0; i < dropCount; i++)
        {
            var removed = _video.Dequeue();
            if (removed.Header is not null)
                _latestHeader = removed.Header;
        }

        var audioCutoff = (_video.Count > 0 ? Math.Min(cutoff, _video.Peek().TimestampUs) : cutoff) - 100_000L;
        while (_audio.Count > 0 && _audio.Peek().TimestampUs < audioCutoff)
            _audio.Dequeue();
    }

    private static unsafe void AllocateExtradata(AVCodecParameters* codecpar, byte[] bytes)
    {
        codecpar->extradata = (byte*)ffmpeg.av_mallocz((ulong)(bytes.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        fixed (byte* src = bytes)
            Buffer.MemoryCopy(src, codecpar->extradata, bytes.Length, bytes.Length);
        codecpar->extradata_size = bytes.Length;
    }

    private static byte[] BuildOpusHead(int channels, int sampleRate)
    {
        var head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = (byte)channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), 312);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), (uint)sampleRate);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(16), 0);
        head[18] = 0;
        return head;
    }

    private static bool StartsWithHeader(byte[] data, byte[] header) =>
        data.Length >= header.Length && data.AsSpan(0, header.Length).SequenceEqual(header);

    private static unsafe void WritePacket(
        AVFormatContext* fmtCtx,
        AVPacket* pkt,
        AVStream* stream,
        byte[] data,
        long ptsUs,
        long durationUs,
        bool isKeyframe,
        AVRational srcTimeBase)
    {
        ffmpeg.av_packet_unref(pkt);
        var buf = ffmpeg.av_malloc((ulong)(data.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        Marshal.Copy(data, 0, (nint)buf, data.Length);
        ffmpeg.av_packet_from_data(pkt, (byte*)buf, data.Length);
        pkt->stream_index = stream->index;
        pkt->pts = ptsUs;
        pkt->dts = ptsUs;
        pkt->duration = durationUs;
        pkt->flags = isKeyframe ? ffmpeg.AV_PKT_FLAG_KEY : 0;
        ffmpeg.av_packet_rescale_ts(pkt, srcTimeBase, stream->time_base);

        var err = ffmpeg.av_interleaved_write_frame(fmtCtx, pkt);
        ffmpeg.av_packet_unref(pkt);
        if (err < 0)
            throw new InvalidOperationException($"FFmpeg failed to write frame: {FfmpegLibrary.ErrorText(err)}");
    }

    private sealed record VideoSample(byte[] Data, byte[]? Header, bool IsKeyframe, long TimestampUs);

    private sealed record AudioSample(byte[] Data, long TimestampUs);
}
