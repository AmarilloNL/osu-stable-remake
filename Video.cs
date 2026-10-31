using System.Runtime.InteropServices;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// A beatmap's background video, decoded by Windows' Media Foundation (no extra libraries) on its own thread: the
// source reader converts to 32-bit RGB, the thread keeps the newest frame not past the wanted time and the game
// uploads it to a texture when it changes. COM calls go through vtable slots to avoid declaring the interfaces.
sealed unsafe class Video : IDisposable
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool Failed { get; private set; }

    readonly string path;
    readonly Thread thread;
    readonly object gate = new();
    byte[] pixels = [];
    int version, uploaded;
    volatile bool stop;
    double wanted = double.NegativeInfinity; // video ms
    Texture2D tex;

    public Video(string path)
    {
        this.path = path;
        thread = new Thread(Run) { IsBackground = true, Name = "video" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    // The frame for this video time (ms from the video's start), or null before the first frame has arrived.
    public Texture2D? Frame(double ms)
    {
        Volatile.Write(ref wanted, ms);
        lock (gate)
        {
            if (version == 0) return null;
            if (tex.Id == 0)
            {
                Image img = new() { Width = Width, Height = Height, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 };
                fixed (byte* p = pixels) { img.Data = p; tex = LoadTextureFromImage(img); }
                SetTextureFilter(tex, TextureFilter.Bilinear);
                uploaded = version;
            }
            else if (uploaded != version)
            {
                fixed (byte* p = pixels) UpdateTexture(tex, p);
                uploaded = version;
            }
        }
        return tex;
    }

    public void Dispose()
    {
        stop = true;
        thread.Join(500);
        if (tex.Id != 0) UnloadTexture(tex);
    }

    void Run()
    {
        IntPtr reader = 0, attrs = 0, type = 0, current = 0;
        try
        {
            Check(MFStartup(0x20070, 0));
            Check(MFCreateAttributes(&attrs, 1));
            var enableProcessing = new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int, int>)Slot(attrs, 21))(attrs, &enableProcessing, 1)); // SetUINT32: let the reader convert to RGB
            fixed (char* p = path) Check(MFCreateSourceReaderFromURL(p, attrs, &reader));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Slot(reader, 4))(reader, 0xFFFFFFFE, 0)); // no audio
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Slot(reader, 4))(reader, 0xFFFFFFFC, 1));
            Check(MFCreateMediaType(&type));
            Guid major = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"),
                video = new("73646976-0000-0010-8000-00AA00389B71"), rgb32 = new("00000016-0000-0010-8000-00AA00389B71");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(type, 24))(type, &major, &video)); // SetGUID
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(type, 24))(type, &subtype, &rgb32));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Slot(reader, 7))(reader, 0xFFFFFFFC, 0, type));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(reader, 6))(reader, 0xFFFFFFFC, &current));
            var frameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
            ulong size;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Slot(current, 8))(current, &frameSize, &size)); // GetUINT64
            int w = (int)(size >> 32), h = (int)(size & 0xFFFFFFFF);
            var strideKey = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
            int stride;
            if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int*, int>)Slot(current, 7))(current, &strideKey, &stride) < 0) stride = w * 4;
            var buffer = new byte[w * h * 4];
            lock (gate) { Width = w; Height = h; pixels = new byte[w * h * 4]; }

            IntPtr next = 0;
            long nextTime = 0;
            bool ended = false;
            while (!stop)
            {
                if (next == 0 && !ended)
                {
                    uint actual, flags;
                    long ts;
                    IntPtr sample = 0;
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Slot(reader, 9))(
                        reader, 0xFFFFFFFC, 0, &actual, &flags, &ts, &sample));
                    if ((flags & 2) != 0) ended = true; // end of stream: the last frame stays
                    if (sample != 0) { next = sample; nextTime = ts / 10000; }
                    continue;
                }
                if (next != 0 && nextTime <= Volatile.Read(ref wanted))
                {
                    Publish(next, ref buffer, w, h, stride);
                    Release(next);
                    next = 0;
                    continue;
                }
                Thread.Sleep(2);
            }
            if (next != 0) Release(next);
        }
        catch (Exception e) when (e is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"video not played: {e.Message}");
            Failed = true;
        }
        finally
        {
            if (current != 0) Release(current);
            if (type != 0) Release(type);
            if (reader != 0) Release(reader);
            if (attrs != 0) Release(attrs);
        }
    }

    // Copies a decoded sample (BGRX rows, bottom-up when the stride is negative) into the shared RGBA frame.
    void Publish(IntPtr sample, ref byte[] buffer, int w, int h, int stride)
    {
        IntPtr media;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(sample, 41))(sample, &media)); // ConvertToContiguousBuffer
        try
        {
            byte* data; uint max, len;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Slot(media, 3))(media, &data, &max, &len)); // Lock
            int rowBytes = w * 4, pitch = Math.Abs(stride);
            fixed (byte* dst = buffer)
                for (int y = 0; y < h; y++)
                {
                    uint* s = (uint*)(data + (stride < 0 ? (h - 1 - y) : y) * pitch), d = (uint*)(dst + y * rowBytes);
                    if ((long)(y + 1) * pitch > len) break;
                    for (int x = 0; x < w; x++)
                    {
                        uint px = s[x]; // memory B G R X -> R G B A
                        d[x] = 0xFF000000u | (px & 0xFF) << 16 | (px & 0xFF00) | (px >> 16 & 0xFF);
                    }
                }
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(media, 4))(media); // Unlock
        }
        finally { Release(media); }
        lock (gate)
        {
            (pixels, buffer) = (buffer, pixels); // swap: the game uploads the new one, the old is filled next
            version++;
        }
    }

    static IntPtr Slot(IntPtr com, int index) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), index * IntPtr.Size);
    static void Release(IntPtr com) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, 2))(com);
    static void Check(int hr) { if (hr < 0) throw new COMException("Media Foundation error", hr); }

    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] static extern int MFCreateAttributes(IntPtr* attributes, uint size);
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(IntPtr* type);
    [DllImport("mfreadwrite.dll")] static extern int MFCreateSourceReaderFromURL(char* url, IntPtr attributes, IntPtr* reader);
}
