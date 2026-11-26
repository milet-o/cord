using FFmpeg.AutoGen;

namespace Cord.Media.Tests;

internal static class FfmpegTestEnvironment
{
    private static readonly Lazy<bool> Loaded = new(TryLoad);

    public static bool Available => Loaded.Value;

    private static bool TryLoad()
    {
        try
        {
            FfmpegLibrary.Load();
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or NotSupportedException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static unsafe int Parse(AVCodecParserContext* parser, AVCodecContext* context, byte* data, int size, List<byte[]> units)
    {
        byte* output;
        int outputSize;
        var consumed = ffmpeg.av_parser_parse2(parser, context, &output, &outputSize, data, size, ffmpeg.AV_NOPTS_VALUE, ffmpeg.AV_NOPTS_VALUE, 0);
        if (outputSize > 0)
            units.Add(new ReadOnlySpan<byte>(output, outputSize).ToArray());
        return size == 0 ? outputSize : consumed;
    }

    /// <summary>Splits an Annex B elementary stream into access units with FFmpeg's parser.</summary>
    public static unsafe List<byte[]> SplitAccessUnits(byte[] stream, VideoCodecKind codec)
    {
        var parser = ffmpeg.av_parser_init(codec == VideoCodecKind.Hevc ? AVCodecID.AV_CODEC_ID_HEVC : AVCodecID.AV_CODEC_ID_H264);
        var context = ffmpeg.avcodec_alloc_context3(null);
        var units = new List<byte[]>();
        try
        {
            fixed (byte* start = stream)
            {
                var remaining = stream.Length;
                var position = start;
                while (remaining > 0)
                {
                    var consumed = Parse(parser, context, position, remaining, units);
                    position += consumed;
                    remaining -= consumed;
                }

                while (Parse(parser, context, null, 0, units) > 0)
                {
                }
            }
        }
        finally
        {
            ffmpeg.av_parser_close(parser);
            ffmpeg.avcodec_free_context(&context);
        }

        return units;
    }
}

public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (!FfmpegTestEnvironment.Available)
            Skip = "FFmpeg shared libraries not found (set CORD_FFMPEG_PATH).";
    }
}

public sealed class FfmpegTheoryAttribute : TheoryAttribute
{
    public FfmpegTheoryAttribute()
    {
        if (!FfmpegTestEnvironment.Available)
            Skip = "FFmpeg shared libraries not found (set CORD_FFMPEG_PATH).";
    }
}
