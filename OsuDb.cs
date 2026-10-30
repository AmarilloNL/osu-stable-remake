using System.Text;

namespace OsuStableRemake;

sealed class DbBeatmap
{
    public string Artist = "", ArtistUnicode = "", Title = "", TitleUnicode = "", Creator = "", Version = "";
    public string AudioFile = "", Md5 = "", OsuFile = "", Source = "", Tags = "", Folder = "";
    // Lowercased text the song select search matches against (built off the main thread by the library loader):
    // the set's text, shared by maps whose fields are the same strings, plus the difficulty name on its own.
    public string? SearchKey, VersionKey;
    string? panelSubtitle;
    public string PanelSubtitle => panelSubtitle ??= $"{Artist} // {Creator}"; // song select draws it every frame
    public bool SearchMatches(string word) => SearchKey!.Contains(word) || VersionKey!.Contains(word);

    public void BuildSearchKey(DbBeatmap? prev)
    {
        VersionKey = Version.ToLowerInvariant();
        SearchKey = prev != null && (object)Artist == prev.Artist && (object)ArtistUnicode == prev.ArtistUnicode && (object)Title == prev.Title
            && (object)TitleUnicode == prev.TitleUnicode && (object)Creator == prev.Creator && (object)Source == prev.Source && (object)Tags == prev.Tags
            ? prev.SearchKey : $"{Artist} {ArtistUnicode} {Title} {TitleUnicode} {Creator} {Source} {Tags}".ToLowerInvariant();
    }
    public int Circles, Sliders, Spinners, DrainSeconds, TotalMs, PreviewMs, BeatmapId, SetId;
    public float AR, CS, HP, OD;
    public long LastModified, LastPlayed;
    public bool Unplayed;
    public double MinBpm, MaxBpm, MainBpm; // MainBpm: the one held longest (stable's "(147)")
    public byte Mode, Status;
    // stable stores star ratings per difficulty-changing mod combination (EZ/HR/DT/HT: 16 slots, NaN = none).
    float[]? stars;

    static int StarSlot(int mods) => ((mods & (int)Mods.Easy) != 0 ? 1 : 0) | ((mods & (int)Mods.HardRock) != 0 ? 2 : 0)
        | ((mods & (int)Mods.DoubleTime) != 0 ? 4 : 0) | ((mods & (int)Mods.HalfTime) != 0 ? 8 : 0);

    // Newer databases also list HD/FL combinations; those would land in (and overwrite) these slots, so they're skipped.
    // (So HD/FL ratings aren't shown; the header would need wider slots for them.)
    public void SetStars(int mods, float value)
    {
        if ((mods & ~(int)(Mods.Easy | Mods.HardRock | Mods.DoubleTime | Mods.HalfTime)) != 0) return;
        if (stars == null) { stars = new float[16]; Array.Fill(stars, float.NaN); }
        stars[StarSlot(mods)] = value;
    }

    public float StarsFor(Mods m)
    {
        if (stars == null) return 0;
        float s = stars[StarSlot((int)m)];
        return float.IsNaN(s) ? float.IsNaN(stars[0]) ? 0 : stars[0] : s;
    }

    string? filePath; // checked every frame by song select (background, timing): built once
    public string FilePath => filePath ??= Path.Combine(Config.OsuDir, "Songs", Folder, OsuFile);
}

// Reader for stable's osu!.db (layout verified against stable's own reader for v20260924).
static class OsuDb
{
    // Loading ~100k maps used to allocate 430MB and keep 325MB, promoted through every GC generation while the
    // menu runs (35-80ms hitches). So: one read buffer for all strings, a field's string shared with the previous
    // map's when the bytes match (a set's maps sit together), and reused scratch lists for the BPM pass.
    sealed class Reader(BinaryReader r)
    {
        public readonly BinaryReader R = r;
        byte[] buf = new byte[1024];
        readonly (byte[]? Bytes, int Len, string Value)[] last = new (byte[]?, int, string)[Slots];
        public readonly List<(double Offset, double Bpm)> Reds = [];
        public readonly Dictionary<double, double> Held = [];
        public const int Slots = 9, NoSlot = -1;

        public string String(int slot)
        {
            int len = Length();
            if (len == 0) return "";
            if (buf.Length < len) buf = new byte[Math.Max(len, buf.Length * 2)];
            R.BaseStream.ReadExactly(buf, 0, len);
            var bytes = buf.AsSpan(0, len);
            if (slot == NoSlot) return Encoding.UTF8.GetString(bytes);
            ref var l = ref last[slot];
            if (l.Bytes != null && bytes.SequenceEqual(l.Bytes.AsSpan(0, l.Len))) return l.Value;
            if (l.Bytes == null || l.Bytes.Length < len) l.Bytes = new byte[Math.Max(len, 256)];
            bytes.CopyTo(l.Bytes);
            l.Len = len;
            return l.Value = Encoding.UTF8.GetString(bytes);
        }

        public void Skip() => R.BaseStream.Seek(Length(), SeekOrigin.Current);

        int Length()
        {
            byte marker = R.ReadByte();
            if (marker == 0) return 0;
            if (marker != 0x0b) throw new InvalidDataException($"osu!.db: bad string marker 0x{marker:x2}");
            int len = 0, shift = 0;
            byte part;
            do { part = R.ReadByte(); len |= (part & 0x7f) << shift; shift += 7; } while ((part & 0x80) != 0);
            return len;
        }
    }

    public static List<DbBeatmap> Load(string path)
    {
        using var stream = new BufferedStream(File.OpenRead(path), 1 << 20);
        using var r = new BinaryReader(stream);
        var rd = new Reader(r);
        int version = r.ReadInt32();
        r.ReadInt32();            // folder count
        r.ReadBoolean();          // account unlocked
        r.ReadInt64();            // unlock date
        rd.Skip();                // player name
        int count = r.ReadInt32();
        var list = new List<DbBeatmap>(count);
        for (int i = 0; i < count; i++) list.Add(ReadBeatmap(rd, version));
        r.ReadInt32();            // user permissions
        if (stream.Position != stream.Length) throw new InvalidDataException($"osu!.db: {stream.Length - stream.Position} unread bytes");
        return list;
    }

    static DbBeatmap ReadBeatmap(Reader rd, int version)
    {
        var r = rd.R;
        if (version < 20191106) r.ReadInt32(); // entry size
        var b = new DbBeatmap
        {
            Artist = rd.String(0), ArtistUnicode = rd.String(1), Title = rd.String(2), TitleUnicode = rd.String(3),
            Creator = rd.String(4), Version = rd.String(Reader.NoSlot), AudioFile = rd.String(5), Md5 = rd.String(Reader.NoSlot),
            OsuFile = rd.String(Reader.NoSlot),
            Status = r.ReadByte(),
            Circles = r.ReadUInt16(), Sliders = r.ReadUInt16(), Spinners = r.ReadUInt16(),
        };
        b.LastModified = r.ReadInt64(); // stable's "Date Added" sort key
        b.AR = r.ReadSingle(); b.CS = r.ReadSingle(); b.HP = r.ReadSingle(); b.OD = r.ReadSingle();
        r.ReadDouble(); // slider velocity
        for (int mode = 0; mode < 4; mode++)
        {
            int n = r.ReadInt32();
            for (int k = 0; k < n; k++)
            {
                r.ReadByte(); int mods = r.ReadInt32();
                byte type = r.ReadByte();
                float stars = type == 0x0d ? (float)r.ReadDouble() : r.ReadSingle();
                if (mode == 0) b.SetStars(mods, stars);
            }
        }
        b.DrainSeconds = r.ReadInt32(); b.TotalMs = r.ReadInt32(); b.PreviewMs = r.ReadInt32();
        int timingPoints = r.ReadInt32();
        b.MinBpm = double.MaxValue; b.MaxBpm = 0;
        var reds = rd.Reds;
        reds.Clear();
        for (int k = 0; k < timingPoints; k++)
        {
            double msPerBeat = r.ReadDouble(); double offset = r.ReadDouble(); bool uninherited = r.ReadBoolean();
            if (!uninherited || msPerBeat <= 0) continue;
            double bpm = 60000 / msPerBeat;
            b.MinBpm = Math.Min(b.MinBpm, bpm); b.MaxBpm = Math.Max(b.MaxBpm, bpm);
            reds.Add((offset, bpm));
        }
        if (b.MaxBpm == 0) b.MinBpm = 0;
        var held = rd.Held;
        held.Clear();
        for (int k = 0; k < reds.Count; k++)
        {
            double end = k + 1 < reds.Count ? reds[k + 1].Offset : Math.Max(reds[k].Offset, b.TotalMs);
            double key = Math.Round(reds[k].Bpm, 2);
            held[key] = held.GetValueOrDefault(key) + Math.Max(0, end - reds[k].Offset);
        }
        b.MainBpm = b.MaxBpm;
        double longest = double.NegativeInfinity;
        foreach (var (bpm, ms) in held) if (ms > longest) { longest = ms; b.MainBpm = bpm; } // first of equals, like MaxBy
        b.BeatmapId = r.ReadInt32(); b.SetId = r.ReadInt32();
        r.ReadInt32();                    // thread id
        r.ReadInt32();                    // grades per mode (4 bytes)
        r.ReadInt16();                    // local offset
        r.ReadSingle();                   // stack leniency
        b.Mode = r.ReadByte();
        b.Source = rd.String(6); b.Tags = rd.String(7);
        r.ReadInt16();                    // online offset
        rd.Skip();                        // title font
        b.Unplayed = r.ReadBoolean();
        b.LastPlayed = r.ReadInt64();     // DateTime ticks (UTC)
        r.ReadBoolean();                  // osz2
        b.Folder = rd.String(8);
        r.ReadInt64();                    // last online check
        r.ReadInt32(); r.ReadByte();      // ignore sound/skin, disable storyboard/video, visual override (5 bytes)
        if (version < 20140609) r.ReadInt16();
        r.ReadInt32();                    // last modification
        r.ReadByte();                     // mania scroll speed
        return b;
    }

    // stable's collection.db: version, then per collection a name and its beatmap MD5s.
    public static List<(string Name, HashSet<string> Md5s)> LoadCollections(string path)
    {
        var result = new List<(string, HashSet<string>)>();
        if (!File.Exists(path)) return result;
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            r.ReadInt32();
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string name = ReadString(r);
                int n = r.ReadInt32();
                var md5s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int k = 0; k < n; k++) md5s.Add(ReadString(r));
                result.Add((name, md5s));
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException) { }
        return result;
    }

    static string ReadString(BinaryReader r)
    {
        byte marker = r.ReadByte();
        if (marker == 0) return "";
        if (marker != 0x0b) throw new InvalidDataException($"osu!.db: bad string marker 0x{marker:x2}");
        int len = 0, shift = 0;
        byte part;
        do { part = r.ReadByte(); len |= (part & 0x7f) << shift; shift += 7; } while ((part & 0x80) != 0);
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }
}
