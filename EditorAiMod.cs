using System.Globalization;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// AiMod (File → Open AiMod, Ctrl+Shift+A): stable's map checker in its own small window. The problems by tab (Compose,
// Design, Timing, Meta, Mapset), each an error, a warning or a note with its time; a click on one seeks there and selects
// its objects (two objects get a line between them). Refresh checks again; "Check distance snap" adds the spacing checks.
sealed partial class Editor
{
    enum AiTab { All, Compose, Design, Timing, Meta, Mapset }
    sealed record AiItem(AiTab Tab, int Level, string Text, double Time = -1, EObj? A = null, EObj? B = null); // Level: 0 note, 1 warning, 2 error

    sealed class AiModWindow
    {
        public List<AiItem> Items = [];
        public AiTab Tab;
        public bool DistanceSnap;
        public int Scroll, Selected = -1;
        public Vector2 Pos = new(float.NaN);
        public Vector2? Drag;
        public string Difficulty = "";
        public Vector2 Mouse; // (as the window saw it: the editor below gets none while it has it)
        public IEnumerable<AiItem> Shown => Tab == AiTab.All ? Items : Items.Where(i => i.Tab == Tab);
    }
    AiModWindow? aimod;
    bool windowPress; // the left button went down on the AiMod or Sample import window

    void OpenAiMod()
    {
        aimod ??= new AiModWindow();
        AiRefresh();
    }

    void AiRefresh()
    {
        var w = aimod!;
        w.Items = AiCheck(w.DistanceSnap);
        w.Difficulty = aiClass.ToString();
        w.Selected = -1;
        w.Scroll = 0;
    }

    // ---- the checks
    enum AiDiff { Easy, Normal, Hard, Insane, Expert, ExpertPlus }

    AiDiff aiClass;

    // the map's no-mod star rating in its mode: osu!.db's for the saved file, else (osu!) worked out here
    double AiStars(string file, int fileMode, bool live)
    {
        if (!live)
            try
            {
                string md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
                if (back.FindMap(md5)?.StoredStars(Mods.None, fileMode) is { } stored) return stored;
            }
            catch (IOException) { }
        if (fileMode != 0) return 0; // (stable reads a missing rating as 0)
        try { return StarRating.Compute(live ? ReloadInfo() : Beatmap.Load(file), Mods.None); }
        catch (Exception e) when (e is IOException or FormatException) { return 0; }
    }

    // stable's difficulty class, from the star rating: it sets how lenient some checks are
    static AiDiff AiClassOf(double stars) => stars < 2 ? AiDiff.Easy : stars < 2.7 ? AiDiff.Normal : stars < 4 ? AiDiff.Hard : stars < 5.3 ? AiDiff.Insane : stars < 6.5 ? AiDiff.Expert : AiDiff.ExpertPlus;

    // stable's order: Compose, Design, Timing, Meta, Mapset (each with its mode's rules)
    List<AiItem> AiCheck(bool distanceSnap)
    {
        aiClass = AiClassOf(AiStars(path, mode, live: modified));
        var items = new List<AiItem>();
        if (mode == 0) AiCompose(items, distanceSnap);
        else if (mode == 3) AiComposeMania(items);
        else AiComposeSnap(items); // (taiko and catch: only the snapping)
        AiDesign(items);
        AiTiming(items);
        AiMeta(items);
        AiMapset(items);
        return items;
    }

    double KvNum(string section, string key, double fallback) => double.TryParse(Kv(section, key), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
    // stable's "total length": the last object's time (a spinner's or hold's end)
    static double TotalLength(IEnumerable<(double Time, double End, int Type)> objects) =>
        objects.Select(o => (o.Type & (8 | 128)) != 0 ? o.End : o.Time).DefaultIfEmpty(0).Max();
    // exactly on a 1/8 or 1/6 tick (within 1ms)
    bool AiSnappedStrict(double t) => Math.Abs(AiSnap(t, 8) - t) < 1 || Math.Abs(AiSnap(t, 6) - t) < 1;

    // ---- Compose for osu!mania (in place of the others): snapping, short holds, notes too close, more than six at
    // once (Insane and below), stacked and overlapping notes
    void AiComposeMania(List<AiItem> items)
    {
        const AiTab C = AiTab.Compose;
        int keys = Math.Max(1, (int)Math.Round(KvNum("Difficulty", "CircleSize", 4)));
        var busy = Enumerable.Repeat(double.MinValue, keys).ToArray();
        var lastStart = new double[keys];
        var placed = Enumerable.Range(0, keys).Select(_ => new Dictionary<double, double>()).ToArray();
        foreach (var o in objs)
        {
            int col = Math.Min((int)Math.Floor(o.X / (512.0 / keys)), keys - 1);
            if (col < 0) col = 0;
            bool hold = (o.Type & 128) != 0;
            double s = o.Time, e = hold ? o.End : o.Time;
            if (!AiSnappedStrict(s)) items.Add(AiRareSnap(s, 1) is > 0 and var d ? new(C, 0, $"Rare snap divisor used (1/{d})", s, o) : new(C, 1, "Object isn't snapped!", s, o));
            if (hold && e - s < 10) items.Add(new(C, 2, "This hold note is less than 10ms long!", s, o));
            if (hold && !AiSnappedStrict(e)) items.Add(AiRareSnap(e, 1) is > 0 and var d2 ? new(C, 0, $"Rare snap divisor used (1/{d2})", e, o) : new(C, 1, "Object's end is not snapped!", e, o));
            if (s != lastStart[col] && lastStart[col] != 0 && s < lastStart[col] + 10) items.Add(new(C, 2, "These two objects are less than 10ms apart!", s, o));
            int together = 1 + busy.Count(b => b >= s);
            if (together >= 7 && aiClass <= AiDiff.Insane) items.Add(new(C, 2, "More than 6 notes simultaneously is not allowed.", s, o));
            busy[col] = hold ? e - 2 : s;
            if (placed[col].ContainsKey(s) || placed[col].ContainsKey(e)) { items.Add(new(C, 2, "This object is stacked on top of another object.", s, o)); continue; }
            if (placed[col].Any(p => p.Key != p.Value && (s <= p.Key && p.Key <= e || s <= p.Value && p.Value <= e || s >= p.Key && e <= p.Value || s < p.Key && e > p.Value)))
            {
                items.Add(new(C, 2, "This object overlaps with another object.", s, o));
                continue;
            }
            placed[col][s] = e;
            lastStart[col] = s;
        }
    }

    // ---- Design: the background, the video's size, quickly toggled storyboard sprites, missing storyboard images
    void AiDesign(List<AiItem> items)
    {
        const AiTab D = AiTab.Design;
        EnsureStoryboard(); // (the video too)
        if (info.Background.Length == 0) items.Add(new(D, 2, "Your beatmap has no background image."));
        else
        {
            if (bg is null) LoadBg();
            if (bg is { Id: not 0 } b && (b.Width > 2560 || b.Height > 1440)) items.Add(new(D, 1, "Background image is larger than 2560x1440."));
        }
        if (video is { Width: > 0 } v)
        {
            if (v.Width > 1280 && v.Height > 720) items.Add(new(D, 1, "Your video's dimensions must not exceed 1280x720 for the 16:9 format."));
            else if (v.Width > 1024 && v.Height > 768) items.Add(new(D, 1, "Your video's dimensions must not exceed 1024x768 for the 4:3 format."));
        }
        SbRefresh();
        if (sbView is not { } sb) return;
        if (Kv("General", "EpilepsyWarning") != "1" && sb.CountShorterThan(100) + info.Breaks.Count(x => x.End - x.Start < 100) > 5)
            items.Add(new(D, 1, "This map may need an epilepsy warning, as it contains frequently toggled storyboards."));
        bool skinSprites = Kv("General", "UseSkinSprites") == "1";
        bool Image(string rel) => Path.GetExtension(rel).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" && File.Exists(Path.Combine(info.Folder, rel));
        foreach (var file in sb.ReferencedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Image(file)) continue;
            int dot = file.LastIndexOf('.');
            if (skinSprites ? skin.Tex(Path.GetFileNameWithoutExtension(file)) != null
                : dot > 0 && Image(file[..dot] + "0" + file[dot..]) && Image(file[..dot] + "1" + file[dot..])) continue;
            items.Add(new(D, 2, "File missing:" + file));
        }
    }

    // ---- Timing: the map's length against the song's, timing points (order, overlaps, kiai), volumes, preview point,
    // the audio's bitrate; taiko's kiai toggles and mania's breaks and speed changes first
    void AiTiming(List<AiItem> items)
    {
        const AiTab T = AiTab.Timing;
        var points = timing.OrderBy(p => p.Time).ToList();
        if (mode == 1)
        {
            var kiai = points.Where(p => (p.Effects & 1) != 0).ToList();
            for (int i = 0; i + 1 < kiai.Count; i++)
                if (kiai[i + 1].Time - kiai[i].Time < 4 * RedAt(kiai[i].Time).Beat) items.Add(new(T, 2, "Kiai is toggled very frequently!"));
        }
        if (mode == 3)
        {
            if (info.Breaks.Count > 0) items.Add(new(T, 1, "Breaktime is not suggested for mania maps."));
            int changes = points.Count(p => p.Beat < 0 && p.Beat != -100);
            if (aiClass == AiDiff.Easy && changes > 0 && (int)TotalLength(objs.Select(o => (o.Time, o.End, o.Type))) / changes > 15000)
                items.Add(new(T, 1, "Easy/Normal diff contains too many speed changes."));
        }
        double drain = objs.Count > 0 ? objs[^1].End - objs[0].Time : 0;
        if (objs.Count > 0)
        {
            if (drain > 360000) items.Add(new(T, 1, "This beatmap is over 6 minutes long. Consider shortening it if it's not a marathon-style map."));
            else if (drain < 45000) items.Add(new(T, 1, "Your beatmap is shorter than 45 seconds. Consider making it longer."));
            if (music != null && drain / (length - objs[0].Time) < 0.8) items.Add(new(T, 1, "The mp3 you are using is a lot longer than the part which is mapped.  Consider cropping it to reduce filesize."));
        }
        bool seenRed = false, inKiai = false;
        double lastKiaiEnd = 0, kiaiTotal = 0;
        int quiet = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            double duration = (i + 1 < points.Count ? points[i + 1].Time : objs.Count > 0 ? objs[^1].End : p.Time) - p.Time;
            bool kiai = (p.Effects & 1) != 0;
            if (!seenRed)
            {
                if (!p.Red) items.Add(new(T, 2, "Inherited timing point before the first uninherited timing point!", p.Time));
                else { seenRed = true; if (kiai) items.Add(new(T, 2, "First uninherited timing point toggles kiai!", p.Time)); }
            }
            for (int k = i - 1; k >= 0 && (int)points[k].Time == (int)p.Time; k--)
                if (points[k].Red == p.Red) { items.Add(new(T, 1, "Two timing points exist at the same time!", p.Time)); break; }
            if (kiai && !inKiai)
            {
                inKiai = true;
                if (!AiSnappedStrict(p.Time)) items.Add(new(T, 1, "Kiai isn't snapped!", p.Time));
                if (p.Time - lastKiaiEnd < 15000 && p.Time - lastKiaiEnd > 10) items.Add(new(T, 1, "Kiai time is toggled on for less than 15 seconds.", p.Time));
            }
            else if (!kiai && inKiai)
            {
                inKiai = false;
                if (!AiSnappedStrict(p.Time)) items.Add(new(T, 1, "Kiai's end isn't snapped!", p.Time));
                lastKiaiEnd = p.Time;
            }
            if (kiai && duration > 0) kiaiTotal += duration;
            if (p.Volume < 5) quiet++;
        }
        if (quiet == points.Count) items.Add(new(T, 2, "All timing sections have a volume below 5%."));
        else if (quiet > 0) items.Add(new(T, 1, $"{quiet} out of {points.Count} timing sections have a volume below 5%."));
        if (drain > 180000 && kiaiTotal > (int)drain / 3) items.Add(new(T, 1, "More than 1/3 of the map is Kiai time.  Consider reducing this."));
        else if (drain < 90000 && kiaiTotal > (int)drain / 2) items.Add(new(T, 1, "More than 1/2 of the TV Size map is Kiai time.  Consider reducing this."));
        if (KvNum("General", "PreviewTime", -1) < 0) items.Add(new(T, 1, "A preview point for this map is not set.  Consider setting one from the Timing menu."));
        if (inKiai) items.Add(new(T, 1, "Kiai needs an end time point."));
        if (Track.Probe(Path.Combine(info.Folder, info.AudioFilename)) is { Kbps: > 0 } audio)
        {
            if (audio.Kbps < 128) items.Add(new(T, 1, "Audio bitrate is lower than 128kbps. Consider finding a better quality source."));
            else if (audio.Kbps > 192) items.Add(new(T, 1, "Audio bitrate is higher than 192kbps. Consider recompressing to CBR 192kbps or VBR ~1.0."));
        }
    }

    // ---- Meta: romanised metadata, stack leniency (osu!), drain time; taiko's letterbox and slider velocity, mania's
    // letterbox, countdown and HP / OD suggestions
    void AiMeta(List<AiItem> items)
    {
        const AiTab M = AiTab.Meta;
        bool letterbox = Kv("General", "LetterboxInBreaks") == "1";
        if (mode == 1)
        {
            if (letterbox) items.Add(new(M, 1, "Letterboxing is not allowed in taiko mode."));
            double sv = KvNum("Difficulty", "SliderMultiplier", 1.4);
            if (sv != 1.4 && sv != 1.6) items.Add(new(M, 0, "The Slider Velocity should be 1.40 or 1.60."));
        }
        if (mode == 3)
        {
            if (letterbox) items.Add(new(M, 1, "Letterboxing is not allowed in mania mode."));
            if (KvNum("General", "Countdown", 1) != 0) items.Add(new(M, 1, "Countdown is not allowed in mania mode."));
        }
        static bool Unicode(string? s) => s != null && s.Any(c => c > 'Ā');
        if (Unicode(Kv("Metadata", "Artist"))) items.Add(new(M, 2, "Romanised artist contains unicode."));
        if (Unicode(Kv("Metadata", "Title"))) items.Add(new(M, 2, "Romanised title contains unicode."));
        if (Unicode(Kv("Metadata", "Version"))) items.Add(new(M, 1, "Difficulty name contains unicode characters."));
        float stack = (float)KvNum("General", "StackLeniency", 0.7);
        if (mode == 0 && (stack < 0.3f || stack > 0.9f)) items.Add(new(M, 1, "Stack leniency is larger than 0.9 or smaller than 0.3."));
        double breaks = info.Breaks.Sum(b => b.End - b.Start);
        if (objs.Count == 0 || (int)((objs[^1].Time - objs[0].Time - breaks) / 1000) <= 30) items.Add(new(M, 2, "Drain time should be over 30 seconds."));
        if (mode == 3)
        {
            double hp = Math.Round(KvNum("Difficulty", "HPDrainRate", 5)), od = Math.Round(KvNum("Difficulty", "OverallDifficulty", 5));
            if (hp < 4 && aiClass < AiDiff.Hard) items.Add(new(M, 0, "HP rate for Easy/Normal is suggested to be at least 4."));
            else if (hp < 7 && aiClass >= AiDiff.Hard) items.Add(new(M, 0, "HP rate for Hard and up is suggested to be at least 7."));
            double holds = (double)objs.Count(o => o.Slider || (o.Type & 128) != 0) / objs.Count; // (NaN without objects: the last case)
            if (holds < 0.05 && od < 8) items.Add(new(M, 0, "OD rate for maps with very few sliders is suggested to be at least 8."));
            else if (holds < 0.25 && od < 7) items.Add(new(M, 0, "OD rate for maps with few sliders is suggested to be at least 7."));
            else if (od < 5) items.Add(new(M, 0, "OD rate is suggested to be at least 5."));
        }
    }

    // ---- Mapset: against the set's other difficulties (metadata, timing, how many there are, an easy enough one), the
    // folder's size and its song files
    sealed record AiDiffFile(string File, Dictionary<string, string> Kv, List<(int Time, double Beat)> Reds, double TotalLength, int Mode);
    static AiDiffFile? ReadDiff(string file)
    {
        try
        {
            var kv = new Dictionary<string, string>();
            var reds = new List<(int, double)>();
            var objects = new List<(double, double, int)>();
            string section = "";
            foreach (var raw in File.ReadLines(file))
            {
                string line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
                if (section is "General" or "Metadata" or "Difficulty" && line.IndexOf(':') is var c and > 0) kv[line[..c].Trim()] = line[(c + 1)..].Trim();
                var f = line.Split(',');
                if (section == "TimingPoints" && f.Length >= 2 && double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double beat)
                    && (f.Length <= 6 || f[6].Trim() == "1")) reds.Add(((int)t, beat));
                if (section == "HitObjects" && f.Length >= 5 && double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double at) && int.TryParse(f[3], out int type))
                    objects.Add((at, (type & 8) != 0 && f.Length > 5 && double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double se) ? se
                        : (type & 128) != 0 && f.Length > 5 && double.TryParse(f[5].Split(':')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double he) ? he : at, type));
            }
            return new(file, kv, reds, TotalLength(objects), kv.TryGetValue("Mode", out var m) && int.TryParse(m, out int mm) ? mm : 0);
        }
        catch (IOException) { return null; }
    }

    void AiMapset(List<AiItem> items)
    {
        const AiTab S = AiTab.Mapset;
        List<AiDiffFile> others;
        try
        {
            others = [.. Directory.EnumerateFiles(info.Folder, "*.osu").Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                .Select(ReadDiff).OfType<AiDiffFile>()];
        }
        catch (IOException) { others = []; }
        string Mine(string key, string fallback) => Kv(key is "Artist" or "Title" or "ArtistUnicode" or "TitleUnicode" or "Source" or "Tags" ? "Metadata" : "General", key) ?? fallback;
        (string Key, string Text, string Default)[] fields = [("Artist", "Artist conflicts", ""), ("Title", "Title conflicts", ""), ("ArtistUnicode", "Unicode artist conflicts", ""),
            ("TitleUnicode", "Unicode title conflicts", ""), ("Source", "Source conflicts", ""), ("Tags", "Tags conflict", ""), ("AudioFilename", "Audio file conflicts", ""),
            ("AudioLeadIn", "Audio lead-in conflicts", "0"), ("PreviewTime", "Audio preview time conflicts", "-1"), ("Countdown", "Countdown conflicts", "1"),
            ("LetterboxInBreaks", "Letterbox in breaks conflicts", "0")];
        var myReds = timing.Where(p => p.Red).OrderBy(p => p.Time).Select(p => ((int)p.Time, p.Beat)).ToList();
        foreach (var d in others)
        {
            string version = d.Kv.GetValueOrDefault("Version", "");
            foreach (var (key, text, def) in fields)
                if (Mine(key, def) != d.Kv.GetValueOrDefault(key, def)) items.Add(new(S, 1, $"{text} with {version} diff."));
            if (Mine("AudioFilename", "") == d.Kv.GetValueOrDefault("AudioFilename", "") && !myReds.SequenceEqual(d.Reds))
                items.Add(new(S, 1, "Uninherited timing points conflict with " + version + " diff."));
        }
        var modes = others.Select(d => d.Mode).Append(mode).ToList();
        foreach (var (m, name) in new[] { (0, "standard"), (1, "Taiko"), (3, "osu!mania") })
            if (modes.Count(x => x == m) == 1) items.Add(new(S, 1, $"This mapset needs at least 2 {name} diffs."));
        // an easy enough osu! difficulty for the song's length
        double drain = objs.Count > 0 ? objs[^1].End - objs[0].Time : 0;
        double limit = drain > 300000 ? 999 : drain > 255000 ? 5.3 : drain > 210000 ? 4.0 : 2.7;
        var osuStars = others.Where(d => d.Mode == 0).Select(d => AiStars(d.File, 0, live: false)).ToList();
        if (mode == 0) osuStars.Add(AiStars(path, 0, live: modified));
        if (osuStars.Count > 0 && osuStars.Aggregate(10.0, Math.Min) > limit) items.Add(new(S, 1, "This mapset needs an easier difficulty."));
        try
        {
            long total = Directory.EnumerateFiles(info.Folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            double longest = others.Select(d => d.TotalLength).Append(TotalLength(objs.Select(o => (o.Time, o.End, o.Type)))).Max();
            long allowed = (long)(longest / 1000) * 166667 + 5242880;
            if (total > allowed) items.Add(new(S, 1, string.Format(CultureInfo.InvariantCulture, "Beatmap exceeds allowed upload size ({0:n0}kb allowed)", allowed / 1024)));
            int songs = 0;
            foreach (var f in Directory.EnumerateFiles(info.Folder).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".mp3" or ".ogg" or ".wav"))
            {
                if (Track.Probe(f) is not { } a) items.Add(new(S, 1, "Beatmap audio has unsupported format."));
                else if (a.LengthMs > 20000) songs++;
            }
            if (songs > 1) items.Add(new(S, 1, $"There are {songs} song files in your map directory!"));
        }
        catch (IOException) { }
    }

    // stable's snap: the time to the nearest 1/divisor tick of its red point, in whole milliseconds
    int AiSnap(double t, int divisor)
    {
        if (!timing.Any(p => p.Red && p.Beat > 0)) return (int)t;
        var red = RedAt(t);
        double step = red.Beat / divisor;
        int k = t - red.Time < 0 ? (int)((t - red.Time) / step) - 1 : (int)((t - red.Time) / step);
        int lo = (int)(k * step + red.Time), hi = (int)((k + 1) * step + red.Time);
        return !(t - lo < hi - t) ? hi : lo;
    }
    // more than `tolerance` ms off both the 1/8 and the 1/6 snap
    bool AiUnsnapped(double t, double tolerance) => Math.Abs(AiSnap(t, 8) - t) > tolerance && Math.Abs(AiSnap(t, 6) - t) > tolerance;
    // on a 1/5, 1/7, 1/9, 1/12 or 1/16 tick (the first of them it fits)
    int AiRareSnap(double t, double within = 1.01) { foreach (int d in (int[])[5, 7, 9, 12, 16]) if (Math.Abs(AiSnap(t, d) - t) < within) return d; return -1; }

    static Vector2 Pos(EObj o) => new((float)o.X, (float)o.Y);
    static Vector2 EndPos(EObj o) => o.Slider && o.Path.Length > 0 ? (o.Slides % 2 == 1 ? o.Path[^1] : o.Path[0]) : Pos(o);

    void AiCompose(List<AiItem> items, bool distanceSnap)
    {
        const AiTab C = AiTab.Compose;
        double lenience = aiClass switch { AiDiff.Easy => 0.5, AiDiff.Normal => 1, AiDiff.Hard => 1.5, _ => 2.5 };
        float margin = Radius - 1;
        bool Offscreen(Vector2 p) => p.X - margin < -64 || p.X + margin > 576 || p.Y - margin < -48 || p.Y + margin > 432;
        bool hitsounds = false;
        double spacing = 0;
        for (int i = 0; i < objs.Count; i++)
        {
            var o = objs[i];
            var next = i + 1 < objs.Count ? objs[i + 1] : null;
            if (Offscreen(Pos(o))) items.Add(new(C, 1, "Object is offscreen!", o.Time, o));
            if (EndPos(o) != Pos(o) && Offscreen(EndPos(o))) items.Add(new(C, 1, "Object's end is offscreen!", o.End, o));
            if (o.Slides > 1 && o.Slider && o.Path.Length > 0 && Offscreen(o.Path[^1])) items.Add(new(C, 1, "Object's end is offscreen!", o.End, o));
            if (o.Combo > 24) items.Add(new(C, 1, "This combo is very long. Consider splitting it up.", o.End, o));
            if (i > 0 && next != null && o.Time == next.Time) items.Add(Pair(objs[i - 1], o, 2, "These two objects exist at the same point in time!"));
            if (i > 0 && objs[i - 1].Spinner && !o.Spinner && objs[i - 1].Time > o.Time - Preempt + 400)
                items.Add(Pair(objs[i - 1], o, 2, "This spinner appears onscreen later than objects which follow it."));
            if (o.Slider && AbnormalSlider(o.Path, Radius)) items.Add(new(C, 2, "This slider moves in an abnormal way.", o.Time, o));
            if (o.Slider && o.Anchors.Count > 40) items.Add(new(C, 1, "Slider has an absurdly large amount of points!", o.Time, o));
            if (o.Spinner)
            {
                if (!o.NewCombo) items.Add(new(C, 1, "Spinners must have a new combo.", o.Time, o));
                // Auto's spins against the ones it needs: at least 5 to spare (1000 bonus)
                double length = o.End - o.Time, auto = 0.05 * length / Math.PI, needed = length / 1000 * info.SpinsPerSecond;
                if (auto - needed < 5) items.Add(new(C, 1, "This spinner is too short. Auto must achieve at least 1000 bonus points on spinners.", o.Time, o));
            }
            // distance snap: against the spacing at the combo's start, 10% (closer: 20%) times the difficulty's lenience
            if (distanceSnap && next != null && !next.NewCombo && !next.Spinner)
            {
                double perSecond = 1000 * 100 * SliderMultiplier * SvAt(next.Time) / RedAt(next.Time).Beat, dt = (int)next.Time - (int)o.End;
                double s = dt <= 0 ? 0 : 1000 * Vector2.Distance(Pos(next), EndPos(o)) / (perSecond * dt);
                if (o.NewCombo || spacing < 0.01) spacing = s;
                else if (spacing > 0.01 && s > 0.01 && (s - spacing > spacing * 0.1 * lenience || spacing - s > spacing * 0.2 * lenience))
                    items.Add(Pair(o, next, 1, s < spacing ? "This object is too close to the previous object." : "This object is too far from the previous object."));
            }
            if ((o.HitSound & 14) != 0 || o.Edges.Split('|').Any(e => int.TryParse(e, out int h) && (h & 14) != 0)) hitsounds = true;
        }
        if (!hitsounds && objs.Count > 0) items.Add(new(C, 2, "There are no hitsounds on any objects. Hitsounds are required for rankable beatmaps."));
        AiComposeSnap(items);
    }

    // stable's abnormal slider: at some point of its path, everything over the four circle radii of length before it
    // stayed within a radius of that point (the path bunched up on itself)
    static bool AbnormalSlider(Vector2[] path, float radius)
    {
        int n = path.Length - 1; // (segment i: path[i] to path[i + 1])
        if (n < 1) return false;
        var length = new double[n];
        for (int i = 0; i < n; i++) length[i] = (i > 0 ? length[i - 1] : 0) + Vector2.Distance(path[i], path[i + 1]);
        int back = -1;
        for (int i = 0; i < n; i++)
        {
            while (back + 1 < n && length[back + 1] < length[i] - 4 * radius) back++; // (the last segment over 4 radii behind)
            int k = i;
            while (k >= Math.Max(0, back) && Vector2.Distance(path[i], path[k + 1]) < radius) k--;
            if (back >= 0 && k + 1 == back) return true;
        }
        return false;
    }

    // two objects: the item sits halfway between the first's end and the second's start
    static AiItem Pair(EObj a, EObj b, int level, string text) => new(AiTab.Compose, level, text, (int)(a.End - b.Time) / 2 + (int)b.Time, a, b);

    // objects closer than 10ms, and anything off the beat snap (all that taiko and catch check in Compose)
    void AiComposeSnap(List<AiItem> items)
    {
        const AiTab C = AiTab.Compose;
        for (int i = 0; i < objs.Count; i++)
        {
            var o = objs[i];
            if (i > 0 && i + 1 < objs.Count && o.Time != objs[i + 1].Time && o.Time < objs[i - 1].End + 10)
                items.Add(Pair(objs[i - 1], o, 2, "These two objects are less than 10ms apart!"));
            if (o.Slides > 1 && o.Slider)
            {
                for (int k = 1; k <= o.Slides; k++)
                {
                    double at = (int)(o.Time + (o.End - o.Time) * k / o.Slides);
                    if (!AiUnsnapped(at, 2)) continue;
                    int rare = AiRareSnap(o.End);
                    items.Add(rare > 0 ? new(C, 0, $"Rare snap divisor used on slider repeat (1/{rare})", o.End, o) : new(C, 1, "Object isn't snapped!", at, o));
                }
            }
            else if (AiUnsnapped(o.Time, 1) && !(o.Circle && i > 0 && o.Time - objs[i - 1].End < 50))
            {
                int rare = AiRareSnap(o.Time);
                items.Add(rare > 0 ? new(C, 0, $"Rare snap divisor used (1/{rare})", o.Time, o) : new(C, 1, "Object isn't snapped!", o.Time, o));
            }
            if (o.End == o.Time || !AiUnsnapped(o.End, 2)) continue;
            int r = AiRareSnap(o.End);
            items.Add(r > 0 ? new(C, 0, $"Rare snap divisor used on slider end (1/{r})", o.End, o) : new(C, 1, "Object's end is not snapped!", o.End, o));
        }
    }

    // ---- the window (stable's is a 424 x 446 Windows tool window, centred on the screen)
    const float AiW = 424, AiH = 446, AiCaption = 22;
    static float AiS => Game.H / 1080f * 1.25f; // (at Windows' common 125% scaling)
    Rectangle AiRect(float x, float y, float w, float h) { var p = aimod!.Pos; float s = AiS; return new Rectangle(p.X + x * s, p.Y + (AiCaption + y) * s, w * s, h * s); }
    Rectangle AiCaptionRect() { var p = aimod!.Pos; float s = AiS; return new Rectangle(p.X, p.Y, AiW * s, AiCaption * s); }
    static readonly string[] AiTabNames = ["All", "Compose", "Design", "Timing", "Meta", "Mapset"];
    const float AiRowH = 22, AiGridTop = 103, AiHeaderH = 23;
    int AiRows => (int)((AiH - AiGridTop - AiHeaderH) / AiRowH);

    string AiTabText(int i) => $"{AiTabNames[i]} ({(i == 0 ? aimod!.Items.Count : aimod!.Items.Count(x => (int)x.Tab == i))})";
    Rectangle AiTabRect(int i)
    {
        float x = 4;
        for (int k = 0; k < i; k++) x += Game.TextWidth(AiTabText(k), 11 * AiS) / AiS + 12;
        return AiRect(x, 83, Game.TextWidth(AiTabText(i), 11 * AiS) / AiS + 12, 20);
    }

    // true while the window has the mouse (the editor below doesn't get it)
    bool UpdateAiMod(Vector2 mouse)
    {
        var w = aimod!;
        float s = AiS;
        w.Mouse = mouse;
        if (float.IsNaN(w.Pos.X)) w.Pos = new((Game.W - AiW * s) / 2, (Game.H - (AiH + AiCaption) * s) / 2);
        if (Input.IsKeyPressed(KeyboardKey.Escape)) { aimod = null; Input.Muted = true; return true; }
        var whole = new Rectangle(w.Pos.X, w.Pos.Y, AiW * s, (AiH + AiCaption) * s);
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        if (w.Drag is { } d)
        {
            if (!Input.IsMouseButtonDown(MouseButton.Left)) w.Drag = null;
            else w.Pos = new(Math.Clamp(mouse.X - d.X, -AiW * s / 2, Game.W - AiW * s / 2), Math.Clamp(mouse.Y - d.Y, 0, Game.H - AiCaption * s));
            return true;
        }
        if (!CheckCollisionPointRec(mouse, whole)) return false;
        var cap = AiCaptionRect();
        if (click && CheckCollisionPointRec(mouse, new Rectangle(cap.X + cap.Width - 28 * s, cap.Y, 28 * s, cap.Height))) { aimod = null; return true; }
        if (click && CheckCollisionPointRec(mouse, cap)) { w.Drag = mouse - w.Pos; return true; }
        if (click && CheckCollisionPointRec(mouse, AiRect(9, 29, 91, 43))) { AiRefresh(); return true; }
        if (click && CheckCollisionPointRec(mouse, AiRect(116, 13, 145, 19))) { w.DistanceSnap = !w.DistanceSnap; AiRefresh(); return true; }
        for (int i = 0; i < AiTabNames.Length; i++)
            if (click && CheckCollisionPointRec(mouse, AiTabRect(i))) { w.Tab = (AiTab)i; w.Selected = -1; w.Scroll = 0; return true; }
        var shown = w.Shown.ToList();
        float wheel = Input.GetMouseWheelMove();
        if (wheel != 0) w.Scroll = Math.Clamp(w.Scroll - Math.Sign(wheel) * 3, 0, Math.Max(0, shown.Count - AiRows));
        for (int r = 0; r < AiRows && r + w.Scroll < shown.Count; r++)
        {
            if (!click || !CheckCollisionPointRec(mouse, AiRect(0, AiGridTop + AiHeaderH + r * AiRowH, AiW, AiRowH))) continue;
            w.Selected = r + w.Scroll;
            var item = shown[w.Selected];
            selected.Clear();
            foreach (var o in new[] { item.A, item.B }) if (o != null && objs.Contains(o)) selected.Add(o); // (not ones an undo or a delete replaced)
            if (item.Time >= 0) Seek(item.Time);
        }
        return true;
    }

    void DrawAiMod(float u)
    {
        var w = aimod!;
        float s = AiS;
        if (float.IsNaN(w.Pos.X)) return;
        var shown = w.Shown.ToList();
        // the selected problem's two objects: a line between them on the playfield (stable's red)
        if (tab == 0 && mode != 3 && w.Selected >= 0 && w.Selected < shown.Count && shown[w.Selected] is { A: { } a, B: { } b })
        {
            Vector2 from = ToScreen(EndPos(a).X, EndPos(a).Y), to = ToScreen(b.X, b.Y);
            DrawLineEx(from, to, 8 * u / 2, Game.NowMs % 1000 > 500 ? Color.Black : Color.White);
            DrawLineEx(from, to, 5 * u / 2, Color.Red);
        }
        Color face = new(240, 240, 240, 255), text = Color.Black, line = new(160, 160, 160, 255);
        float fs = 11 * s;
        var cap = AiCaptionRect();
        var whole = new Rectangle(w.Pos.X, w.Pos.Y, AiW * s, (AiH + AiCaption) * s);
        DrawRectangle((int)(whole.X + 4 * s), (int)(whole.Y + 4 * s), (int)whole.Width, (int)whole.Height, new Color(0, 0, 0, 70)); // (shadow)
        DrawRectangleRec(whole, face);
        DrawRectangleRec(cap, Color.White);
        Game.Text("AiMod", cap.X + 6 * s, cap.Y + (cap.Height - fs) / 2, fs, text);
        float cx = cap.X + cap.Width - 14 * s, cy = cap.Y + cap.Height / 2;
        DrawLineEx(new(cx - 4 * s, cy - 4 * s), new(cx + 4 * s, cy + 4 * s), 1.2f * s, text);
        DrawLineEx(new(cx - 4 * s, cy + 4 * s), new(cx + 4 * s, cy - 4 * s), 1.2f * s, text);
        DrawRectangleLinesEx(whole, 1, line);

        // Refresh, the distance snap box and the summary
        var mouse = w.Mouse;
        var refresh = AiRect(9, 29, 91, 43);
        DrawRectangleRec(refresh, CheckCollisionPointRec(mouse, refresh) ? new Color(229, 241, 251, 255) : new Color(225, 225, 225, 255));
        DrawRectangleLinesEx(refresh, 1, CheckCollisionPointRec(mouse, refresh) ? new Color(0, 120, 215, 255) : new Color(173, 173, 173, 255));
        Game.Text("Refresh", refresh.X + refresh.Width / 2 - Game.TextWidth("Refresh", fs) / 2, refresh.Y + (refresh.Height - fs) / 2, fs, text);
        var box = AiRect(116, 15, 13, 13);
        DrawRectangleRec(box, Color.White);
        DrawRectangleLinesEx(box, 1, new Color(51, 51, 51, 255));
        if (w.DistanceSnap)
        {
            DrawLineEx(new(box.X + 3 * s, box.Y + 6.5f * s), new(box.X + 5.5f * s, box.Y + 9.5f * s), 1.5f * s, text);
            DrawLineEx(new(box.X + 5.5f * s, box.Y + 9.5f * s), new(box.X + 10 * s, box.Y + 3.5f * s), 1.5f * s, text);
        }
        Game.Text("Check distance snap", box.X + 17 * s, box.Y + (box.Height - fs) / 2, fs, text);
        var group = AiRect(278, 7, 133, 69);
        DrawRectangleLinesEx(group, 1, new Color(220, 220, 220, 255));
        int errors = w.Items.Count(i => i.Level == 2), warnings = w.Items.Count(i => i.Level == 1);
        void Row(float y, string label, string value, Color c)
        {
            Game.Text(label, group.X + 2 * s, group.Y + y * s, fs, text);
            Game.TextBold(value, group.X + 63 * s, group.Y + y * s, fs, c);
        }
        Row(15, "Difficulty:", w.Difficulty, new Color(16, 112, 16, 255));
        Row(30, "Warnings:", warnings.ToString(), warnings > 0 ? new Color(120, 80, 16, 255) : text);
        Row(45, "Errors:", errors.ToString(), errors > 0 ? new Color(136, 16, 16, 255) : text);

        // the tabs
        for (int i = 0; i < AiTabNames.Length; i++)
        {
            var r = AiTabRect(i);
            bool on = (int)w.Tab == i;
            if (on) r = new Rectangle(r.X - 2 * s, r.Y - 2 * s, r.Width + 4 * s, r.Height + 2 * s);
            DrawRectangleRec(r, on ? Color.White : CheckCollisionPointRec(mouse, r) ? new Color(229, 241, 251, 255) : face);
            DrawRectangleLinesEx(r, 1, new Color(217, 217, 217, 255));
            Game.Text(AiTabText(i), r.X + 6 * s, r.Y + (r.Height - fs) / 2, fs, text);
        }

        // the list: icon, Information, Time
        var grid = AiRect(0, AiGridTop, AiW, AiH - AiGridTop);
        DrawRectangleRec(grid, new Color(171, 171, 171, 255));
        float[] cols = [30, 299, 95];
        var header = AiRect(0, AiGridTop, AiW, AiHeaderH);
        DrawRectangleRec(header, Color.White);
        float hx = header.X;
        string[] heads = ["", "Information", "Time"];
        for (int c = 0; c < 3; c++)
        {
            Game.Text(heads[c], hx + 5 * s, header.Y + (header.Height - fs) / 2, fs, text);
            hx += cols[c] * s;
            DrawLineEx(new(hx, header.Y), new(hx, header.Y + header.Height), 1, new Color(229, 229, 229, 255));
        }
        DrawLineEx(new(header.X, header.Y + header.Height), new(header.X + header.Width, header.Y + header.Height), 1, line);
        List<AiItem> rows = shown.Count == 0 && w.Tab == AiTab.All ? [new AiItem(AiTab.All, 0, "No problems were found in this map!")] : shown;
        for (int r = 0; r < AiRows && r + w.Scroll < rows.Count; r++)
        {
            var item = rows[r + w.Scroll];
            var rr = AiRect(0, AiGridTop + AiHeaderH + r * AiRowH, AiW, AiRowH);
            bool sel = r + w.Scroll == w.Selected && shown.Count > 0;
            DrawRectangleRec(rr, sel ? new Color(0, 120, 215, 255) : Color.White);
            DrawLineEx(new(rr.X, rr.Y + rr.Height), new(rr.X + rr.Width, rr.Y + rr.Height), 1, new Color(229, 229, 229, 255));
            AiIcon(new(rr.X + cols[0] * s / 2, rr.Y + rr.Height / 2), item.Level, s);
            string t = item.Text;
            float maxW = (cols[1] - 8) * s;
            while (t.Length > 4 && Game.TextWidth(t, fs) > maxW) t = t[..^5] + "...";
            Game.Text(t, rr.X + (cols[0] + 4) * s, rr.Y + (rr.Height - fs) / 2, fs, sel ? Color.White : text);
            if (item.Time >= 0)
            {
                int ms = (int)item.Time;
                string time = $"{ms / 60000:00}:{ms % 60000 / 1000:00}:{Math.Max(0, ms) % 1000:000}";
                float tx = rr.X + (cols[0] + cols[1] + 4) * s;
                Color link = sel ? Color.White : new Color(0, 0, 255, 255);
                Game.Text(time, tx, rr.Y + (rr.Height - fs) / 2, fs, link);
                DrawLineEx(new(tx, rr.Y + (rr.Height + fs) / 2), new(tx + Game.TextWidth(time, fs), rr.Y + (rr.Height + fs) / 2), 1, link);
            }
        }
        if (rows.Count > AiRows) // (a scroll bar)
        {
            var bar = AiRect(AiW - 6, AiGridTop + AiHeaderH, 5, AiH - AiGridTop - AiHeaderH);
            float f = (float)AiRows / rows.Count, at = (float)w.Scroll / rows.Count;
            DrawRectangleRec(new Rectangle(bar.X, bar.Y + bar.Height * at, bar.Width, bar.Height * f), new Color(150, 150, 150, 255));
        }
    }

    // stable's three icons: a red cross, a yellow warning triangle, a blue "i"
    static void AiIcon(Vector2 c, int level, float s)
    {
        float r = 7 * s;
        switch (level)
        {
            case 2:
                DrawCircleV(c, r, new Color(212, 40, 40, 255));
                DrawLineEx(c - new Vector2(3, 3) * s, c + new Vector2(3, 3) * s, 1.6f * s, Color.White);
                DrawLineEx(c + new Vector2(-3, 3) * s, c + new Vector2(3, -3) * s, 1.6f * s, Color.White);
                break;
            case 1:
                DrawTriangle(c + new Vector2(0, -r), c + new Vector2(-r, r * 0.8f), c + new Vector2(r, r * 0.8f), new Color(250, 200, 30, 255));
                DrawLineEx(c + new Vector2(0, -3.5f) * s, c + new Vector2(0, 1.5f) * s, 1.6f * s, Color.Black);
                DrawCircleV(c + new Vector2(0, 3.6f) * s, 0.9f * s, Color.Black);
                break;
            default:
                DrawCircleV(c, r, new Color(40, 110, 220, 255));
                DrawCircleV(c + new Vector2(0, -3.5f) * s, 1 * s, Color.White);
                DrawLineEx(c + new Vector2(0, -1.5f) * s, c + new Vector2(0, 4) * s, 1.8f * s, Color.White);
                break;
        }
    }
}
