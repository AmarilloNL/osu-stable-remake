using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;

namespace StableRemake;

// stable's default skin, UI images, fonts and sounds live as resources inside osu!ui.dll (and osu!gameplay.dll). They're read
// from the user's own install at runtime (never copied) and used for anything the user's skin lacks.
static class DefaultSkin
{
    static Dictionary<string, byte[]>? files;

    public static byte[]? Get(string name)
    {
        files ??= LoadAll();
        return files.TryGetValue(name, out var b) ? b : null;
    }

    // osu!ui.dll first; osu!gameplay.dll adds gameplay-only resources (e.g. the flashlight mask).
    static Dictionary<string, byte[]> LoadAll()
    {
        var all = Load(Path.Combine(Config.OsuDir, "osu!ui.dll"));
        foreach (var (name, bytes) in Load(Path.Combine(Config.OsuDir, "osu!gameplay.dll"))) all.TryAdd(name, bytes);
        return all;
    }

    static Dictionary<string, byte[]> Load(string dll)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(dll)) return result;
        try
        {
            using var pe = new PEReader(File.OpenRead(dll));
            var md = pe.GetMetadataReader();
            var dir = pe.PEHeaders.CorHeader!.ResourcesDirectory;
            var section = pe.GetSectionData(dir.RelativeVirtualAddress).GetContent().ToArray();
            foreach (var h in md.ManifestResources)
            {
                var res = md.GetManifestResource(h);
                if (!res.Implementation.IsNil) continue;
                int offset = (int)res.Offset;
                int length = BitConverter.ToInt32(section.AsSpan(offset, 4));
                try
                {
                    using var stream = new MemoryStream(section, offset + 4, length, false);
                    using var reader = new ResourceReader(stream);
                    var names = new List<string>();
                    var e = reader.GetEnumerator();
                    while (e.MoveNext()) names.Add((string)e.Key); // reading Key doesn't deserialize the value
                    foreach (var name in names)
                    {
                        reader.GetResourceData(name, out var type, out var data);
                        if (Payload(type, data) is { } bytes) result[name] = bytes;
                    }
                }
                catch (ArgumentException)
                {
                    // Not a .resources table: a plain embedded file, kept under its manifest name.
                    result[md.GetString(res.Name)] = section.AsSpan(offset + 4, length).ToArray();
                }
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"{Path.GetFileName(dll)} resources unavailable: {ex.Message}");
        }
        return result;
    }

    static readonly byte[] PngSig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    static readonly byte[] JpegStart = [0xFF, 0xD8, 0xFF], JpegEnd = [0xFF, 0xD9];

    // byte[] and Stream resources are stored as length + raw bytes. Bitmaps are BinaryFormatter blobs (which .NET
    // no longer deserializes), but they carry the original PNG/JPEG file verbatim, so it's cut out directly.
    static byte[]? Payload(string type, byte[] data)
    {
        if (type.StartsWith("ResourceTypeCode.ByteArray") || type.StartsWith("ResourceTypeCode.Stream"))
            return data.AsSpan(4, BitConverter.ToInt32(data, 0)).ToArray();
        int start = data.AsSpan().IndexOf(PngSig);
        if (start < 0)
        {
            int jpeg = data.AsSpan().IndexOf(JpegStart), end = data.AsSpan().LastIndexOf(JpegEnd);
            return jpeg >= 0 && end > jpeg ? data.AsSpan(jpeg, end + 2 - jpeg).ToArray() : null;
        }
        int pos = start + 8;
        while (pos + 12 <= data.Length)
        {
            int len = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            bool end = data[pos + 4] == 'I' && data[pos + 5] == 'E' && data[pos + 6] == 'N' && data[pos + 7] == 'D';
            pos += 12 + len;
            if (end) return data.AsSpan(start, pos - start).ToArray();
        }
        return null;
    }

    public static IEnumerable<string> Names => (files ??= LoadAll()).Keys;
}
