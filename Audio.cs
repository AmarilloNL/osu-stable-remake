using System.Runtime.InteropServices;

namespace OsuStableRemake;

// Minimal BASS 2.4 bindings (the same audio library stable uses), x64 build in lib/.
static class Bass
{
    const string Dll = "bass";
    public const uint Unicode = 0x80000000, StreamPrescan = 0x20000, SampleOverPos = 0x20000;
    public const uint AttribVol = 2, PosByte = 0;
    public const uint ConfigBuffer = 0, ConfigUpdatePeriod = 1, ConfigDevBuffer = 27, ConfigDevPeriod = 53;
    public const uint ActiveStopped = 0, ActivePlaying = 1, ActivePaused = 3;

    [DllImport(Dll)] public static extern bool BASS_SetConfig(uint option, uint value);
    [DllImport(Dll)] public static extern bool BASS_Init(int device, uint freq, uint flags, IntPtr win, IntPtr guid);
    [DllImport(Dll)] public static extern bool BASS_Free();
    [DllImport(Dll)] public static extern int BASS_ErrorGetCode();
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int BASS_SampleLoad(uint filetype, string file, ulong offset, uint length, uint max, uint flags);
    [DllImport(Dll)] public static extern int BASS_SampleLoad(uint filetype, byte[] mem, ulong offset, uint length, uint max, uint flags); // filetype 1 = memory
    [DllImport(Dll)] public static extern bool BASS_SampleFree(int handle);
    [DllImport(Dll)] public static extern int BASS_SampleGetChannel(int handle, uint flags);
    [DllImport(Dll, CharSet = CharSet.Unicode)] public static extern int BASS_StreamCreateFile(uint filetype, string file, ulong offset, ulong length, uint flags);
    [DllImport(Dll)] public static extern bool BASS_StreamFree(int handle);
    [DllImport(Dll)] public static extern bool BASS_ChannelPlay(int handle, bool restart);
    [DllImport(Dll)] public static extern bool BASS_ChannelPause(int handle);
    [DllImport(Dll)] public static extern bool BASS_ChannelStop(int handle);
    [DllImport(Dll)] public static extern uint BASS_ChannelIsActive(int handle);
    [DllImport(Dll)] public static extern ulong BASS_ChannelGetPosition(int handle, uint mode);
    [DllImport(Dll)] public static extern ulong BASS_ChannelGetLength(int handle, uint mode);
    [DllImport(Dll)] public static extern bool BASS_ChannelSetPosition(int handle, ulong pos, uint mode);
    [DllImport(Dll)] public static extern double BASS_ChannelBytes2Seconds(int handle, ulong pos);
    [DllImport(Dll)] public static extern ulong BASS_ChannelSeconds2Bytes(int handle, double pos);
    [DllImport(Dll)] public static extern bool BASS_ChannelSetAttribute(int handle, uint attrib, float value);
    [DllImport(Dll)] public static extern int BASS_ChannelGetData(int handle, float[] buffer, uint length);
    [DllImport(Dll)] public static extern uint BASS_ChannelGetLevel(int handle);
    [DllImport(Dll)] public static extern bool BASS_ChannelSlideAttribute(int handle, uint attrib, float value, uint timeMs);
    [DllImport(Dll)] public static extern bool BASS_ChannelGetAttribute(int handle, uint attrib, ref float value);
    [DllImport(Dll)] public static extern uint BASS_ChannelFlags(int handle, uint flags, uint mask);
    public const uint AttribFreq = 1, SampleLoop = 4;
    public const uint DataFft2048 = 0x80000003;

    // bass_fx: tempo change without pitch change (DT/HT), as stable does.
    public const uint StreamDecode = 0x200000, FxFreeSource = 0x10000, AttribTempo = 0x10000, AttribTempoPitch = 0x10001;
    [DllImport("bass_fx")] public static extern int BASS_FX_TempoCreate(int chan, uint flags);

    public static void Init()
    {
        // Small device buffers = low output latency. Must be set before BASS_Init.
        // AudioDeviceBuffer in remake.cfg can raise it if a device crackles.
        BASS_SetConfig(ConfigDevPeriod, 2);
        BASS_SetConfig(ConfigDevBuffer, (uint)Config.GetInt("AudioDeviceBuffer", 10));
        BASS_SetConfig(ConfigUpdatePeriod, 5);
        BASS_SetConfig(ConfigBuffer, 100);
        if (!BASS_Init(-1, 44100, 0, IntPtr.Zero, IntPtr.Zero))
            Console.Error.WriteLine($"BASS_Init failed: {BASS_ErrorGetCode()}");
    }
}

sealed class Track : IDisposable
{
    readonly int handle;
    Track(int handle) => this.handle = handle;

    // tempo: always go through BASS_FX so the rate can change later (replay speed).
    public static Track? Load(string path, double rate = 1, bool nightcore = false, bool tempo = false)
    {
        if (rate == 1 && !tempo)
        {
            int h = Bass.BASS_StreamCreateFile(0, path, 0, 0, Bass.StreamPrescan | Bass.Unicode);
            return h == 0 ? null : new Track(h);
        }
        int decode = Bass.BASS_StreamCreateFile(0, path, 0, 0, Bass.StreamPrescan | Bass.StreamDecode | Bass.Unicode);
        if (decode == 0) return null;
        int stream = Bass.BASS_FX_TempoCreate(decode, Bass.FxFreeSource);
        if (stream == 0) { Bass.BASS_StreamFree(decode); return null; }
        var track = new Track(stream);
        track.SetTempo(rate, nightcore);
        return track;
    }

    // Needs a track loaded through BASS_FX (rate != 1 or tempo: true).
    public void SetTempo(double rate, bool nightcore)
    {
        Bass.BASS_ChannelSetAttribute(handle, Bass.AttribTempo, (float)((rate - 1) * 100));
        if (nightcore) Bass.BASS_ChannelSetAttribute(handle, Bass.AttribTempoPitch, (float)(12 * Math.Log2(rate)));
    }

    public float Volume { set => Bass.BASS_ChannelSetAttribute(handle, Bass.AttribVol, value); }
    public void Play() => Bass.BASS_ChannelPlay(handle, false);
    public void Pause() => Bass.BASS_ChannelPause(handle);
    public bool Stopped => Bass.BASS_ChannelIsActive(handle) == Bass.ActiveStopped;
    public bool Paused => Bass.BASS_ChannelIsActive(handle) == Bass.ActivePaused;
    public double LengthMs => Bass.BASS_ChannelBytes2Seconds(handle, Bass.BASS_ChannelGetLength(handle, Bass.PosByte)) * 1000;
    public void Fft(float[] bins1024) => Bass.BASS_ChannelGetData(handle, bins1024, Bass.DataFft2048);

    // Playback sample rate; lowering it slows and pitches the song down together (stable's fail).
    public float Frequency
    {
        get { float f = 0; Bass.BASS_ChannelGetAttribute(handle, Bass.AttribFreq, ref f); return f; }
        set => Bass.BASS_ChannelSetAttribute(handle, Bass.AttribFreq, value);
    }
    // Peak levels 0..32768 (left, right) over the last 20ms.
    public (int L, int R) Level { get { uint v = Bass.BASS_ChannelGetLevel(handle); return (v == uint.MaxValue ? 0 : (int)(v & 0xFFFF), v == uint.MaxValue ? 0 : (int)(v >> 16)); } }

    // BASS reports the audible position (device latency already accounted for).
    public double PositionMs => Bass.BASS_ChannelBytes2Seconds(handle, Bass.BASS_ChannelGetPosition(handle, Bass.PosByte)) * 1000;
    public void Seek(double ms) => Bass.BASS_ChannelSetPosition(handle, Bass.BASS_ChannelSeconds2Bytes(handle, Math.Max(0, ms) / 1000), Bass.PosByte);

    public void Dispose()
    {
        Bass.BASS_ChannelStop(handle);
        Bass.BASS_StreamFree(handle);
    }
}

// A hitsound: one BASS sample with up to 8 overlapping voices.
sealed class SoundPool(int sample)
{
    public void Play(float volume, float pitch = 1)
    {
        if (sample == 0) return;
        int ch = Bass.BASS_SampleGetChannel(sample, 0);
        if (ch == 0) return;
        Bass.BASS_ChannelSetAttribute(ch, Bass.AttribVol, volume);
        if (pitch != 1) { float f = 0; Bass.BASS_ChannelGetAttribute(ch, Bass.AttribFreq, ref f); Bass.BASS_ChannelSetAttribute(ch, Bass.AttribFreq, f * pitch); }
        Bass.BASS_ChannelPlay(ch, false);
    }

    public void Unload()
    {
        if (sample != 0) Bass.BASS_SampleFree(sample);
    }

    // A looping voice (pause music); stop it with Stop.
    public int PlayLooped(float volume)
    {
        if (sample == 0) return 0;
        int ch = Bass.BASS_SampleGetChannel(sample, 0);
        if (ch == 0) return 0;
        Bass.BASS_ChannelFlags(ch, Bass.SampleLoop, Bass.SampleLoop);
        Bass.BASS_ChannelSetAttribute(ch, Bass.AttribVol, volume);
        Bass.BASS_ChannelPlay(ch, false);
        return ch;
    }

    public static void Stop(int channel) { if (channel != 0) Bass.BASS_ChannelStop(channel); }

    public static SoundPool Load(byte[] data) => new(Bass.BASS_SampleLoad(1, data, 0, (uint)data.Length, 8, Bass.SampleOverPos));

    // A blank/invalid file is a deliberate mute in osu!, so it still counts as found (silent).
    public static SoundPool? Load(string path) =>
        File.Exists(path) ? new SoundPool(Bass.BASS_SampleLoad(0, path, 0, 0, 8, Bass.SampleOverPos | Bass.Unicode)) : null;
}
