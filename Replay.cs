using System.IO.Compression;
using System.Security.Cryptography;

namespace OsuStableRemake;

// Keys: bit 0/1 = the two osu! keys, bit 2/3 = left/right mouse button. Time is map time in ms.
readonly record struct ReplayFrame(double Time, float X, float Y, byte Keys);

sealed class ReplayInfo
{
    public string Path = "", BeatmapMd5 = "", Artist = "", Title = "", Version = "", Player = "";
    public bool FromStable; // a stable .osr (stats are stable's own judgement)
    public Mods Mods;
    public long Score;
    public int MaxCombo, N300, N100, N50, NMiss, NGeki, NKatu;
    public int Mode; // 0 osu!, 1 taiko, 2 catch, 3 mania
    public int Seed; // the play's random seed (mania's Random mod)
    public int FormatVersion = 9; // an own replay's format (6: catch frames on every step that judged something; 7-9: see Replays.Version)
    public bool Perfect; // full combo (the .osr flag)
    public double Accuracy;
    public DateTime Date;
}

// Own replay format: small header (for the leaderboard) + deflate-compressed input frames.
static class Replays
{
    const int Magic = 0x50525253; // "SRRP"
    const int Version = 9; // 3: varint µs time deltas + zigzag varint cursor deltas on a 1/64 px grid; 4: + mode, geki, katu; 5: + seed;
    // 6: catch plays record a frame on every step that judged something (judged at that frame's spot on playback)
    // 7: same frames, judged with stable's mania hold ticks, late misses and last press, and Score V2's own judging in
    // taiko, catch and mania (older replays play back with the judging they were made with)
    // 8: an osu! play is judged a whole millisecond at a time with each frame's input coming in at its own time, and its
    // replay drains health up to each frame before that frame's presses (older replays keep draining a step at a time)
    // 9: catch plays on stable's exact fruits (tiny droplets, bananas and HardRock's nudges a pixel off before) and its
    // rules for missed droplets and combo ends (older replays keep the fruits and rules they were made with)
    // Frames of the other modes follow stable's: taiko's keys as its bits (M1 left centre, M2 left rim, K1 right
    // centre, K2 right rim), catch's x the catcher's (keys 1: dashing), mania's x the held columns' bits.
    public const float PosScale = 64;

    static long ZigZag(long v) => (v << 1) ^ (v >> 63);
    static long UnZigZag(long v) => (long)((ulong)v >> 1) ^ -(v & 1);
    public static string Dir => System.IO.Path.Combine(AppContext.BaseDirectory, "Replays");

    public static string Md5Of(string file) => Convert.ToHexStringLower(MD5.HashData(File.ReadAllBytes(file)));

    // subfolder: failed plays go to Replays\Failed, out of the leaderboard scan (stable keeps them off it too).
    public static ReplayInfo Save(ReplayInfo info, List<ReplayFrame> frames, string subfolder = "")
    {
        string dir = System.IO.Path.Combine(Dir, subfolder);
        Directory.CreateDirectory(dir);
        string safe = string.Concat($"{info.Artist} - {info.Title} [{info.Version}]".Split(System.IO.Path.GetInvalidFileNameChars()));
        if (safe.Length > 120) safe = safe[..120];
        info.Path = System.IO.Path.Combine(dir, $"{safe} {info.Date:yyyy-MM-dd HH-mm-ss}.srr");
        using var w = new BinaryWriter(File.Create(info.Path));
        w.Write(Magic); w.Write(Version);
        w.Write(info.BeatmapMd5); w.Write(info.Artist); w.Write(info.Title); w.Write(info.Version);
        w.Write((int)info.Mods); w.Write(info.Score); w.Write(info.MaxCombo);
        w.Write(info.N300); w.Write(info.N100); w.Write(info.N50); w.Write(info.NMiss);
        w.Write(info.Accuracy); w.Write(info.Date.ToBinary());
        w.Write(info.Mode); w.Write(info.NGeki); w.Write(info.NKatu); w.Write(info.Seed);
        w.Write(frames.Count);
        using var z = new BinaryWriter(new DeflateStream(w.BaseStream, CompressionLevel.SmallestSize, leaveOpen: true));
        long prev = 0;
        int px = 0, py = 0;
        foreach (var f in frames)
        {
            long us = (long)Math.Round(f.Time * 1000); // gameplay time is already whole µs
            int x = (int)MathF.Round(f.X * PosScale), y = (int)MathF.Round(f.Y * PosScale); // cursor is already on the grid
            z.Write7BitEncodedInt64(us - prev);
            z.Write7BitEncodedInt64(ZigZag(x - px));
            z.Write7BitEncodedInt64(ZigZag(y - py));
            z.Write(f.Keys);
            prev = us; px = x; py = y;
        }
        return info;
    }

    static ReplayInfo ReadHeader(BinaryReader r, string path)
    {
        int version;
        if (r.ReadInt32() != Magic || (version = r.ReadInt32()) is not (3 or 4 or 5 or 6 or 7 or 8 or Version)) throw new InvalidDataException($"not a replay: {path}");
        var info = new ReplayInfo
        {
            Path = path, BeatmapMd5 = r.ReadString(), Artist = r.ReadString(), Title = r.ReadString(), Version = r.ReadString(),
            Mods = (Mods)r.ReadInt32(), Score = r.ReadInt64(), MaxCombo = r.ReadInt32(),
            N300 = r.ReadInt32(), N100 = r.ReadInt32(), N50 = r.ReadInt32(), NMiss = r.ReadInt32(),
            Accuracy = r.ReadDouble(), Date = DateTime.FromBinary(r.ReadInt64()),
        };
        if (version >= 4) { info.Mode = r.ReadInt32(); info.NGeki = r.ReadInt32(); info.NKatu = r.ReadInt32(); }
        if (version >= 5) info.Seed = r.ReadInt32();
        info.FormatVersion = version;
        return info;
    }

    public static ReplayInfo ReadInfo(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        return ReadHeader(r, path);
    }

    public static ReplayFrame[] ReadFrames(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        ReadHeader(r, path);
        var frames = new ReplayFrame[r.ReadInt32()];
        using var z = new BinaryReader(new DeflateStream(r.BaseStream, CompressionMode.Decompress));
        long us = 0, x = 0, y = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            us += z.Read7BitEncodedInt64();
            x += UnZigZag(z.Read7BitEncodedInt64());
            y += UnZigZag(z.Read7BitEncodedInt64());
            frames[i] = new ReplayFrame(us / 1000.0, x / PosScale, y / PosScale, z.ReadByte());
        }
        return frames;
    }

    // Stable's .osr: header (stats as recorded by stable) + LZMA-compressed "w|x|y|keys," frames.
    static (ReplayInfo Info, byte Mode, byte[] Data) ReadOsr(string path, bool withData)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        byte mode = r.ReadByte();
        r.ReadInt32(); // game version
        var info = new ReplayInfo { Path = path, BeatmapMd5 = OsrString(r) };
        info.Player = OsrString(r);
        OsrString(r); // replay md5
        info.N300 = r.ReadUInt16(); info.N100 = r.ReadUInt16(); info.N50 = r.ReadUInt16();
        info.NGeki = r.ReadUInt16(); info.NKatu = r.ReadUInt16();
        info.NMiss = r.ReadUInt16();
        info.Score = r.ReadInt32();
        info.MaxCombo = r.ReadUInt16();
        info.Perfect = r.ReadBoolean();
        info.Mods = (Mods)r.ReadInt32() & ModInfo.Supported;
        OsrString(r); // life bar graph
        info.Date = new DateTime(r.ReadInt64(), DateTimeKind.Utc).ToLocalTime();
        info.Mode = mode;
        info.Accuracy = Player.AccuracyFor(mode, info.N300, info.N100, info.N50, info.NMiss, info.NGeki, info.NKatu, info.Mods.HasFlag(Mods.ScoreV2));
        info.FromStable = true;
        byte[] data = withData ? r.ReadBytes(r.ReadInt32()) : [];
        return (info, mode, data);
    }

    // Writes frames as a stable .osr (osu!standard), so stable can play the replay too. Frame times become whole-ms
    // deltas, keys stable's bits (K1 = 5, K2 = 10, M1 = 1, M2 = 2).
    // exact: frame times as recorded (stable reads them after its -1ms lead-in frame). The fuzz tests keep the
    // older, 1ms-early layout so their recorded stable results stay valid.
    // lifeBar: the play's (time, hp 0..1) graph, written like stable's: a point whenever 2s have passed since the last
    // one, plus the first and last, as "time|hp," with both rounded to 2 decimals
    public static void SaveOsr(string path, ReplayInfo info, IReadOnlyList<ReplayFrame> frames, bool exact = false, IReadOnlyList<System.Numerics.Vector2>? lifeBar = null, string? player = null, bool? perfect = null)
    {
        var text = new System.Text.StringBuilder("0|256|-500|0,-1|256|-500|0,");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        long last = exact ? -1 : 0;
        foreach (var f in frames)
        {
            long t = (long)Math.Round(f.Time);
            int z = info.Mode != 0 ? f.Keys : ((f.Keys & 1) != 0 ? 5 : 0) | ((f.Keys & 2) != 0 ? 10 : 0) | ((f.Keys & 4) != 0 ? 1 : 0) | ((f.Keys & 8) != 0 ? 2 : 0) | (f.Keys & 16);
            text.Append(inv, $"{t - last}|{f.X:R}|{f.Y:R}|{z},");
            last = t;
        }
        text.Append(inv, $"-12345|0|0|{info.Seed},"); // (the play's random seed: mania's Random)
        using var w = new BinaryWriter(File.Create(path));
        void Str(string s) { w.Write((byte)0x0b); w.Write(s); } // BinaryWriter's length prefix is the same ULEB128
        w.Write((byte)info.Mode);
        w.Write(20250101);
        Str(info.BeatmapMd5);
        Str(player ?? info.Player);
        Str(Convert.ToHexStringLower(MD5.HashData(System.Text.Encoding.ASCII.GetBytes(text.ToString()))));
        w.Write((ushort)info.N300); w.Write((ushort)info.N100); w.Write((ushort)info.N50);
        w.Write((ushort)info.NGeki); w.Write((ushort)info.NKatu); w.Write((ushort)info.NMiss);
        w.Write((int)info.Score);
        w.Write((ushort)info.MaxCombo);
        w.Write(perfect ?? info.Perfect);
        w.Write((int)info.Mods);
        var bar = new System.Text.StringBuilder();
        float lastPoint = 0;
        for (int i = 0; lifeBar != null && i < lifeBar.Count; i++)
            if (lifeBar[i].X - lastPoint > 2000f || i == lifeBar.Count - 1 || i == 0)
            {
                lastPoint = lifeBar[i].X;
                bar.Append(inv, $"{Math.Round(lifeBar[i].X, 2)}|{Math.Round(lifeBar[i].Y, 2)},");
            }
        Str(bar.ToString());
        w.Write(info.Date.ToUniversalTime().Ticks);
        var data = Lzma.Encode(System.Text.Encoding.ASCII.GetBytes(text.ToString()));
        w.Write(data.Length);
        w.Write(data);
        w.Write(0L); // online score id
    }

    static string OsrString(BinaryReader r)
    {
        byte marker = r.ReadByte();
        if (marker == 0) return "";
        if (marker != 0x0b) throw new InvalidDataException("osr: bad string");
        int len = 0, shift = 0;
        byte part;
        do { part = r.ReadByte(); len |= (part & 0x7f) << shift; shift += 7; } while ((part & 0x80) != 0);
        return System.Text.Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    // The HP graph stable saved in an .osr ("ms|hp,..." with hp 0..1), which its ranking screen shows.
    public static List<System.Numerics.Vector2> OsrLifeBar(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        r.ReadByte(); r.ReadInt32();
        OsrString(r); OsrString(r); OsrString(r); // beatmap md5, player, replay md5
        r.ReadBytes(23); // six counts, score, max combo, perfect, mods
        var pts = new List<System.Numerics.Vector2>();
        foreach (var pair in OsrString(r).Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (pair.Split('|') is [var t, var hp]
                && float.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, out var ms)
                && float.TryParse(hp, System.Globalization.CultureInfo.InvariantCulture, out var v))
                pts.Add(new(ms, v));
        return pts;
    }

    public static string OsrText(string path) => System.Text.Encoding.ASCII.GetString(Lzma.Decode(ReadOsr(path, withData: true).Data));

    // info: gets the play's random seed from the trailing seed frame (mania's Random)
    public static ReplayFrame[] ReadOsrFrames(string path, bool rawKeys = false, ReplayInfo? info = null)
    {
        var text = OsrText(path);
        var frames = new List<ReplayFrame>();
        double t = 0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var entry in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = entry.Split('|');
            if (p.Length < 4) continue;
            long w = long.Parse(p[0], inv);
            if (w == -12345) { if (info != null && int.TryParse(p[3], inv, out int seed)) info.Seed = seed; continue; } // trailing RNG seed frame
            t += w;
            int z = int.Parse(p[3], inv);
            // stable: M1=1, M2=2, K1=4 (+M1), K2=8 (+M2). Ours: K1, K2, M1, M2 as bits 0-3.
            byte keys = rawKeys ? (byte)z : (byte)(((z & 4) != 0 ? 1 : 0) | ((z & 8) != 0 ? 2 : 0)
                | ((z & 1) != 0 && (z & 4) == 0 ? 4 : 0) | ((z & 2) != 0 && (z & 8) == 0 ? 8 : 0) | (z & 16)); // 16: smoke (rawKeys: the other modes, stable's bits as they are)
            frames.Add(new ReplayFrame(t, float.Parse(p[1], inv), float.Parse(p[2], inv), keys));
        }
        return frames.ToArray();
    }

    public static ReplayFrame[] Frames(ReplayInfo info) => info.FromStable ? ReadOsrFrames(info.Path, rawKeys: info.Mode != 0, info) : ReadFrames(info.Path);

    public static ReplayInfo ReadAnyInfo(string path) =>
        path.EndsWith(".osr", StringComparison.OrdinalIgnoreCase) ? ReadOsr(path, withData: false).Info : ReadInfo(path);

    static IEnumerable<string> StableReplayFiles()
    {
        foreach (var dir in new[] { "Replays", Path.Combine("Data", "r") })
        {
            var full = System.IO.Path.Combine(Config.OsuDir, dir);
            if (Directory.Exists(full))
                foreach (var f in Directory.EnumerateFiles(full, "*.osr")) yield return f;
        }
    }

    // Local leaderboard: every saved replay, grouped by beatmap MD5, best score first.
    public static Dictionary<string, List<ReplayInfo>> Index()
    {
        var index = new Dictionary<string, List<ReplayInfo>>();
        var ours = Directory.Exists(Dir) ? Directory.EnumerateFiles(Dir, "*.srr") : [];
        foreach (var file in ours)
        {
            try
            {
                using var r = new BinaryReader(File.OpenRead(file));
                var info = ReadHeader(r, file);
                if (!index.TryGetValue(info.BeatmapMd5, out var list)) index[info.BeatmapMd5] = list = [];
                list.Add(info);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { } // (a damaged file is left out, never fatal)
        }
        // Stable's replays (exported + local scores); osu!standard only, deduplicated by file content stats.
        // Headers are cached by path/size/mtime: opening ~2000 small files on every launch took 10-25s.
        var cache = LoadIndexCache();
        var fresh = new Dictionary<string, IndexEntry>();
        foreach (var file in StableReplayFiles())
        {
            try
            {
                var fi = new FileInfo(file);
                if (!cache.TryGetValue(file, out var entry) || entry.Length != fi.Length || entry.Modified != fi.LastWriteTimeUtc.Ticks)
                {
                    var (read, m, _) = ReadOsr(file, withData: false);
                    entry = new IndexEntry { Length = fi.Length, Modified = fi.LastWriteTimeUtc.Ticks, Mode = m, Info = read };
                }
                fresh[file] = entry;
                var (info, mode) = (entry.Info, entry.Mode);
                if (mode > 3) continue;
                info.Mode = mode;
                if (mode != 0) info.Accuracy = Player.AccuracyFor(mode, info.N300, info.N100, info.N50, info.NMiss, info.NGeki, info.NKatu, info.Mods.HasFlag(Mods.ScoreV2)); // (cached entries from before)
                if (!index.TryGetValue(info.BeatmapMd5, out var list)) index[info.BeatmapMd5] = list = [];
                if (list.Any(o => o.FromStable && o.Score == info.Score && o.Date == info.Date)) continue; // same play exported twice
                list.Add(info);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { } // (a damaged file is left out, never fatal)
        }
        if (fresh.Count != cache.Count || fresh.Any(kv => !cache.ContainsKey(kv.Key)) || cache.Any(kv => fresh.TryGetValue(kv.Key, out var e) && e.Modified != kv.Value.Modified))
            try { File.WriteAllText(IndexCachePath, System.Text.Json.JsonSerializer.Serialize(fresh, JsonFields)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // a stale or missing cache only costs speed
        foreach (var list in index.Values) list.Sort((a, b) => b.Score.CompareTo(a.Score));
        return index;
    }

    sealed class IndexEntry
    {
        public long Length, Modified;
        public byte Mode;
        public ReplayInfo Info = new();
    }

    static readonly System.Text.Json.JsonSerializerOptions JsonFields = new() { IncludeFields = true };
    static string IndexCachePath => System.IO.Path.Combine(AppContext.BaseDirectory, "replay-index.json");

    static Dictionary<string, IndexEntry> LoadIndexCache()
    {
        try
        {
            return File.Exists(IndexCachePath)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, IndexEntry>>(File.ReadAllText(IndexCachePath), JsonFields) ?? []
                : [];
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return []; }
    }
}
