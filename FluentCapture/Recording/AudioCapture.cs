using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FluentSnip.Recording;

public sealed class RecordedAudio { public byte[] Data; public TimeSpan Time; }

/// <summary>
/// Captures computer audio (WASAPI loopback) and/or the default microphone, resamples both to 48 kHz stereo,
/// mixes them and hands out fixed 20 ms PCM16 chunks on demand (silence when nothing is playing).
/// </summary>
public sealed class AudioCapture : IDisposable
{
    public const int Rate = 48000, Channels = 2, ChunkFrames = 960;
    readonly MixingSampleProvider mixer;
    readonly float[] fbuf = new float[ChunkFrames * Channels];
    WasapiCapture loop, mic;
    BufferedWaveProvider loopBuf, micBuf;

    public AudioCapture(bool system, bool microphone)
    {
        mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels)) { ReadFully = true };
        if (system)
        {
            loop = new WasapiLoopbackCapture();
            loopBuf = Buffer(loop);
            mixer.AddMixerInput(Normalize(loopBuf.ToSampleProvider()));
        }
        if (microphone)
        {
            mic = new WasapiCapture();
            micBuf = Buffer(mic);
            mixer.AddMixerInput(Normalize(micBuf.ToSampleProvider()));
        }
    }

    static BufferedWaveProvider Buffer(WasapiCapture c)
    {
        var b = new BufferedWaveProvider(c.WaveFormat) { ReadFully = true, DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(2) };
        c.DataAvailable += (s, e) => b.AddSamples(e.Buffer, 0, e.BytesRecorded);
        return b;
    }

    static ISampleProvider Normalize(ISampleProvider sp)
    {
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2)
        {
            var m = new MultiplexingSampleProvider(new[] { sp }, 2);
            m.ConnectInputToOutput(0, 0); m.ConnectInputToOutput(1, 1);
            sp = m;
        }
        if (sp.WaveFormat.SampleRate != Rate) sp = new WdlResamplingSampleProvider(sp, Rate);
        return sp;
    }

    public void Start() { loop?.StartRecording(); mic?.StartRecording(); }

    /// <summary>Drops whatever the devices buffered (used around pause / resume).</summary>
    public void Flush() { loopBuf?.ClearBuffer(); micBuf?.ClearBuffer(); }

    public byte[] ReadChunk()
    {
        int n = mixer.Read(fbuf, 0, fbuf.Length);
        if (n < fbuf.Length) Array.Clear(fbuf, n, fbuf.Length - n);
        var o = new byte[fbuf.Length * 2];
        for (int i = 0; i < fbuf.Length; i++)
        {
            short v = (short)Math.Clamp((int)(fbuf[i] * 32767f), -32768, 32767);
            o[i * 2] = (byte)v; o[i * 2 + 1] = (byte)(v >> 8);
        }
        return o;
    }

    public void Dispose()
    {
        try { loop?.StopRecording(); } catch { }
        try { mic?.StopRecording(); } catch { }
        loop?.Dispose(); mic?.Dispose();
    }
}
