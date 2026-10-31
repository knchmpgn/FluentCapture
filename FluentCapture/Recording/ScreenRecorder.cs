using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using FluentSnip.Native;

namespace FluentSnip.Recording;

/// <summary>Captures a screen region with GDI at a fixed rate and feeds a GIF or MP4 encoder.</summary>
public sealed class ScreenRecorder
{
    readonly int x, y, w, h, fps;
    readonly bool cursor, gif, loop;
    readonly uint bitrate;
    readonly string path;
    readonly Channel<RecordedFrame> channel = Channel.CreateBounded<RecordedFrame>(new BoundedChannelOptions(24) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    readonly Stopwatch active = new();
    readonly bool systemAudio, micAudio;
    AudioCapture audio; Channel<RecordedAudio> audioCh; Thread thread, audioThread; Task encoder;
    volatile bool stopping, paused;

    public TimeSpan Elapsed => active.Elapsed;
    public bool IsPaused => paused;
    public string Path => path;

    public ScreenRecorder(Rect region, int fps, bool cursor, bool gif, bool loop, string quality, string path, bool systemAudio = false, bool micAudio = false)
    {
        this.systemAudio = systemAudio && !gif; this.micAudio = micAudio && !gif;
        x = (int)region.X; y = (int)region.Y;
        w = Math.Max(2, (int)region.Width & ~1); h = Math.Max(2, (int)region.Height & ~1);   // H.264 needs even sizes
        this.fps = Math.Clamp(fps, 1, 60); this.cursor = cursor; this.gif = gif; this.loop = loop; this.path = path;
        double bpp = quality switch { "Low" => 0.05, "Medium" => 0.09, _ => 0.16 };
        bitrate = (uint)Math.Clamp(w * h * this.fps * bpp, 800_000, 60_000_000);
    }

    public void Start()
    {
        if (systemAudio || micAudio)
        {
            audio = new AudioCapture(systemAudio, micAudio);     // throws if no device / access denied: nothing else started yet
            audio.Start();
            audioCh = Channel.CreateUnbounded<RecordedAudio>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        }
        NativeMethods.timeBeginPeriod(1);
        encoder = Task.Run(Encode);
        active.Start();
        if (audio != null) { audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "FluentSnip audio" }; audioThread.Start(); }
        thread = new Thread(CaptureLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "FluentSnip recorder" };
        thread.Start();
    }

    public void Pause() { paused = true; active.Stop(); audio?.Flush(); }
    public void Resume() { audio?.Flush(); active.Start(); paused = false; }

    public async Task StopAsync()
    {
        stopping = true; paused = false;
        thread?.Join(2000);
        audioThread?.Join(2000);
        active.Stop();
        try { await encoder; }
        finally { audio?.Dispose(); }
        NativeMethods.timeEndPeriod(1);
    }

    void CaptureLoop()
    {
        IntPtr sdc = NativeMethods.GetDC(IntPtr.Zero), mdc = NativeMethods.CreateCompatibleDC(sdc);
        var bmi = NativeMethods.MakeBmi(w, h);
        IntPtr hbm = NativeMethods.CreateDIBSection(sdc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = NativeMethods.SelectObject(mdc, hbm);
        int size = w * h * 4;
        long interval = Stopwatch.Frequency / fps, next = 0;
        try
        {
            while (!stopping)
            {
                if (paused) { Thread.Sleep(15); continue; }
                long nowTicks = active.ElapsedTicks;
                if (nowTicks < next)
                {
                    double ms = (next - nowTicks) * 1000.0 / Stopwatch.Frequency;
                    if (ms > 2) Thread.Sleep((int)ms - 1); else Thread.SpinWait(200);
                    continue;
                }
                var t = active.Elapsed;
                NativeMethods.BitBlt(mdc, 0, 0, w, h, sdc, x, y, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
                if (cursor) DrawCursor(mdc);
                var data = ArrayPool<byte>.Shared.Rent(size);
                Marshal.Copy(bits, data, 0, size);
                if (!channel.Writer.TryWrite(new RecordedFrame { Data = data, Time = t }))
                    channel.Writer.WriteAsync(new RecordedFrame { Data = data, Time = t }).AsTask().Wait();
                next += interval;
                if (active.ElapsedTicks - next > interval * 3) next = active.ElapsedTicks;   // fell behind: resync
            }
        }
        catch (Exception ex) { Core.SettingsService.Log("Capture loop: " + ex); }
        finally
        {
            channel.Writer.TryComplete();
            NativeMethods.SelectObject(mdc, old); NativeMethods.DeleteObject(hbm); NativeMethods.DeleteDC(mdc); NativeMethods.ReleaseDC(IntPtr.Zero, sdc);
        }
    }

    void AudioLoop()
    {
        long sent = 0;
        try
        {
            while (!stopping)
            {
                double due = active.Elapsed.TotalSeconds * AudioCapture.Rate - sent;
                if (!paused && due >= AudioCapture.ChunkFrames)
                {
                    var data = audio.ReadChunk();
                    audioCh.Writer.TryWrite(new RecordedAudio { Data = data, Time = TimeSpan.FromSeconds(sent / (double)AudioCapture.Rate) });
                    sent += AudioCapture.ChunkFrames;
                }
                else Thread.Sleep(4);
            }
        }
        catch (Exception ex) { Core.SettingsService.Log("Audio loop: " + ex); }
        finally { audioCh.Writer.TryComplete(); }
    }

    void DrawCursor(IntPtr mdc)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!NativeMethods.GetCursorInfo(ref ci) || (ci.flags & 1) == 0) return;
        if (!NativeMethods.GetIconInfo(ci.hCursor, out var ii)) return;
        if (ii.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmColor);
        if (ii.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmMask);
        NativeMethods.DrawIconEx(mdc, ci.pt.X - x - ii.xHotspot, ci.pt.Y - y - ii.yHotspot, ci.hCursor, 0, 0, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
    }

    static void Recycle(byte[] b) { try { ArrayPool<byte>.Shared.Return(b); } catch { } }

    async Task Encode()
    {
        if (gif)
        {
            using var gw = new GifWriter(path, w, h, loop);
            RecordedFrame prev = null;
            var buf = new byte[w * h * 4];
            await foreach (var f in channel.Reader.ReadAllAsync())
            {
                if (prev != null) { gw.AddFrame(prev.Data, f.Time - prev.Time); Recycle(prev.Data); }
                prev = f;
            }
            if (prev != null) { gw.AddFrame(prev.Data, TimeSpan.FromSeconds(1.0 / fps)); Recycle(prev.Data); }
        }
        else
        {
            await Mp4Writer.WriteAsync(path, w, h, fps, bitrate, channel.Reader, Recycle, audioCh?.Reader);
        }
    }
}
