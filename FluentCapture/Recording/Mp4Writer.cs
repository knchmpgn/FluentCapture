using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace FluentSnip.Recording;

public sealed class RecordedFrame { public byte[] Data; public TimeSpan Time; }

/// <summary>H.264 / MP4 encoding through Media Foundation (MediaStreamSource -> MediaTranscoder).</summary>
public static class Mp4Writer
{
    public static async Task WriteAsync(string path, int w, int h, int fps, uint bitrate,
        ChannelReader<RecordedFrame> reader, Action<byte[]> recycle, ChannelReader<RecordedAudio> audio = null)
    {
        var props = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Nv12, (uint)w, (uint)h);
        props.FrameRate.Numerator = (uint)fps; props.FrameRate.Denominator = 1;
        var vdesc = new VideoStreamDescriptor(props);
        var mss = audio == null
            ? new MediaStreamSource(vdesc)
            : new MediaStreamSource(vdesc, new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm((uint)AudioCapture.Rate, (uint)AudioCapture.Channels, 16)));
        mss.BufferTime = TimeSpan.Zero;
        mss.Starting += (s, a) => { if (a.Request.StartPosition != null) a.Request.SetActualStartPosition(a.Request.StartPosition.Value); };
        mss.SampleRequested += async (s, a) =>
        {
            var deferral = a.Request.GetDeferral();
            try
            {
                if (audio != null && a.Request.StreamDescriptor is AudioStreamDescriptor)
                {
                    if (await audio.WaitToReadAsync().ConfigureAwait(false) && audio.TryRead(out var chunk))
                    {
                        var asmp = MediaStreamSample.CreateFromBuffer(chunk.Data.AsBuffer(), chunk.Time);
                        asmp.Duration = TimeSpan.FromSeconds(AudioCapture.ChunkFrames / (double)AudioCapture.Rate);
                        a.Request.Sample = asmp;
                    }
                    else a.Request.Sample = null;
                }
                else if (await reader.WaitToReadAsync().ConfigureAwait(false) && reader.TryRead(out var f))
                {
                    var nv = ToNv12(f.Data, w, h);
                    recycle(f.Data);
                    var sample = MediaStreamSample.CreateFromBuffer(nv.AsBuffer(), f.Time);
                    sample.Duration = TimeSpan.FromSeconds(1.0 / fps);
                    a.Request.Sample = sample;
                }
                else a.Request.Sample = null;
            }
            finally { deferral.Complete(); }
        };

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = (uint)w; profile.Video.Height = (uint)h; profile.Video.Bitrate = bitrate;
        profile.Video.FrameRate.Numerator = (uint)fps; profile.Video.FrameRate.Denominator = 1;
        profile.Audio = audio == null ? null : AudioEncodingProperties.CreateAac((uint)AudioCapture.Rate, (uint)AudioCapture.Channels, 192000);

        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var folder = await StorageFolder.GetFolderFromPathAsync(dir);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(mss, stream, profile);
        if (!prep.CanTranscode)
        {   // some GPUs/drivers refuse hardware encoding of this source: retry in software
            transcoder = new MediaTranscoder { HardwareAccelerationEnabled = false };
            prep = await transcoder.PrepareMediaStreamSourceTranscodeAsync(mss, stream, profile);
        }
        if (!prep.CanTranscode) throw new InvalidOperationException("MP4 encoding is not available: " + prep.FailureReason);
        await prep.TranscodeAsync();
    }

    /// <summary>BGRA -> NV12 (BT.601, limited range), 2x2 chroma averaging.</summary>
    static byte[] ToNv12(byte[] bgra, int w, int h)
    {
        int ySize = w * h; var buf = new byte[ySize + ySize / 2];
        Parallel.For(0, h, row =>
        {
            int si = row * w * 4, di = row * w;
            for (int x = 0; x < w; x++, si += 4)
                buf[di + x] = (byte)(((66 * bgra[si + 2] + 129 * bgra[si + 1] + 25 * bgra[si] + 128) >> 8) + 16);
        });
        Parallel.For(0, h / 2, row =>
        {
            int di = ySize + row * w;
            for (int x = 0; x < w / 2; x++)
            {
                int o = ((row * 2) * w + x * 2) * 4, o2 = o + w * 4;
                int b = (bgra[o] + bgra[o + 4] + bgra[o2] + bgra[o2 + 4]) >> 2;
                int g = (bgra[o + 1] + bgra[o + 5] + bgra[o2 + 1] + bgra[o2 + 5]) >> 2;
                int r = (bgra[o + 2] + bgra[o + 6] + bgra[o2 + 2] + bgra[o2 + 6]) >> 2;
                buf[di + x * 2] = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                buf[di + x * 2 + 1] = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
            }
        });
        return buf;
    }
}
