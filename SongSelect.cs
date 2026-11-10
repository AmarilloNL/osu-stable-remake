using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

sealed class BeatmapSet(List<DbBeatmap> diffs)
{
    public readonly List<DbBeatmap> Diffs = diffs;
    public DbBeatmap First => Diffs[0];
}

sealed partial class SongSelect : Screen
{
    Task<List<BeatmapSet>>? loading;
    string? reselect; // F5: the selection to find again in the re-read library
    List<BeatmapSet> sets = [];
    readonly List<(BeatmapSet Set, List<DbBeatmap> Diffs)> view = []; // after search filter
    string filter = "", message = "";
    int setIndex, diffIndex;
    float scrollRow;
    Mods mods;
    bool modSelect;

    // Preview music: starts once the selection has rested briefly, like stable.
    Track? preview;
    (string Folder, string Audio, double Rate, bool Nightcore)? previewKey; // same song = no restart across difficulties
    double selectionChangedAt;
    bool themeFromStart;

    // Layout in stable's 768px-high units.
    static float S => Game.H / 768f;
    static float RowH => 76.8f * S; // stable's panel spacing: 144px at 1440p (measured on p057/p059)
    static float TopH => 100 * S;
    static float BottomH => 80 * S;

    // Gameplay's sprites decode in the background while a map is picked (and upload a frame at a time in Update).
    static bool starRangeWarned;

    public override void Entered()
    {
        // stable, once a session: a star range other than the full one says so; a click opens the setting
        if (!starRangeWarned && (Config.GetDouble("DisplayStarsMinimum", 0) != 0 || Config.GetDouble("DisplayStarsMaximum", 10) != 10))
        {
            starRangeWarned = true;
            Toast.Show("Beatmaps are currently filtered by difficulty.\nClick here to change the range.", new Color(245, 245, 220, 255), 10000,
                () => { if (Game.Screen == this) Game.Screen = new Options(this, this, "SONG SELECT"); });
        }
        headerFor = default; // (a play may have changed the map's local offset)
        fadeFor = null; // stable fades the header in on entering too
        Game.Skin.PrefetchGameplay();
        if (Game.Previous is not Options) SlideIn(); // stable's options are an overlay; anything else re-enters song select
    }

    internal static SongSelect? Instance; // the one song select (dropped files need it from any screen)

    public SongSelect(string filter = "", Mods mods = Mods.None)
    {
        Instance = this;
        this.filter = filter;
        this.mods = mods;
        gradeLookup = BestGrade;
        loading = Task.Run(LoadLibrary);
        scoresLoading = Task.Run(Replays.Index);
    }

    // Local leaderboard (saved replays), keyed by beatmap MD5.
    Task<Dictionary<string, List<ReplayInfo>>>? scoresLoading;
    Dictionary<string, List<ReplayInfo>> scores = [];
    static readonly List<ReplayInfo> NoScores = []; // read-only: returned when a map has no scores
    List<ReplayInfo> SelectedScores => Selected is { } b && scores.TryGetValue(ScoreKey(b), out var l) ? l : NoScores;
    // osu!.db ranked status (4 ranked, 5 approved, 6 qualified, 7 loved, ...); null until the library has loaded.
    internal byte? StatusOf(string md5)
    {
        if (Selected is { } sel && sel.Md5 == md5) return sel.Status; // the usual case; the scan below is ~16ms
        var lib = loading == null ? sets : loading.IsCompleted ? loading.Result : null; // no side effects: Ambient() adopts it
        return lib?.SelectMany(s => s.Diffs).FirstOrDefault(d => d.Md5 == md5)?.Status ?? (lib == null ? null : (byte)0);
    }

    internal List<ReplayInfo> ScoresFor(string md5)
    {
        PollScores();
        return scores.TryGetValue(md5, out var l) ? l : [];
    }

    // The local leaderboard loads in the background; picked up whenever it's finished.
    void PollScores()
    {
        if (scoresLoading is not { IsCompleted: true }) return;
        foreach (var (md5, list) in scoresLoading.Result)
            foreach (var info in list) AddScore(info);
        scoresLoading = null;
        if (GroupMode == "Recent" && sets.Count > 0) ApplyFilter(); // the groups count this client's plays too
    }

    public void AddScore(ReplayInfo info)
    {
        if (Cleared(info)) return;
        rankedFor = null; // By Rank Achieved may change
        string key = ScoreKey(info.BeatmapMd5, info.Mode);
        if (!scores.TryGetValue(key, out var list)) scores[key] = list = [];
        list.Add(info);
        // score, then name (stable's scoreboard order); equal ones keep their order
        var sorted = list.OrderByDescending(s => s.Score).ThenBy(s => s.Player, StringComparer.CurrentCulture).ToList();
        list.Clear();
        list.AddRange(sorted);
    }

    void ShowScore(ReplayInfo info)
    {
        if (Selected is not { } b) return;
        try
        {
            Game.Skin.Sound("menuhit")?.Play(BeatmapSamples.EffectVolume);
            Game.Screen = Player.Summary(Beatmap.Load(b.FilePath, mods: info.Mods), this, info);
        }
        catch (Exception e) when (e is not OutOfMemoryException) // a malformed map or replay mustn't take the game down
        {
            message = $"Can't show score: {e.Message}";
        }
    }

    void Watch(ReplayInfo info)
    {
        if (Selected is not { } b) return;
        try
        {
            var map = Beatmap.Load(b.FilePath, mods: info.Mods);
            StopPreview();
            Game.Screen = new Player(map, this, info.Mods, Replays.Frames(info), info);
        }
        catch (Exception e) when (e is not OutOfMemoryException) // a malformed map or replay mustn't take the game down
        {
            message = $"Can't play replay: {e.Message}";
        }
    }

    // what the library load is doing, for the loading screen (set by the loading task)
    static volatile string loadStage = "Loading osu!.db…";
    static int loadDone, loadTotal; // (read by the loading screen while the load runs)
    // maps read from their .osu that still need their star ratings (worked out in the background once the list is up)
    static List<DbBeatmap> unrated = [];
    static List<(string, long, long, DbBeatmap)> cacheEntries = [];
    Task? rating;
    static Task RunLowPriority(Action work)
    {
        var done = new TaskCompletionSource();
        new Thread(() => { try { work(); } finally { done.SetResult(); } }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
        return done.Task;
    }

    static List<BeatmapSet> LoadLibrary()
    {
        loadStage = "Loading osu!.db…";
        loadTotal = 0;
        var clock = Stopwatch.StartNew();
        List<DbBeatmap> all;
        try { all = OsuDb.Load(Path.Combine(Config.OsuDir, "osu!.db")); Console.Error.WriteLine($"osu!.db: {all.Count} maps in {clock.ElapsedMilliseconds}ms"); }
        catch (Exception e) when (e is not OutOfMemoryException) // missing, unreadable or a format we don't know
        {
            Console.Error.WriteLine(e is FileNotFoundException ? "osu!.db: none" : $"osu!.db: {e}");
            all = []; // then every Songs folder is new below: read in parallel, cached for the next start, rated in the background
        }
        var dirs = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase); // Songs folder -> last change
        bool listed = false;
        try
        {
            if (Directory.Exists(Config.SongsDir))
            {
                foreach (var d in new DirectoryInfo(Config.SongsDir).EnumerateDirectories()) dirs[d.Name] = d.LastWriteTimeUtc;
                listed = true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"Songs folder: {e.Message}"); }
        // maps deleted since stable last wrote its osu!.db (it does when it closes; the remake's Delete doesn't touch it):
        // folders that are gone, and in folders changed since then, difficulties whose file is gone
        if (listed && all.Count > 0)
        {
            var written = File.GetLastWriteTimeUtc(Path.Combine(Config.OsuDir, "osu!.db"));
            int before = all.Count;
            all = all.Where(b => dirs.TryGetValue(b.Folder, out var changed) ? changed <= written || File.Exists(b.FilePath) : b.Folder.IndexOfAny(['/', '\\']) >= 0 && File.Exists(b.FilePath)).ToList();
            if (all.Count < before) Console.Error.WriteLine($"osu!.db: {before - all.Count} maps no longer on disk");
        }
        // like stable after an import (or a folder copied in): Songs folders its osu!.db doesn't list yet are read too
        var known = new HashSet<string>(all.Select(b => b.Folder), StringComparer.OrdinalIgnoreCase);
        var fresh = dirs.Keys.Where(d => !known.Contains(d) && !d.Equals("Failed", StringComparison.OrdinalIgnoreCase)).Select(d => Path.Combine(Config.SongsDir, d)).ToList();
        var newMaps = new List<DbBeatmap>();
        var toRate = new List<DbBeatmap>();
        var cache = fresh.Count > 0 ? OsuDb.LoadMapCache() : new Dictionary<string, (long Length, long Ticks, DbBeatmap Map)>();
        var keyed = new List<(string, long, long, DbBeatmap)>();
        if (fresh.Count > 0) { loadStage = "Reading new beatmaps…"; loadDone = 0; loadTotal = fresh.Count; }
        // folders in parallel: reading the .osu text is the cost (~1.5ms a map), and they don't depend on each other
        var gate = new object();
        Parallel.ForEach(fresh, dir =>
        {
            var mine = new List<(string Key, long Length, long Ticks, DbBeatmap Map, bool Rate)>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.osu"))
                {
                    var fi = new FileInfo(file);
                    string key = Path.GetFileName(dir) + "/" + fi.Name;
                    long ticks = fi.LastWriteTimeUtc.Ticks;
                    DbBeatmap? b = cache.TryGetValue(key, out var c) && c.Length == fi.Length && c.Ticks == ticks ? c.Map : null;
                    bool rate = b == null;
                    b ??= ReadMap(file, stars: false);
                    if (b != null) mine.Add((key, fi.Length, ticks, b, rate));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            lock (gate)
                foreach (var (key, length, ticks, b, rate) in mine)
                {
                    newMaps.Add(b);
                    keyed.Add((key, length, ticks, b));
                    if (rate) toRate.Add(b);
                }
            Interlocked.Increment(ref loadDone);
        });
        cacheEntries = keyed;
        if (fresh.Count > 0) Console.Error.WriteLine($"{fresh.Count} folders osu!.db doesn't list ({string.Join(", ", fresh.Take(5).Select(Path.GetFileName))}{(fresh.Count > 5 ? ", ..." : "")}): {newMaps.Count} maps");
        Console.Error.WriteLine($"library read in {clock.ElapsedMilliseconds}ms");
        all.AddRange(newMaps);
        unrated = toRate;
        TotalBeatmaps = all.Count;
        for (int i = 0; i < all.Count; i++) all[i].BuildSearchKey(i > 0 ? all[i - 1] : null); // here, not on the first keystroke
        return [.. all.Where(b => b.Mode <= 3)
            .GroupBy(b => b.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BeatmapSet(g.OrderBy(b => b.StarsFor(Mods.None)).ToList()))];
    }

    // stable's sort modes (TreeSortMode2 in its cfg; ours is saved to remake.cfg).
    static readonly (string Key, string Label)[] SortModes =
        [("Artist", "By Artist"), ("Bpm", "By BPM"), ("Creator", "By Creator"), ("Date", "By Date Added"), ("Difficulty", "By Difficulty"), ("Length", "By Length"), ("Rank", "By Rank Achieved"), ("Title", "By Title")];
    static string SortMode => Config.Get("TreeSortMode2", "Title"); // stable's default
    internal static int TotalBeatmaps; // every mode, as stable's menu counts them

    // By Rank Achieved: the difficulty's best local grade (song select fills the lookup in)
    static Func<DbBeatmap, string?>? gradeLookup;
    static readonly string[] GradeRanks = ["XH", "SH", "X", "S", "A", "B", "C", "D"];
    static int GradeIndex(DbBeatmap d) => gradeLookup?.Invoke(d) is { } g && Array.IndexOf(GradeRanks, g) is int i and >= 0 ? i : GradeRanks.Length;

    // stable sorts the difficulties themselves, not sets: by the sort's key, then folder, stars and version. A set's
    // difficulties stay together unless the key differs between them (stars, length, BPM), when other sets' come between.
    static Comparison<DbBeatmap> DiffOrder(Mods mods)
    {
        var text = StringComparer.CurrentCulture;
        Comparison<DbBeatmap> key = SortMode.ToLowerInvariant() switch // stable writes BPM, ours Bpm
        {
            "title" => (a, b) => text.Compare(a.Title, b.Title),
            "creator" => (a, b) => text.Compare(a.Creator, b.Creator),
            "bpm" => (a, b) => a.MaxBpm.CompareTo(b.MaxBpm),
            "length" => (a, b) => (a.TotalMs / 1000).CompareTo(b.TotalMs / 1000),
            "rank" => (a, b) => GradeIndex(a).CompareTo(GradeIndex(b)), // best local grade first, unplayed last
            "difficulty" => (a, b) => a.StarsFor(mods).CompareTo(b.StarsFor(mods)),
            // a set's difficulties added within 10 seconds of each other count as one import
            "date" => (a, b) => a.Folder != b.Folder || Math.Abs(a.LastModified - b.LastModified) > 10 * TimeSpan.TicksPerSecond ? a.LastModified.CompareTo(b.LastModified) : 0,
            _ => (a, b) => text.Compare(a.Artist, b.Artist) is var c and not 0 ? c : text.Compare(a.Title, b.Title),
        };
        return (a, b) =>
        {
            int c = key(a, b);
            if (c == 0) c = text.Compare(a.Folder, b.Folder);
            if (c == 0) c = a.StarsFor(mods).CompareTo(b.StarsFor(mods));
            return c != 0 ? c : text.Compare(a.Version, b.Version);
        };
    }

    void CycleSort(int dir)
    {
        int i = Array.FindIndex(SortModes, m => m.Key.Equals(SortMode, StringComparison.OrdinalIgnoreCase));
        Config.SetOwn("TreeSortMode2", SortModes[((i < 0 ? 0 : i) + dir + SortModes.Length) % SortModes.Length].Key);
        ApplyFilter();
        Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
    }

    // A library entry read from the .osu itself, with what stable's database records on import (counts, drain and
    // total time, BPMs, date added, and the star ratings for every EZ/HR/DT/HT combination, from StarRating).
    static readonly Mods[] RatedMods = [Mods.None, Mods.HardRock, Mods.Easy, Mods.DoubleTime, Mods.HalfTime,
        Mods.HardRock | Mods.DoubleTime, Mods.Easy | Mods.DoubleTime, Mods.HardRock | Mods.HalfTime, Mods.Easy | Mods.HalfTime];

    internal static DbBeatmap? ReadMap(string file, bool stars = true)
    {
        try
        {
            var m = Beatmap.Load(file);
            var objs = m.Objects;
            var bpms = m.TimingPoints.Where(t => t.Uninherited && t.BeatLength > 0).OrderBy(t => t.Time).ToList();
            // stable: the total time runs to the last object's start (a spinner's end); the main BPM is the one held
            // longest in all (checked against osu!.db with --check-readmap)
            double end = objs.Count == 0 ? 0 : objs[^1].Type == ObjType.Spinner ? objs[^1].EndTime : objs[^1].Time, mainBpm = 0;
            var held = new Dictionary<double, double>();
            for (int i = 0; i < bpms.Count; i++)
            {
                double from = i == 0 ? 0 : bpms[i].Time, to = i + 1 < bpms.Count ? bpms[i + 1].Time : end;
                held[bpms[i].BeatLength] = held.GetValueOrDefault(bpms[i].BeatLength) + Math.Max(0, to - from);
            }
            if (held.Count > 0) mainBpm = 60000 / held.MaxBy(kv => kv.Value).Key;
            var entry = new DbBeatmap
            {
                Artist = m.Artist, ArtistUnicode = m.ArtistUnicode, Title = m.Title, TitleUnicode = m.TitleUnicode, Creator = m.Creator,
                Version = m.Version, Source = m.Source, Tags = m.Tags, AudioFile = m.AudioFilename,
                OsuFile = Path.GetFileName(file), Folder = Path.GetFileName(Path.GetDirectoryName(file)) ?? "", Mode = (byte)m.Mode,
                Md5 = Replays.Md5Of(file), AR = (float)m.AR, CS = (float)m.CS, OD = (float)m.OD, HP = (float)m.HP,
                PreviewMs = (int)m.PreviewTime, BeatmapId = m.BeatmapId, SetId = m.SetId,
                Circles = objs.Count(o => o.Type == ObjType.Circle), Sliders = objs.Count(o => o.Type == ObjType.Slider),
                Spinners = objs.Count(o => o.Type == ObjType.Spinner),
                DrainSeconds = objs.Count > 0 ? ((int)objs[^1].Time - (int)objs[0].Time - m.Breaks.Sum(b => (int)b.End - (int)b.Start)) / 1000 : 0,
                TotalMs = (int)end, MinBpm = bpms.Count > 0 ? bpms.Min(t => 60000 / t.BeatLength) : 0,
                MaxBpm = bpms.Count > 0 ? bpms.Max(t => 60000 / t.BeatLength) : 0, MainBpm = mainBpm,
                LastModified = DateTime.Now.Ticks, Unplayed = true, Status = (byte)(m.SetId <= 0 ? 1 : 0), Scanned = true,
            };
            if (stars) RateStars(entry, m);
            return entry;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Console.Error.WriteLine($"unreadable map {file}: {e.GetType().Name}"); return null; } // a malformed map mustn't stop the library
    }

    // the star ratings stable keeps for a map (every EZ/HR/DT/HT combination)
    static void RateStars(DbBeatmap entry, Beatmap? nomod = null)
    {
        if (entry.Mode != 0) return;
        try
        {
            foreach (var mods in RatedMods)
                entry.SetStars((int)mods, (float)StarRating.Compute(mods == Mods.None && nomod != null ? nomod : Beatmap.Load(entry.FilePath, mods: mods), mods));
        }
        catch (Exception e) when (e is not OutOfMemoryException) { } // (on a background thread: anything uncaught would end the game)
    }

    // the library entry with this MD5, if it's loaded
    internal DbBeatmap? FindMap(string md5) => Lib.SelectMany(s => s.Diffs).FirstOrDefault(d => d.Md5 == md5);
    // the library, also when it finished loading while another screen was up (song select adopts it when it's next shown)
    List<BeatmapSet> Lib => loading is { IsCompletedSuccessfully: true } l ? l.Result : sets;

    // re-reads the library (osu!.db plus new folders), then selects that map (or keeps the selection)
    internal void Reload(string? select)
    {
        reselect = select ?? Selected?.Md5;
        loading = Task.Run(LoadLibrary);
    }

    internal bool LibraryLoading => loading != null;
    internal static bool SongsPending => songsSeen.Count > 0; // the Songs folder changed since the library was read

    // test hook (--count-search): the number of difficulties each search finds, like stable's "N matches found"
    internal IEnumerable<(string Query, int Matches)> CountMatches(IEnumerable<string> queries)
    {
        sets = loading!.Result;
        loading = null;
        scoresLoading?.Wait();
        PollScores();
        foreach (var q in queries)
        {
            filter = q;
            ApplyFilter();
            yield return (q, matchCount);
        }
    }

    // ---------------------------------------------------------------- filtering

    // ---- stable's search: the text splits on spaces (a value after '=' may be quoted to keep its spaces); every term
    // must match. key<op>value terms (op < > = == <= >= !=) filter on: ar cs od hp (one decimal), stars/star (floored to
    // two), bpm (the main one), length (total seconds), drain, speed (mania scroll speed), key/keys (mania keys, converted
    // for osu! maps), played (days since the last play), unplayed=, mode, status (names by prefix), and text: artist,
    // creator, title, difficulty (= contains, != doesn't). A whole number also matches beatmap / set IDs; anything else
    // (unknown keys included) is text found in the artist, title, creator, source, tags or difficulty name.
    static readonly System.Text.RegularExpressions.Regex TermRx = new(@"^(\w*)([<>=]=?|!=)(.*)$"),
        NumberRx = new(@"^[+-]?(?:\d+\.?|\.\d)\d*(?:e[+-]?\d+)?$");
    static readonly (double Value, string Name)[] ModeNames = [(0, "osu!"), (1, "taiko"), (2, "catchthebeat"), (2, "ctb"), (3, "osu!mania"), (3, "osumania"), (3, "mania"), (3, "o!m")];
    static readonly (double Value, string Name)[] StatusNames = [(0, "unknown"), (1, "notsubmitted"), (2, "pending"), (4, "ranked"), (5, "approved"), (6, "qualified"), (7, "loved")];

    static List<string> SearchTerms(string text)
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        int quotes = text.Count(c => c == '"') & ~1, seen = 0; // an unpaired last quote is plain text
        bool inQuotes = false;
        char prev = ' ';
        foreach (char c in text)
        {
            if (c != ' ' || inQuotes)
            {
                if (c == '"' && seen < quotes && (inQuotes || prev == '=')) { inQuotes = !inQuotes; seen++; }
                else sb.Append(c);
            }
            else
            {
                if (sb.Length > 0) list.Add(sb.ToString());
                sb.Clear();
            }
            prev = c;
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    static bool Compare<T>(T a, string op, T b) where T : IComparable<T>
    {
        int c = a.CompareTo(b);
        return op switch { "<" => c < 0, ">" => c > 0, "==" => c == 0, ">=" => c >= 0, "<=" => c <= 0, "!=" => c != 0, _ => false };
    }

    static double ByPrefix((double Value, string Name)[] names, string value)
    {
        if (value.Length == 0) return double.NaN;
        foreach (var (v, name) in names) if (name.StartsWith(value, StringComparison.Ordinal)) return v;
        return double.NaN;
    }

    // stable's mania key count: a mania map's CS; for an osu! map from the share of sliders and spinners, CS and OD
    static int ManiaKeys(DbBeatmap b)
    {
        if (b.Mode == 3) return (int)Math.Round(b.CS);
        double ratio = (double)(b.Sliders + b.Spinners) / Math.Max(1, b.Circles + b.Sliders + b.Spinners); // (stable divides in double)
        if (ratio < 0.2) return 7;
        if (ratio < 0.3 || Math.Round(b.CS) >= 5) return Math.Round(b.OD) > 5 ? 7 : 6;
        if (ratio > 0.6) return Math.Round(b.OD) > 4 ? 5 : 4;
        return Math.Max(4, Math.Min((int)Math.Round(b.OD) + 1, 7));
    }

    // when the map was last played: stable's record or this client's latest saved replay, whichever is newer
    internal DateTime? LastPlayedUtc(DbBeatmap d)
    {
        DateTime? at = d.Unplayed || d.LastPlayed <= 0 ? null : new DateTime(d.LastPlayed, DateTimeKind.Utc);
        if (scores.TryGetValue(d.Md5, out var local) && local.Count > 0 && local.Max(r => r.Date).ToUniversalTime() is var mine && (at == null || mine > at)) at = mine;
        return at;
    }

    Func<DbBeatmap, bool> SearchTerm(string term)
    {
        string lower = term.ToLowerInvariant();
        bool Text(DbBeatmap b) => b.SearchMatches(lower);
        var m = TermRx.Match(term);
        if (m.Success)
        {
            string key = m.Groups[1].Value.ToLowerInvariant(), op = m.Groups[2].Value == "=" ? "==" : m.Groups[2].Value, value = m.Groups[3].Value.ToLowerInvariant();
            if (NumberRx.IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                switch (key)
                {
                    case "cs": return b => Compare(Math.Round(b.CS, 1), op, v);
                    case "hp": return b => Compare(Math.Round(b.HP, 1), op, v);
                    case "od": return b => Compare(Math.Round(b.OD, 1), op, v);
                    case "ar": return b => Compare(Math.Round(b.AR, 1), op, v);
                    case "stars" or "star": return b => Compare(Math.Floor(b.StarsFor(mods) * 100.0) / 100, op, v);
                    case "bpm": return b => Compare(Math.Round(b.MainBpm), op, v);
                    case "length": return b => Compare((double)(b.TotalMs / 1000), op, v);
                    case "drain": return b => Compare((double)b.DrainSeconds, op, v);
                    case "speed": return b => Compare((double)b.ManiaSpeed, op, v);
                    case "key" or "keys": return b => Compare((double)ManiaKeys(b), op, v);
                    case "played":
                        double maxBack = (DateTime.UtcNow - DateTime.MinValue).TotalDays, maxAhead = (DateTime.MaxValue - DateTime.UtcNow).TotalDays;
                        var since = v >= maxBack ? DateTime.MinValue : -v >= maxAhead ? DateTime.MaxValue : DateTime.UtcNow.AddDays(-v);
                        return b => Compare(since, op, LastPlayedUtc(b) ?? DateTime.MinValue);
                }
            }
            bool contains = op == "==";
            switch (key)
            {
                case "unplayed" when value.Length == 0: return b => LastPlayedUtc(b) == null && BestGrade(b) == null;
                case "mode": { double mode = ByPrefix(ModeNames, value); return b => Compare((double)b.Mode, op, mode); }
                case "status":
                    double status = ByPrefix(StatusNames, value);
                    if (status == 4) // ranked takes approved along
                    {
                        if (op is ">" or "<=") status = 5;
                        else if (op == "==") return b => b.Status is 4 or 5;
                        else if (op == "!=") return b => b.Status is not (4 or 5);
                    }
                    return b => Compare((double)b.Status, op, status);
                case "creator": return b => b.Creator.Contains(value, StringComparison.OrdinalIgnoreCase) == contains;
                case "artist": return b => (b.Artist.Contains(value, StringComparison.OrdinalIgnoreCase) || b.ArtistUnicode.Contains(value, StringComparison.OrdinalIgnoreCase)) == contains;
                case "title": return b => b.Title.Contains(value, StringComparison.OrdinalIgnoreCase) == contains;
                case "difficulty": return b => b.Version.Contains(value, StringComparison.OrdinalIgnoreCase) == contains;
            }
        }
        if (int.TryParse(term, out int id)) return b => b.BeatmapId == id || b.SetId == id || Text(b);
        return Text;
    }

    // this difficulty's osu!.db entry (null before osu!.db has loaded)
    internal DbBeatmap? DbEntry(string md5)
    {
        foreach (var set in sets) foreach (var b in set.Diffs) if (b.Md5 == md5) return b;
        return null;
    }

    // stable's local offset for this difficulty (0 before osu!.db has loaded)
    internal short LocalOffsetOf(string md5) => DbEntry(md5)?.LocalOffset ?? 0;
    internal short OnlineOffsetOf(string md5) => DbEntry(md5)?.OnlineOffset ?? 0;

    // stable's mod caption: the selected mods' names (in its order, "," between) in size 30 at (64.4, 397), 0.5 apart, white at
    // quarter alpha, under the panels
    static readonly (Mods Flag, string Name)[] CaptionOrder =
    [
        (Mods.ScoreV2, "ScoreV2"), (Mods.Cinema, "Cinema"), (Mods.Autoplay, "Autoplay"), (Mods.SpunOut, "SpunOut"), (Mods.Easy, "Easy"), (Mods.NoFail, "NoFail"),
        (Mods.Hidden, "Hidden"), (Mods.Nightcore, "Nightcore"), (Mods.DoubleTime, "DoubleTime"), (Mods.HalfTime, "HalfTime"),
        (Mods.HardRock, "HardRock"), (Mods.Relax, "Relax"), (Mods.Autopilot, "AutoPilot"), (Mods.Perfect, "Perfect"),
        (Mods.SuddenDeath, "SuddenDeath"), (Mods.Flashlight, "Flashlight"),
    ];
    Mods captionMods = (Mods)(-1);
    string caption = "";

    void DrawModCaption()
    {
        if (mods != captionMods)
        {
            captionMods = mods;
            caption = string.Join(",", CaptionOrder.Where(c => mods.HasFlag(c.Flag) && !(c.Flag == Mods.Autoplay && mods.HasFlag(Mods.Cinema))
                && !(c.Flag == Mods.DoubleTime && mods.HasFlag(Mods.Nightcore)) && !(c.Flag == Mods.SuddenDeath && mods.HasFlag(Mods.Perfect))).Select(c => c.Name));
        }
        if (caption.Length == 0) return;
        float u = Game.H / 480f;
        Game.Text(caption, 64.4f * u, 397 * u, 30 * u, new Color(255, 255, 255, 64), 0.5f * u);
    }

    internal void ApplyFilter(DbBeatmap? select = null)
    {
        var keep = select != null ? sets.FirstOrDefault(s => s.Diffs.Contains(select)) : view.Count > 0 && setIndex < view.Count ? view[setIndex].Set : null;
        var keepDiff = select ?? Selected;
        var all = new List<(BeatmapSet Set, List<DbBeatmap> Diffs)>();
        var terms = SearchTerms(filter).Select(SearchTerm).ToArray();
        // the options' "Display beatmaps from .. up to .. stars" (10 = no upper limit)
        double lo = Config.GetDouble("DisplayStarsMinimum", 0), hi = Config.GetDouble("DisplayStarsMaximum", 10);
        bool starRange = lo > 0 || hi < 10;
        bool InRange(DbBeatmap b) => !starRange || b.StarsFor(mods) is var st && st >= lo && (hi >= 10 || st <= hi);
        foreach (var set in sets)
        {
            var diffs = filter.Length == 0 && !starRange ? set.Diffs : set.Diffs.Where(b => InRange(b) && terms.All(t => t(b))).ToList();
            if (diffs.Count > 0) all.Add((set, diffs));
        }
        BuildGroups(all);
        int g = keepDiff == null ? -1 : groups.FindIndex(gr => gr.Entries.Any(e => e.Diffs.Contains(keepDiff)));
        if (g < 0 && keep != null) g = groups.FindIndex(gr => gr.Entries.Any(e => e.Set == keep));
        groupIndex = g >= 0 ? g : Math.Clamp(groupIndex, 0, Math.Max(0, groups.Count - 1));
        view.Clear();
        if (groups.Count > 0) view.AddRange(groups[groupIndex].Entries);
        viewStamp++;
        spinning = false; // its place in the old list means nothing now
        matchCount = view.Sum(v => v.Diffs.Count);
        keptSelection = view.Count == 0 ? keepDiff : null;
        expanded = Grouped && groups.Count > 0 ? groupIndex : -1;
        focusGroup = -2;
        int found = keepDiff == null ? -1 : view.FindIndex(v => v.Diffs.Contains(keepDiff));
        if (found >= 0) { setIndex = found; diffIndex = view[found].Diffs.IndexOf(keepDiff!); }
        else
        {
            found = keep == null ? -1 : view.FindIndex(v => v.Set == keep);
            setIndex = found >= 0 ? found : 0;
            diffIndex = Math.Clamp(diffIndex, 0, Math.Max(0, (view.Count > 0 ? view[setIndex].Diffs.Count : 1) - 1));
        }
        SelectionChanged();
    }

    // stable keeps the last selection (its info, scores and music) while a search matches nothing
    DbBeatmap? keptSelection;
    DbBeatmap? Selected => view.Count == 0 ? keptSelection : view[setIndex].Diffs[diffIndex];
    readonly Stack<DbBeatmap> randomHistory = [];

    // ---- stable's random spin: jump to a random entry, then step one entry on per 60Hz tick once the tick count passes
    // a threshold that starts at 0.0001 and grows x1.6 a step (menuclick each step, louder as it slows: min(50,
    // threshold + 20) / 50), stopping once the threshold passes a random 20-50. F2 while spinning stops it there.
    bool spinning;
    float spinThreshold;
    int spinTicks;
    double spinTick;

    // ungrouped, stable's spin walks every matching difficulty (so a big set is likelier): a step onto the selected
    // run selects it, a step elsewhere only moves the focus, and the end selects the difficulty it stopped on
    int spinAt;
    void SpinTo(int at, bool end)
    {
        spinAt = at;
        int e = 0;
        while (at >= view[e].Diffs.Count) at -= view[e++].Diffs.Count;
        if (end || e == setIndex) { setIndex = e; diffIndex = at; SelectionChanged(); }
        else focusRow = Layout().Start[e] + (view[e].Set == view[setIndex].Set ? at : 0);
    }

    int FlatCount => Grouped ? groups.Sum(g => g.Entries.Count) : view.Count;
    int FlatIndex()
    {
        if (!Grouped) return setIndex;
        int at = 0;
        for (int g = 0; g < groupIndex; g++) at += groups[g].Entries.Count;
        return at + setIndex;
    }
    void SelectFlat(int i)
    {
        if (Grouped) { SelectRelative(i - FlatIndex()); return; }
        setIndex = Math.Clamp(i, 0, view.Count - 1);
        diffIndex = 0;
        SelectionChanged();
    }

    void RandomKey(bool back)
    {
        if (back)
        {
            spinning = false;
            while (randomHistory.TryPop(out var prev))
                if (view.FindIndex(v => v.Diffs.Contains(prev)) is var i and >= 0)
                { if (Grouped) SelectRelative(i - setIndex); else setIndex = i; diffIndex = view[i].Diffs.IndexOf(prev); SelectionChanged(); break; }
                else if (Grouped && groups.FindIndex(g => g.Entries.Any(e => e.Diffs.Contains(prev))) is var gi and >= 0)
                { SelectEntry(gi, groups[gi].Entries.FindIndex(e => e.Diffs.Contains(prev))); break; }
        }
        else if (spinning) StopSpin();
        else
        {
            int n = Grouped ? FlatCount : matchCount;
            if (n < 2) return;
            if (Selected is { } cur) randomHistory.Push(cur);
            spinning = true;
            spinThreshold = 0.0001f;
            spinTicks = 0;
            spinTick = Game.NowMs;
            if (Grouped) SelectFlat(Random.Shared.Next(n));
            else SpinTo(Random.Shared.Next(n), false);
            Game.Skin.Sound("menuclick")?.Play(BeatmapSamples.EffectVolume);
        }
        Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
    }

    void UpdateSpin(double dtMs)
    {
        for (; spinning && Game.NowMs - spinTick >= 1000 / 60.0; spinTick += 1000 / 60.0)
        {
            if (!(spinTicks++ > spinThreshold)) continue;
            int n = Grouped ? FlatCount : matchCount;
            if (n <= 1) { StopSpin(); break; }
            Game.Skin.Sound("menuclick")?.Play(BeatmapSamples.EffectVolume * Math.Min(50, spinThreshold + 20) / 50);
            spinTicks = 0;
            spinThreshold *= 1.6f;
            if (spinThreshold > Random.Shared.Next(20, 50)) { StopSpin(); break; }
            if (Grouped) SelectFlat((FlatIndex() + 1) % n);
            else SpinTo((spinAt + 1) % n, false);
        }
    }

    void StopSpin()
    {
        spinning = false;
        if (!Grouped && view.Count > 0) SpinTo(spinAt, true);
    }

    void SelectionChanged() { selectionChangedAt = Game.NowMs; wheelTarget = null; focusRow = -1; }

    // stable: the mouse wheel scrolls the list (~1.5 rows a notch, measured) without touching the selection; the list
    // follows the selection / focus again once that moves.
    float? wheelTarget;
    int lastFocusRow = -1;
    // free scrolling stops with the last panel at the bottom (its centre ~186 units below the selection line at 220)
    float MaxFreeScroll => Math.Max(0, RowCount - 1 - 186 * U / RowH);
    bool rightScroll;
    bool leftDown, leftDragging;
    Vector2 leftPressAt;
    float leftDragFrom, dragLastY;
    double dragVelocity; // stable: holding the right button over the list scrolls it to the cursor's height

    // Left / Right: the focused panel's run, else the next run that way (wrapping round the list)
    void MoveSet(int delta)
    {
        if (Grouped) { SelectRelative(delta); return; }
        if (view.Count == 0) return;
        SelectRun(focusRow >= 0 ? RowInfo(focusRow).Set : ((setIndex + Math.Sign(delta)) % view.Count + view.Count) % view.Count);
    }

    // ungrouped: open a run at its easiest difficulty (stable picks the one nearest the player's recommended stars,
    // which is 0 when nobody's signed in)
    void SelectRun(int e)
    {
        if (e == setIndex && diffIndex == 0) { focusRow = -1; return; }
        if (view[e].Set != view[setIndex].Set) Game.Skin.Sound("select-expand")?.Play(BeatmapSamples.EffectVolume);
        setIndex = e;
        diffIndex = 0;
        SelectionChanged();
    }

    void MoveDiff(int delta)
    {
        if (Grouped) { SelectRelative(delta); return; }
        if (view.Count == 0) return;
        Game.Skin.Sound("select-difficulty")?.Play(BeatmapSamples.EffectVolume);
        // stable: delta panels on from the focus (or the selection), wrapping round the list; a difficulty of the
        // selected run becomes the selection, any other panel only the focus
        int rows = RowCount, to = ((FocusRow + delta) % rows + rows) % rows;
        var (e, d, _) = RowInfo(to);
        if (e != setIndex) { focusRow = to; return; }
        setIndex = e;
        diffIndex = d;
        SelectionChanged();
    }

    // ---------------------------------------------------------------- preview / play

    void UpdatePreview(bool menu)
    {
        var b = Selected;
        (string, string, double, bool)? key = b == null ? null : (b.Folder, b.AudioFile, mods.Rate(), mods.HasFlag(Mods.Nightcore));
        if (previewLoading is { IsCompleted: true } done)
        {
            previewLoading = null;
            var (forKey, track) = done.Result;
            if (forKey != previewKey || b == null) track?.Dispose(); // the selection moved on while it loaded
            else if (track != null)
            {
                preview = track;
                ApplyMusicVolume();
                preview.Seek(themeFromStart ? 0 : Math.Max(0, b.PreviewMs));
                themeFromStart = false;
                preview.Play();
            }
            else if (menu) { MusicNext(); return; } // (a song that won't play: the main menu goes on to another, like stable)
        }
        if (key != previewKey && Game.NowMs - selectionChangedAt > 150 && previewLoading == null)
        {
            StopPreview();
            previewKey = key;
            if (b == null) return;
            previewLoading = LoadPreview(key, Path.Combine(Config.SongsDir, b.Folder, b.AudioFile), mods.Rate(), mods.HasFlag(Mods.Nightcore));
        }
        else if (preview != null && preview.Stopped && b != null)
        {
            if (menu) { MusicNext(); return; } // the main menu plays songs through, then another (stable)
            preview.Seek(Math.Max(0, b.PreviewMs));
            preview.Play();
        }
    }

    // Off the main thread: the prescan reads the whole file (100-170ms per song change measured). Its own method so
    // the closure is only made when a load starts.
    Task<((string, string, double, bool)?, Track?)>? previewLoading;
    static Task<((string, string, double, bool)?, Track?)> LoadPreview((string, string, double, bool)? key, string path, double rate, bool nightcore) =>
        Task.Run(() => (key, Track.Load(path, rate, nightcore)));

    // Beat source: timing points of the song that's playing, loaded off the main thread.
    string timingKey = "";
    Task<(List<TimingPoint>, int)>? timingLoading;
    List<TimingPoint> timing = [];
    internal IReadOnlyList<TimingPoint> Timing => timing; // (the offset wizard's beat)
    internal int TimingFormat = 14; // that map's file format (stable's clock takes 24ms off before v5)

    void UpdateTiming()
    {
        if (timingLoading is { IsCompleted: true })
        {
            (timing, TimingFormat) = timingLoading.Result;
            timingLoading = null;
        }
        if (Current is not { } b || timingLoading != null || b.FilePath == timingKey) return;
        timingKey = b.FilePath;
        timingLoading = LoadTiming(b.FilePath);
    }

    // Its own method so the closure is only made when a load starts (UpdateTiming runs every frame).
    static Task<(List<TimingPoint>, int)> LoadTiming(string file) => Task.Run(() =>
    {
        try { var m = Beatmap.Load(file); return (m.TimingPoints, m.FormatVersion); }
        catch (Exception e) when (e is not OutOfMemoryException) { return (new List<TimingPoint>(), 14); } // (unreadable or malformed: no timing)
    });

    // Where the music is in its beat: index within the current red line, phase 0 (on the beat) .. 1.
    // PointTime: the timing point in effect (red or green).
    internal (double RedTime, long Index, double Phase, double Length, int Meter, bool Kiai, double PointTime)? Beat()
    {
        double ms = MusicMs;
        if (double.IsNaN(ms) || timing.Count == 0) return null;
        TimingPoint? red = null;
        bool kiai = false;
        double point = double.NaN;
        foreach (var t in timing)
        {
            if (t.Time > ms) break;
            if (t.Uninherited) red = t;
            kiai = t.Kiai;
            point = t.Time;
        }
        red ??= timing.FirstOrDefault(t => t.Uninherited);
        if (red == null) return null;
        double beats = (ms - red.Time) / red.BeatLength;
        long index = (long)Math.Floor(beats);
        return (red.Time, index, beats - index, red.BeatLength, red.Meter, kiai, point);
    }

    internal double BeatPhase() => Beat()?.Phase ?? double.NaN;

    // The main menu's music controls (stable's Now Playing buttons). Next takes the forward history when there is
    // one (after Previous), else a random song; Stop pauses at the start so nothing restarts it.
    readonly Stack<int> musicBack = [], musicForward = [];

    internal IReadOnlyList<BeatmapSet> Library => sets;

    // the main menu's Jump To window: play that map's set (like Next, it can be gone back from)
    internal void MusicJump(DbBeatmap b)
    {
        if (Grouped) // the view holds only the open group: find the set's first entry in any group
        {
            for (int g = 0; g < groups.Count; g++)
                if (groups[g].Entries.FindIndex(e => e.Set.Diffs.Contains(b)) is var e and >= 0) { SelectEntry(g, e); return; }
            return;
        }
        int i = view.FindIndex(v => v.Set.Diffs.Contains(b));
        if (i < 0 || i == setIndex) return;
        musicBack.Push(setIndex);
        setIndex = i;
        diffIndex = 0;
        SelectionChanged();
    }

    internal void MusicNext()
    {
        if (view.Count == 0) return;
        musicBack.Push(setIndex);
        setIndex = musicForward.Count > 0 ? Math.Min(musicForward.Pop(), view.Count - 1) : Random.Shared.Next(view.Count);
        diffIndex = 0;
        SelectionChanged();
    }

    internal void MusicPrevious()
    {
        if (musicBack.Count == 0 || view.Count == 0) return;
        musicForward.Push(setIndex);
        setIndex = Math.Min(musicBack.Pop(), view.Count - 1);
        diffIndex = 0;
        SelectionChanged();
    }

    internal void MusicPlay()
    {
        if (preview == null) return;
        if (!preview.Paused) preview.Seek(0);
        preview.Play();
    }

    // Returns true if it paused (false: it resumed).
    internal bool MusicPause()
    {
        if (preview == null) return false;
        if (preview.Paused) { preview.Play(); return false; }
        preview.Pause();
        return true;
    }

    internal void MusicStop()
    {
        preview?.Pause();
        preview?.Seek(0);
    }

    internal void MusicSeek(double fraction)
    {
        if (preview != null) preview.Seek(fraction * preview.LengthMs);
    }

    internal void ApplyMusicVolume()
    {
        if (preview != null) preview.Volume = Config.MusicVolume;
    }

    internal void StopPreview()
    {
        preview?.Dispose();
        preview = null;
        previewKey = null;
    }

    // stable's Preview gameplay (options, skin section): up to 99 random picks from the whole library for a map whose
    // file is there and that has a length; Auto becomes the only selected mod and that map is selected and played. An
    // empty library: the main menu and a notice.
    internal void PreviewGameplay()
    {
        var all = sets.SelectMany(s => s.Diffs).ToList();
        DbBeatmap? pick = null;
        for (int n = all.Count > 0 ? 99 : 0; n > 0 && (pick == null || pick.TotalMs <= 0 || !File.Exists(pick.FilePath)); n--)
            pick = all[Random.Shared.Next(all.Count)];
        if (pick == null)
        {
            if (Menu != null) Game.FadeTo(() => Menu);
            Toast.Show("Failed to find a map to preview with! Please download more beatmaps!");
            return;
        }
        mods = Mods.Autoplay;
        ApplyFilter(pick);
        Play(mods, pick);
    }

    // the editor: from the main menu's Edit (song select picks the map) or the beatmap options
    internal bool EditMode;
    void OpenEditor(DbBeatmap b)
    {
        Editor ed;
        try { ed = new Editor(b.FilePath, this); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notification.Show($"Couldn't open the beatmap: {e.Message}", 3000); return; }
        Game.Skin.Sound("menuhit")?.Play(BeatmapSamples.EffectVolume);
        StopPreview();
        Game.FadeTo(ed);
    }

    void Play(Mods mods, DbBeatmap? only = null)
    {
        if ((only ?? Selected) is not { } b) return;
        if (EditMode && only == null) { OpenEditor(b); return; }
        // stable, however the play starts (Enter, the panel, the logo): Ctrl held makes it an Auto play and Ctrl+Shift
        // a Cinema one, for this play only (the selected mods stay as they are)
        if (Input.CtrlHeld) mods = mods & ~(Mods.Relax | Mods.Autopilot) | Mods.Autoplay | (Input.ShiftHeld ? Mods.Cinema : Mods.None);
        try
        {
            var map = Beatmap.Load(b.FilePath, mods: mods);
            if (map.Objects.Count == 0) { message = "Map has no hit objects"; return; }
            map.PlayMode = map.Mode != 0 ? map.Mode : PlayMode;
            Game.Skin.Sound("menuhit")?.Play(BeatmapSamples.EffectVolume);
            var player = new Player(map, this, mods) { LeftSongSelectAt = Game.NowMs };
            // the preview fades out in the player before its clock starts, like stable
            if (preview != null) { player.FadingOut(preview); preview = null; previewKey = null; }
            else StopPreview();
            Game.Screen = player;
        }
        catch (Exception e) when (e is not OutOfMemoryException) // a malformed map mustn't take the game down
        {
            message = $"Failed to load: {e.Message}";
            Game.UseSkin(null); // (a play that never started gives its skin back)
        }
    }

    // ---------------------------------------------------------------- input

    static readonly KeyboardKey[] ModKeys =
    [
        KeyboardKey.Q, KeyboardKey.W, KeyboardKey.E, KeyboardKey.A, KeyboardKey.S, KeyboardKey.D,
        KeyboardKey.F, KeyboardKey.G, KeyboardKey.Z, KeyboardKey.X, KeyboardKey.C, KeyboardKey.V, KeyboardKey.B,
    ];
    static readonly string[] ModBindings = ["keyEasy", "keyNoFail", "keyHalfTime", "keyHardRock", "keySuddenDeath", "keyDoubleTime", "keyHidden", "keyFlashlight", "keyRelax", "keyAutopilot", "keySpunOut", "keyAuto", "keyScoreV2"];

    static bool Pressed(KeyboardKey k) => Input.IsKeyPressed(k) || Input.IsKeyPressedRepeat(k);

    // Backspace in stable's text boxes: one character, or with Ctrl the last word ("my love" -> "my ")
    internal static string DeleteBack(string s) =>
        !Input.CtrlHeld ? s[..^1] : s.TrimEnd().LastIndexOf(' ') is var sp and >= 0 ? s.TrimEnd()[..(sp + 1)] : "";

    // The main menu we return to on Esc/back (null: quit instead).
    internal Screen? Menu;
    internal DbBeatmap? Current => Selected;
    internal double MusicMs => preview?.PositionMs ?? double.NaN;
    internal Track? Music => preview;

    // Library/score loading, preview music and background. The main menu runs this too, so its music and
    // background are song select's. Returns false while the library is still loading.
    // stable watches the Songs folder (subfolders too) while it runs: the first change in a folder, to an .osu file or
    // at the top level says "New beatmap detected.  Hit F5 to refresh." while song select is up; F5 starts over. The
    // game's own deletes and imports don't count.
    static FileSystemWatcher? songsWatcher;
    static readonly System.Collections.Concurrent.ConcurrentQueue<string> songsChanged = new();
    static readonly HashSet<string> songsSeen = new(StringComparer.OrdinalIgnoreCase);
    internal static double IgnoreSongsUntil;

    static void WatchSongs()
    {
        if (songsWatcher != null || !Directory.Exists(Config.SongsDir)) return;
        try
        {
            songsWatcher = new FileSystemWatcher(Config.SongsDir) { IncludeSubdirectories = true };
            songsWatcher.Created += (_, e) => songsChanged.Enqueue(e.FullPath);
            songsWatcher.Deleted += (_, e) => songsChanged.Enqueue(e.FullPath);
            songsWatcher.Renamed += (_, e) => songsChanged.Enqueue(e.FullPath);
            songsWatcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException) { songsWatcher = null; }
    }

    void PollSongsWatcher()
    {
        while (songsChanged.TryDequeue(out var path))
        {
            if (Game.NowMs < IgnoreSongsUntil) continue;
            if (Path.GetRelativePath(Config.SongsDir, path).Split(Path.DirectorySeparatorChar)[0].Equals("Failed", StringComparison.OrdinalIgnoreCase)) continue; // broken imports
            string parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
            string key = parent.Equals(Path.GetFullPath(Config.SongsDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".osu", StringComparison.OrdinalIgnoreCase) ? path : parent;
            if (songsSeen.Add(key) && Game.Screen == this) Notification.Show("New beatmap detected.  Hit F5 to refresh.", 2000);
        }
    }

    internal bool Ambient(bool menu = false)
    {
        PollSongsWatcher();
        if (loading != null)
        {
            if (!loading.IsCompleted) return false;
            sets = loading.Result;
            loading = null;
            songsSeen.Clear();
            WatchSongs();
            var again = reselect == null ? null : sets.SelectMany(s => s.Diffs).FirstOrDefault(d => d.Md5 == reselect);
            // every non-Latin character the library's names use, into the fallback glyph atlases at once (one build
            // instead of one each time a new title scrolls into view)
            Game.PreloadGlyphs(sets.SelectMany(s => s.Diffs).SelectMany(d => new[] { d.Title, d.TitleUnicode, d.Artist, d.ArtistUnicode, d.Version, d.Creator, d.Source }));
            // like stable's background difficulty calculation: new maps' stars fill in while the list is already up
            // (on a low-priority thread: thousands of new maps can take minutes, and a play shouldn't stutter meanwhile)
            if (unrated.Count > 0)
            {
                ratingTotal = ratingLeft = unrated.Count;
                if (unrated.Count > 50 && !ratingNoticed) { ratingNoticed = true; Toast.Show("Beatmap difficulty calculation is running in the background. Performance at song select may be lower than usual."); }
            }
            if (unrated.Count > 0) { var todo = unrated; unrated = []; var keep = cacheEntries; rating = RunLowPriority(() => { var sw = Stopwatch.StartNew(); foreach (var b in todo) { RateStars(b); Interlocked.Decrement(ref ratingLeft); } rankedFor = null; OsuDb.SaveMapCache(keep); Console.Error.WriteLine($"star ratings for {todo.Count} new maps in {sw.Elapsed.TotalSeconds:0.0}s"); }); }
            ApplyFilter(again);
            if (reselect == null && filter.Length == 0 && view.Count > 0)
            {
                // stable's menu theme (MenuMusic): circles! from the start when it's in the library, else a random song.
                int theme = Menu != null && Config.GetBool("MenuMusic", true) ? view.FindIndex(v => v.Set.First.Artist == "nekodex" && v.Set.First.Title == "circles!") : -1;
                setIndex = theme >= 0 ? theme : Random.Shared.Next(view.Count);
                diffIndex = 0;
                themeFromStart = theme >= 0;
            }
            reselect = null;
        }
        // the new maps' star ratings are in: re-sort (by difficulty they all sat at 0 stars), keeping the selection
        if (rating is { IsCompleted: true })
        {
            rating = null;
            ratingLeft = 0;
            ApplyFilter();
            if (ratingNoticed) { ratingNoticed = false; Toast.Show("Background difficulty calculation finished.", YellowGreen, 4000); }
        }
        PollScores();
        UpdatePreview(menu);
        UpdateTiming();
        UpdateBackground();
        return true;
    }

    void Back()
    {
        Game.Skin.Sound("menuback")?.Play(BeatmapSamples.EffectVolume);
        if (Menu != null) Game.FadeTo(Menu);
        else Game.Quit = true;
    }

    // stable: song select with no beatmaps at all says so for 5 seconds, then goes back to the main menu (stable also
    // opens the website's beatmap listing; this offline remake doesn't, so its notice leaves that line out)
    double noMapsAt = double.NaN;
    void CheckEmptyLibrary()
    {
        if (sets.Count > 0 || Menu is not { } menu) { noMapsAt = double.NaN; return; }
        if (double.IsNaN(noMapsAt))
        {
            noMapsAt = Game.NowMs;
            Notification.Show("You have no beatmaps!  Please place at least one beatmap or audio file in the songs directory before starting.", 5000);
        }
        else if (Game.NowMs - noMapsAt >= 5000 && !Game.Fading) { noMapsAt = double.NaN; Game.FadeTo(() => menu); }
    }

    public override void Update(double dtMs)
    {
        Game.Skin.UploadPrefetched();
        if (!Ambient()) return;
        CheckEmptyLibrary();

        if (Input.CtrlHeld && !Input.AltHeld && Input.IsKeyPressed(KeyboardKey.O)) { Game.Screen = new Options(this, this); return; }
        // stable offline: the chat keys open the options at sign-in
        if (Input.IsKeyPressed(Config.Key("keyToggleChat", KeyboardKey.F8)) || Input.IsKeyPressed(Config.Key("keyToggleExtendedChat", KeyboardKey.F9))) { Options.lastScroll = 0; Game.Screen = new Options(this, this); return; }

        // F1: stable's mod select, with stable's hotkeys.
        if (dialog != null) { UpdateDialog(); while (Input.GetCharPressed() != 0) { } return; }
        if (Input.IsKeyPressed(KeyboardKey.F3) && !modSelect) { OpenBeatmapOptions(); return; }
        if (Input.IsKeyPressed(KeyboardKey.F1)) OpenModSelect(!modSelect);
        else if (modSelect)
        {
            while (Input.GetCharPressed() != 0) { } // keep mod hotkeys out of the search box
            UpdateModSelect();
            return;
        }
        if (modSelect) { while (Input.GetCharPressed() != 0) { } return; }
        if (openDrop != 0) { UpdateDropdown(); while (Input.GetCharPressed() != 0) { } return; }
        if (modeMenuAt >= 0) // the mode menu takes every click and Esc while open
        {
            bool click = Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsMouseButtonPressed(MouseButton.Right), esc = Input.IsKeyPressed(KeyboardKey.Escape);
            UpdateModeMenu(Input.GetMousePosition());
            if (click || esc) { while (Input.GetCharPressed() != 0) { } return; }
        }

        // stable: Ctrl+A toggles Auto outside the mod select too (on and off: not V's cycle through Cinema)
        if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.A))
        {
            mods = mods.HasFlag(Mods.Autoplay) ? mods & ~(Mods.Autoplay | Mods.Cinema) : ModInfo.Toggle(mods, KeyboardKey.V);
            while (Input.GetCharPressed() != 0) { }
        }
        if (Grouped)
        {
            if (Pressed(KeyboardKey.Down)) MoveFocus(1);
            if (Pressed(KeyboardKey.Up)) MoveFocus(-1);
            bool shift = Input.ShiftHeld;
            if (Pressed(KeyboardKey.Right)) { if (shift) JumpGroup(1); else Sideways(1); }
            if (Pressed(KeyboardKey.Left)) { if (shift) JumpGroup(-1); else Sideways(-1); }
        }
        else
        {
            if (Pressed(KeyboardKey.Down)) MoveDiff(1);
            if (Pressed(KeyboardKey.Up)) MoveDiff(-1);
            if (Pressed(KeyboardKey.Right)) MoveSet(1);
            if (Pressed(KeyboardKey.Left)) MoveSet(-1);
        }
        // stable: PageUp / PageDown move 10 panels
        if (Pressed(KeyboardKey.PageDown)) { if (Grouped) MoveFocus(10); else MoveDiff(10); }
        if (Pressed(KeyboardKey.PageUp)) { if (Grouped) MoveFocus(-10); else MoveDiff(-10); }
        // stable's F5 rescans the Songs folder and rebuilds the list; ours re-reads osu!.db, keeping the selection
        if (Input.IsKeyPressed(KeyboardKey.F5)) { Game.Screen = new BeatmapImport(this, [], full: true); return; } // stable: the library is read again (its import screen passes unseen) and the list slides back in
        // stable: Shift+Delete is the beatmap options' Delete...
        if (Input.IsKeyPressed(KeyboardKey.Delete) && Input.ShiftHeld && Selected is { } toDelete) { OpenDelete(toDelete); return; }
        // F2: random map (stable's spin; F2 again stops it); Shift+F2: back to the one before the last random pick.
        if (Input.IsKeyPressed(KeyboardKey.F2) && view.Count > 0) RandomKey(Input.ShiftHeld);
        UpdateSpin(dtMs);
        float wheel = VolumeControl.Wheel;
        if (FocusRow != lastFocusRow) { lastFocusRow = FocusRow; wheelTarget = null; }
        if (wheel != 0)
        {
            // ~72 units a notch: in rows at the current spot (difficulty rows carry extra spacing)
            float from = wheelTarget ?? FocusRow;
            int r0 = Math.Max(0, (int)from);
            float rowUnits = (200 * U + (r0 + 1) * RowH + Extra(r0 + 1) - (200 * U + r0 * RowH + Extra(r0))) / U;
            wheelTarget = Math.Clamp(from - wheel * 72 / Math.Max(1, rowUnits), 0, MaxFreeScroll);
        }
        {
            var m = Input.GetMousePosition();
            if (Input.IsMouseButtonPressed(MouseButton.Right) && m.X >= 200 * U && m.Y > TopH && m.Y < Game.H - BottomH
                && !CheckCollisionPointRec(m, GroupBox(Game.W)) && !CheckCollisionPointRec(m, SortBox(Game.W)))
            {
                // stable: right-clicking a beatmap panel selects it and opens the beatmap options; elsewhere it scrolls
                if (m.X >= PanelX(RowAt(m.Y)) && RightClickRow(m.Y)) { OpenBeatmapOptions(); return; }
                rightScroll = true;
            }
            if (!Input.IsMouseButtonDown(MouseButton.Right)) rightScroll = false;
            // (y - 70) / (480 - 150) of the way down the whole list
            if (rightScroll && !Input.IsMouseButtonDown(MouseButton.Left))
                wheelTarget = Math.Clamp((m.Y / U - 70) / 330, 0, 1) * MaxFreeScroll;
        }

        var mouse = Input.GetMousePosition();
        if ((Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsMouseButtonPressed(MouseButton.Right)) && CheckCollisionPointRec(mouse, GroupBox(Game.W)))
        {
            if (Input.IsMouseButtonPressed(MouseButton.Left)) { openDrop = openDrop == 1 ? 0 : 1; Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume); return; }
            int gi = Array.FindIndex(GroupList, m => m.Key == GroupMode);
            SetGroupMode(GroupList[((gi < 0 ? 0 : gi) - 1 + GroupList.Length) % GroupList.Length].Key);
            return;
        }
        for (int t = 0; t < GroupModes.Length; t++)
            if (Input.IsMouseButtonPressed(MouseButton.Left) && CheckCollisionPointRec(mouse, TabRect(Game.W, t))) { SetGroupMode(GroupModes[t].Key); return; }
        if ((Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsMouseButtonPressed(MouseButton.Right)) && CheckCollisionPointRec(mouse, SortBox(Game.W)))
        {
            if (Input.IsMouseButtonPressed(MouseButton.Left)) { openDrop = openDrop == 2 ? 0 : 2; Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume); }
            else CycleSort(-1);
            return;
        }
        if (Input.IsMouseButtonPressed(MouseButton.Left) && CheckCollisionPointRec(mouse, RankBox)) { openDrop = 3; Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume); return; }
        if (Input.IsMouseButtonPressed(MouseButton.Left) && ClickBottomBar(mouse)) return;
        if (Input.IsMouseButtonPressed(MouseButton.Left) && mouse.Y > TopH && mouse.Y < Game.H - BottomH)
        {
            if (mouse.X < Game.W * 0.35f)
            {
                int n = ScoreRowAt(mouse.Y);
                if (n >= 0 && n < Math.Min(8, SelectedScores.Count)) { ShowScore(SelectedScores[n]); return; }
            }
            else { leftDown = true; leftPressAt = mouse; leftDragging = false; leftDragFrom = scrollRow; dragLastY = mouse.Y; dragVelocity = 0; }
        }
        // stable: a press on the list becomes a drag once the cursor is 80px away (the list follows it); otherwise the
        // panel acts when the button is let go
        // velocities in 768-high pixels per ms; stable keeps a smoothed drag speed and lets it run on after release,
        // decaying by max(0.5, 0.996 - 0.002 / |v|) per ms
        float k768 = 768f / Game.H;
        if (leftDown)
        {
            if (!leftDragging && Vector2.DistanceSquared(mouse, leftPressAt) > 6400) leftDragging = true;
            if (leftDragging)
            {
                wheelTarget = scrollRow = Math.Clamp(leftDragFrom - (mouse.Y - leftPressAt.Y) / RowH, 0, MaxFreeScroll);
                double inst = dtMs > 0 ? (dragLastY - mouse.Y) * k768 / dtMs : 0;
                double kk = Math.Pow(Math.Sign(inst) == -Math.Sign(dragVelocity) || Math.Abs(inst) > Math.Abs(dragVelocity) ? 0.9 : 0.95, dtMs);
                dragVelocity = dragVelocity * kk + (1 - kk) * inst;
            }
            dragLastY = mouse.Y;
            if (!Input.IsMouseButtonDown(MouseButton.Left))
            {
                leftDown = false;
                if (!leftDragging && ClickRow(leftPressAt.Y)) return;
                if (!leftDragging) dragVelocity = 0;
            }
        }
        else if (dragVelocity != 0 && wheelTarget is float wt)
        {
            double f = Math.Max(0.5, 0.996 - 0.002 / Math.Abs(dragVelocity)), v0 = dragVelocity;
            dragVelocity *= Math.Pow(f, dtMs);
            double moved768 = (dragVelocity - v0) / Math.Log(f); // the distance covered while it decayed
            wheelTarget = scrollRow = Math.Clamp(wt + (float)(moved768 / k768) / RowH, 0, MaxFreeScroll);
            if (Math.Abs(dragVelocity) < 0.01) dragVelocity = 0;
        }
        else dragVelocity = 0;
        // stable: Ctrl+1..4 switch the game mode (osu!, taiko, catch, mania)
        if (Input.CtrlHeld)
            for (int n = 0; n < ModeRows.Length; n++)
                if (Input.IsKeyPressed(KeyboardKey.One + n)) SetMode(n);
        // Ctrl+Enter: this one play with Auto, like stable (the selected mods stay as they are).
        if (Input.IsKeyPressed(KeyboardKey.Enter))
        {
            // stable: Shift+Enter (grouped) closes / opens the group of the focus (else the selection)
            if (Grouped && groups.Count > 0 && Input.ShiftHeld && !Input.CtrlHeld)
            { ToggleGroup(focusGroup >= 0 ? focusGroup : groupIndex); return; }
            if (Grouped && focusGroup >= 0 && focusEntry < 0) { ToggleGroup(focusGroup); return; }
            if (Grouped && focusGroup >= 0 && (focusGroup != groupIndex || focusEntry != setIndex)) { SelectEntry(focusGroup, focusEntry); return; }
            if (!Grouped && focusRow >= 0) { SelectRun(RowInfo(focusRow).Set); return; }
            Play(mods);
            return;
        }

        if (Input.IsKeyPressed(KeyboardKey.Escape))
        {
            if (filter.Length > 0) { filter = ""; ApplyFilter(); }
            else Back();
            return;
        }

        bool changed = false;
        for (int ch = Input.GetCharPressed(); ch != 0; ch = Input.GetCharPressed())
        {
            filter += char.ConvertFromUtf32(ch);
            changed = true;
            TypingSound("key-press");
        }
        if (Pressed(KeyboardKey.Backspace) && filter.Length > 0)
        {
            filter = DeleteBack(filter);
            changed = true;
            TypingSound("key-delete");
        }
        if (changed) ApplyFilter();

        // right-button scrolling eases like stable's (0.992 a ms), everything else follows within ~80ms
        scrollRow += ((wheelTarget ?? FocusRow) - scrollRow) * (rightScroll ? 1 - (float)Math.Pow(0.992, dtMs) : (float)Math.Min(1, dtMs / 80));
        UpdateCarousel(dtMs, Input.GetMousePosition());
        UpdateSparkles();
    }

    // stable's text box sounds: a typed character plays key-press-1..4 at random (else key-press), Backspace / Delete
    // key-delete.
    internal static void TypingSound(string name)
    {
        var s = name == "key-press" ? Game.Skin.Sound($"key-press-{Random.Shared.Next(1, 5)}") ?? Game.Skin.Sound("key-press") : Game.Skin.Sound(name);
        s?.Play(BeatmapSamples.EffectVolume);
    }

    // Rows: one per set, except the selected set, which is replaced by one row per difficulty (like stable).
    // Grouped (stable's): a header row per group and the expanded group's difficulties under its header; the
    // selection's group (groupIndex, its entries in view) needn't be the expanded one. Up/Down move a focus over the
    // rows without selecting; Left/Right (and Enter) open or close a focused header, select a focused difficulty, or
    // step the selection on through the groups; a tab switch closes every group, focusing the selection's header.
    int expanded = -1;
    int focusGroup = -2, focusEntry; // a focused header has entry -1; -2: the focus follows the selection
    static readonly List<(BeatmapSet Set, List<DbBeatmap> Diffs)> NoEntries = [];
    List<(BeatmapSet Set, List<DbBeatmap> Diffs)> RowEntries => !Grouped ? view : expanded >= 0 ? groups[expanded].Entries : NoEntries;
    int ExpandedCount => Grouped && expanded >= 0 ? groups[expanded].Entries.Count : 0;
    int HeaderRow(int g) => expanded >= 0 && g > expanded ? g + ExpandedCount : g;
    int EntryRows => Layout().Start[^1];
    int SelectedRow => !Grouped ? (view.Count == 0 ? 0 : Layout().Start[setIndex]) + diffIndex
        : expanded == groupIndex && view.Count > 0 ? HeaderRow(expanded) + 1 + setIndex : HeaderRow(groupIndex);
    int FocusRow => !Grouped ? focusRow >= 0 ? focusRow : SelectedRow
        : focusGroup < -1 ? SelectedRow : focusEntry < 0 ? HeaderRow(focusGroup) : HeaderRow(focusGroup) + 1 + focusEntry;

    // ungrouped: every run of the selected set is open (a row per difficulty), the other entries a row each. Start:
    // each entry's first row (then the total); Gaps: stable's 10-unit gaps up to each row, one beside every open panel.
    // stable's keyboard focus: Up/Down only highlight a panel outside the selected run (even another run of the same
    // set); Enter or Left/Right then open that panel's run
    int focusRow = -1;
    int viewStamp;
    (BeatmapSet?, int, int) layoutKey;
    (int[] Start, int[] Gaps) layout = ([0], []);
    (int[] Start, int[] Gaps) Layout()
    {
        var open = view.Count > 0 ? view[setIndex].Set : null;
        if (layoutKey == (open, view.Count, viewStamp)) return layout;
        layoutKey = (open, view.Count, viewStamp);
        var start = new int[view.Count + 1];
        for (int i = 0; i < view.Count; i++) start[i + 1] = start[i] + (view[i].Set == open ? view[i].Diffs.Count : 1);
        var gaps = new int[start[^1]];
        for (int i = 0, r = 0, g = 0; i < view.Count; i++)
            for (bool o = view[i].Set == open; r < start[i + 1]; r++)
            {
                if (r > 0 && (o || r == start[i] && view[i - 1].Set == open)) g++;
                gaps[r] = g;
            }
        return layout = (start, gaps);
    }
    int RowCount => Grouped ? groups.Count + ExpandedCount : EntryRows;

    // Returns (set, diff, group) for a row: group >= 0 for a group header, diff = -1 for a collapsed set row (grouped:
    // an entry of the expanded group, diff 0 when it's the selected one).
    (int Set, int Diff, int Group) RowInfo(int row)
    {
        if (Grouped)
        {
            int n = ExpandedCount;
            if (expanded < 0 || row <= expanded) return (-1, -1, row);
            if (row <= expanded + n) { int e = row - expanded - 1; return (e, expanded == groupIndex && e == setIndex ? 0 : -1, -1); }
            return (-1, -1, row - n);
        }
        var start = Layout().Start;
        int at = Array.BinarySearch(start, row);
        if (at < 0) at = ~at - 1;
        return view[at].Set == view[setIndex].Set ? (at, row - start[at], -1) : (at, -1, -1);
    }

    // ---- grouping (stable's tabs): Collections, Recently Played, By Artist, By Difficulty, No Grouping.
    static readonly (string Key, string Label)[] GroupModes =
        [("Collections", "Collections"), ("Recent", "Recently Played"), ("Artist", "By Artist"), ("Difficulty", "By Difficulty"), ("None", "No Grouping")];
    // stable's Group dropdown (all of them) and its five tabs
    static readonly (string Key, string Label)[] GroupList =
    [
        ("None", "No Grouping"), ("Artist", "By Artist"), ("Bpm", "By BPM"), ("Creator", "By Creator"), ("Date", "By Date Added"),
        ("Difficulty", "By Difficulty"), ("Length", "By Length"), ("Mode", "By Mode"), ("Rank", "By Rank Achieved"), ("Title", "By Title"),
        ("Collections", "Collections"), ("Favourites", "Favourites"), ("MyMaps", "My Maps"), ("Status", "Ranked Status"), ("Recent", "Recently Played"),
    ];
    static readonly string[] GroupTabLabels = [.. GroupModes.Select(m => m.Label)];
    // ours once changed here (remake.cfg), else stable's own TreeSortMode
    static string GroupMode => Config.Get("RemakeGroupMode", Config.Get("TreeSortMode", "Show_All") switch
    {
        "Artist" => "Artist", "BPM" => "Bpm", "Creator" => "Creator", "Date" => "Date", "Difficulty" => "Difficulty", "Length" => "Length",
        "Mode" => "Mode", "Rank" => "Rank", "Title" => "Title", "Collection" => "Collections", "Online_Favourites" => "Favourites",
        "My_Maps" => "MyMaps", "RankedStatus" => "Status", "Last_Played" => "Recent", _ => "None",
    });
    // Header text and match count, rebuilt only when they change.
    (DbBeatmap?, Mods, float) headerFor;
    DbBeatmap? fadeFor; // the map the header last faded in for
    string statusIcon = "selection-question";
    int statusKind;
    bool statusFadesIn;
    double headerFadeAt = double.MinValue;
    string[] header = [];
    // stable's tooltip on the difficulty line: approach time, the 300 / 100 / 50 windows (whole ms less half, as the
    // judging compares with <), spinner difficulty and the circle radius in osu! pixels, all with the mods
    string diffTip = "", offsetLine = "";
    bool diffTipShown;
    // stable colours the length line by the speed mod and the stats line by HR / EZ (salmon harder, light
    // blue easier), and marks AR, OD and HP with a small arrow when a speed mod changes them in play
    static readonly Color Harder = new(246, 154, 161, 255), Easier = new(173, 216, 230, 255);
    Color lengthColour = Color.White, statsColour = Color.White;
    int statArrow;
    readonly CachedText<int> matchesText = new(v => v == 1 ? "1 match found!" : $"{v} matches found!");
    int matchCount; // difficulties left after the search filter
    // Per-row text for the leaderboard and grade icons, built once per replay / grade.
    static readonly Dictionary<string, string> smallGrades = [];
    static string SmallGrade(string grade) => smallGrades.TryGetValue(grade, out var s) ? s : smallGrades[grade] = $"ranking-{grade}-small";
    readonly Dictionary<ReplayInfo, (string Score, string Mods, string Acc)> rowText = [];
    (string Score, string Mods, string Acc) RowText(ReplayInfo r) => rowText.TryGetValue(r, out var t) ? t : rowText[r] =
        ($"Score: {r.Score:N0} ({r.MaxCombo:N0}x)", r.Mods == Mods.None ? "" : string.Join(",", r.Mods.Short().Split(' ', StringSplitOptions.RemoveEmptyEntries)),
         $"{(r.N300 + r.N100 + r.N50 + r.NMiss == 0 ? 100 : r.Accuracy):0.00}%"); // stable: no judgements count as 100%

    // stable's "how long ago" for scores under 96 hours old: days past 47h, else hours, minutes, seconds.
    readonly Dictionary<ReplayInfo, (long Key, string Text)> agoText = [];
    string? AgoText(ReplayInfo r)
    {
        double hours = (DateTime.Now - r.Date).TotalHours;
        if (hours >= 96 || hours < 0) return null;
        (long key, char unit) = hours > 47 ? ((long)(hours / 24), 'd') : hours >= 1 ? ((long)hours, 'h')
            : hours > 1 / 60.0 ? ((long)(hours * 60), 'm') : ((long)(hours * 3600), 's');
        key = key * 4 + "dhms".IndexOf(unit);
        if (agoText.TryGetValue(r, out var t) && t.Key == key) return t.Text;
        string s = $"{key / 4}{unit}";
        agoText[r] = (key, s);
        return s;
    }
    readonly string[] rowLines = new string[3];
    readonly NumberText leadText = new(v => $"+{v:N0}");
    readonly Dictionary<(string, int), string> groupLabels = [];
    string GroupLabel(string name, int maps) => groupLabels.TryGetValue((name, maps), out var s) ? s
        : groupLabels[(name, maps)] = $"{name} ({maps} {(maps == 1 ? "map" : "maps")})";
    // My Maps and Favourites are flat lists in stable (no headers)
    static bool Grouped => GroupMode is not ("None" or "MyMaps" or "Favourites");
    readonly List<(string Name, List<(BeatmapSet Set, List<DbBeatmap> Diffs)> Entries)> groups = [];
    int groupIndex;
    static List<(string Name, HashSet<string> Md5s)>? collections;

    // ---- stable's Group / Sort dropdowns: the box fills with its label colour; the options list under it on black at
    // 94%, 16.44 units a row, the current one bold, the hovered one in the label colour with a chevron. A click picks
    // (or closes), Esc closes.
    int openDrop; // 0 none, 1 group, 2 sort, 3 the leaderboard type
    const float DropRowH = 16.44f;
    // stable's leaderboard types; offline only Local Ranking works
    static readonly (string Key, string Label)[] RankTypes = [("Local", "Local Ranking"), ("Country", "Country Ranking"), ("Top", "Global Ranking"), ("SelectedMod", "Global Ranking (Selected Mods)"), ("Friends", "Friend Ranking")];
    static Rectangle RankBox => new(5 * U, 73 * U, 191 * U, 15 * U);
    (string Key, string Label)[] DropItems => openDrop switch { 1 => GroupList, 2 => SortModes, _ => RankTypes };
    Rectangle DropBox => openDrop switch { 1 => GroupBox(Game.W), 2 => SortBox(Game.W), _ => RankBox };
    Rectangle DropRow(int i) { float u = U; var box = DropBox; float left = openDrop == 3 ? 0 : 1.6f * u; return new Rectangle(box.X - left, box.Y + box.Height + 2 * u + i * DropRowH * u, box.Width + left, DropRowH * u); }
    int DropHover() { var m = Input.GetMousePosition(); for (int i = 0; i < DropItems.Length; i++) if (CheckCollisionPointRec(m, DropRow(i))) return i; return -1; }

    void UpdateDropdown()
    {
        if (Input.IsKeyPressed(KeyboardKey.Escape)) { openDrop = 0; return; }
        if (!Input.IsMouseButtonPressed(MouseButton.Left) && !Input.IsMouseButtonPressed(MouseButton.Right)) return;
        int i = DropHover(), which = openDrop;
        if (CheckCollisionPointRec(Input.GetMousePosition(), DropBox)) { openDrop = 0; return; }
        openDrop = 0;
        if (i < 0) return;
        if (which == 3) { if (i > 0) Notification.Show("You need to be logged in to access online rankings!", 1500); }
        else if (which == 1) SetGroupMode(GroupList[i].Key);
        else if (!SortModes[i].Key.Equals(SortMode, StringComparison.OrdinalIgnoreCase)) { Config.SetOwn("TreeSortMode2", SortModes[i].Key); ApplyFilter(); Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume); }
    }

    void DrawDropdown()
    {
        if (openDrop == 0) return;
        float u = U;
        var items = DropItems;
        string current = openDrop switch { 1 => GroupMode, 2 => SortMode, _ => "Local" };
        var lit = openDrop switch { 1 => new Color(146, 195, 230, 255), 2 => new Color(174, 210, 139, 255), _ => new Color(22, 130, 179, 255) };
        var first = DropRow(0);
        DrawRectangleRec(new Rectangle(first.X, first.Y, first.Width, items.Length * DropRowH * u), new Color(0, 0, 0, 240));
        int hover = DropHover();
        for (int i = 0; i < items.Length; i++)
        {
            var r = DropRow(i);
            if (i == hover)
            {
                DrawRectangleRec(r, lit);
                Options.DrawIcon(0xf054, r.X + 4.2f * u, r.Y + r.Height / 2, 8.5f * u, Color.Black);
            }
            float size = 13.4f * u, ty = r.Y + r.Height / 2 - 7.3f * u;
            if (items[i].Key.Equals(current, StringComparison.OrdinalIgnoreCase)) Game.TextBold(items[i].Label, r.X + 10.2f * u, ty, size, Color.White);
            else Game.Text(items[i].Label, r.X + 10.2f * u, ty, size, Color.White);
        }
    }

    void SetGroupMode(string key)
    {
        if (key == GroupMode) return;
        Config.SetOwn("RemakeGroupMode", key);
        // stable: these groupings bring the matching sort along
        if (key is "Artist" or "Creator" or "Date" or "Difficulty" or "Length" or "Title" && !key.Equals(SortMode, StringComparison.OrdinalIgnoreCase))
            Config.SetOwn("TreeSortMode2", key);
        groupIndex = 0;
        ApplyFilter();
        SlideIn();
        if (Grouped && groups.Count > 0) { expanded = -1; focusGroup = groupIndex; focusEntry = -1; } // stable: all closed
        Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
    }

    // stable's Shift+Left / Right (grouped): the group before / after the focused (else the open) one opens, wrapping
    void JumpGroup(int dir)
    {
        if (groups.Count == 0) return;
        int g = focusGroup >= 0 ? focusGroup : expanded >= 0 ? expanded : groupIndex;
        int to = ((g + dir) % groups.Count + groups.Count) % groups.Count;
        if (expanded != to) ToggleGroup(to);
        else { focusGroup = to; focusEntry = -1; }
    }

    void ToggleGroup(int g)
    {
        expanded = expanded == g ? -1 : g;
        focusGroup = g;
        focusEntry = -1;
        Game.Skin.Sound("select-expand")?.Play(BeatmapSamples.EffectVolume);
    }

    void SelectEntry(int g, int e)
    {
        bool changed = g != groupIndex || e != setIndex;
        if (g != groupIndex) { groupIndex = g; view.Clear(); view.AddRange(groups[g].Entries); viewStamp++; }
        setIndex = e;
        diffIndex = 0;
        expanded = g;
        focusGroup = -2;
        if (!changed) return;
        Game.Skin.Sound("select-difficulty")?.Play(BeatmapSamples.EffectVolume);
        SelectionChanged();
    }

    // grouped: the selection delta difficulties on through all the groups, opening the one it lands in
    void SelectRelative(int delta)
    {
        int total = 0, at = 0;
        for (int g = 0; g < groups.Count; g++) { if (g == groupIndex) at = total + setIndex; total += groups[g].Entries.Count; }
        if (total == 0) return;
        int to = ((at + delta) % total + total) % total, gi = 0; // wrapping, like stable
        while (to >= groups[gi].Entries.Count) { to -= groups[gi].Entries.Count; gi++; }
        SelectEntry(gi, to);
    }

    void MoveFocus(int delta)
    {
        if (RowCount == 0) return;
        var (e, _, g) = RowInfo(((FocusRow + delta) % RowCount + RowCount) % RowCount); // wrapping, like stable
        if (g >= 0) { focusGroup = g; focusEntry = -1; }
        else { focusGroup = expanded; focusEntry = e; }
    }

    void Sideways(int delta)
    {
        if (focusGroup >= 0 && focusEntry < 0) { ToggleGroup(focusGroup); return; }
        if (focusGroup >= 0 && (focusGroup != groupIndex || focusEntry != setIndex)) SelectEntry(focusGroup, focusEntry);
        else SelectRelative(delta);
    }

    // stable's date groups: under a day, two, a week; "1 month(s) ago" up to 30 days, then one per 30 days to 150;
    // "Over 5 months ago"; Never (none, or before 1999)
    static (int, string)? Ago(DateTime? at)
    {
        if (at is not { } t || t.Year < 1999) return (99, "Never");
        double days = (DateTime.UtcNow - t).TotalDays;
        if (days >= 150) return (98, "Over 5 months ago");
        int months = days < 7 ? 0 : days < 30 ? 1 : (int)(days / 30) + 1;
        return days < 1 ? (0, "Today") : days < 2 ? (1, "Yesterday") : days < 7 ? (2, "Last Week") : (2 + months, $"{months} month(s) ago");
    }

    // every difficulty's place in the sort, ranked once per sort, mods, library or score change (sorting with the
    // culture's text order on every keystroke costs ~130ms on a 50k-difficulty library)
    object? rankedFor;
    void RankDiffs()
    {
        var key = (SortMode, mods, sets);
        if (key.Equals(rankedFor)) return;
        rankedFor = key;
        var all = sets.SelectMany(s => s.Diffs).ToList();
        all.Sort(DiffOrder(mods));
        for (int i = 0; i < all.Count; i++) all[i].SortRank = i;
    }

    void BuildGroups(List<(BeatmapSet Set, List<DbBeatmap> Diffs)> all)
    {
        RankDiffs();
        groups.Clear();
        // Groups keep each set's matching difficulties only (a set can sit in several difficulty/date groups).
        void ByDiff(Func<DbBeatmap, (int Order, string Name)?> key)
        {
            var map = new SortedDictionary<int, (string Name, List<(BeatmapSet, List<DbBeatmap>)> Entries)>();
            foreach (var (set, diffs) in all)
                foreach (var part in diffs.Select(d => (Key: key(d), Diff: d)).Where(x => x.Key != null).GroupBy(x => x.Key!.Value))
                {
                    if (!map.TryGetValue(part.Key.Order, out var gr)) map[part.Key.Order] = gr = (part.Key.Name, []);
                    gr.Entries.Add((set, part.Select(x => x.Diff).ToList()));
                }
            foreach (var gr in map.Values) groups.Add(gr);
        }
        switch (GroupMode)
        {
            case "Artist" or "Title" or "Creator":
                ByDiff(d =>
                {
                    string s = GroupMode == "Title" ? d.Title : GroupMode == "Creator" ? d.Creator : d.Artist;
                    char c = s.Length > 0 ? char.ToUpperInvariant(s[0]) : '?';
                    return char.IsNumber(c) ? (0, "0-9") : c is >= 'A' and <= 'Z' ? (c, c.ToString()) : (101, "Other");
                });
                break;
            case "Bpm": // stable: "Under 60 BPM" ... "Under 300 BPM" in 60s, then "Over 300 BPM"
                ByDiff(d =>
                {
                    double bpm = d.MainBpm;
                    for (int q = 1; q <= 5; q++) if (bpm < q * 60 && bpm >= (q - 1) * 60) return (q, $"Under {q * 60} BPM");
                    return bpm > 300 ? (6, "Over 300 BPM") : null;
                });
                break;
            case "Length": // stable: "N minute(s) or less" per minute to 5, then 10, then "Over 10 minutes"
                ByDiff(d =>
                {
                    int ms = d.TotalMs;
                    for (int q = 1; q <= 5; q++) if (ms < q * 60000 && ms >= (q - 1) * 60000) return (q, $"{q} minute(s) or less");
                    return ms >= 600000 ? (7, "Over 10 minutes") : ms >= 300000 ? (6, "10 minute(s) or less") : null;
                });
                break;
            case "Date":
                ByDiff(d => Ago(d.LastModified > 0 ? new DateTime(d.LastModified, DateTimeKind.Utc) : null));
                break;
            case "Mode": // only osu!standard maps are listed here
                ByDiff(d => (0, "osu!"));
                break;
            case "Rank": // the local best: stable's silver/gold grades, then "No Rank Achieved"
                ByDiff(d => BestGrade(d) switch
                {
                    "XH" => (0, "Silver SS"), "SH" => (1, "Silver S"), "X" => (2, "SS"), "S" => (3, "S"), "A" => (4, "A"),
                    "B" => (5, "B"), "C" => (6, "C"), "D" => (7, "D"), _ => (8, "No Rank Achieved"),
                });
                break;
            case "Status": // osu!.db's ranked status
                ByDiff(d => d.Status switch
                {
                    4 or 5 => (0, "Ranked"), 6 => (1, "Qualified"), 2 => (2, "Pending"), 1 => (3, "Not Submitted"), 0 => (4, "Unknown"), 7 => (5, "Loved"), _ => null,
                });
                break;
            case "Difficulty":
                ByDiff(d =>
                {
                    float st = d.StarsFor(Mods.None);
                    int n = Math.Clamp((int)st, 0, 11);
                    return (n, n == 0 ? "Below 1 star" : n == 11 ? "Above 10 stars" : n == 1 ? "1 Star" : $"{n} Stars");
                });
                break;
            case "Recent":
                ByDiff(d => Ago(LastPlayedUtc(d)));
                break;
            case "Collections":
                collections ??= OsuDb.LoadCollections(Path.Combine(Config.OsuDir, "collection.db"));
                foreach (var (name, md5s) in collections.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var entries = all.Select(e => (e.Set, Diffs: e.Diffs.Where(d => md5s.Contains(d.Md5)).ToList())).Where(e => e.Diffs.Count > 0).ToList();
                    if (entries.Count > 0) groups.Add((name, entries));
                }
                break;
            case "MyMaps": // flat: the maps the player made (stable compares the creator with the signed-in name)
                string me = Config.Get("Username", "");
                groups.Add(("", Runs(all.Select(e => (e.Set, e.Diffs.Where(d => me.Length > 0 && d.Creator.Equals(me, StringComparison.OrdinalIgnoreCase)).ToList())))));
                return;
            case "Favourites": // flat: online favourites, none offline
                groups.Add(("", []));
                return;
            default:
                groups.Add(("", Runs(all)));
                return;
        }
        // stable lists every difficulty in a group as its own panel, in the sort's order
        for (int i = 0; i < groups.Count; i++)
            groups[i] = (groups[i].Name, [.. groups[i].Entries.SelectMany(e => e.Diffs.Select(d => (e.Set, new List<DbBeatmap> { d }))).OrderBy(e => e.Item2[0].SortRank)]);
    }

    // ungrouped: the difficulties in the sort's order, each set's neighbouring ones forming one entry (a run); a set
    // whose difficulties sort apart (by stars, say) has several
    List<(BeatmapSet Set, List<DbBeatmap> Diffs)> Runs(IEnumerable<(BeatmapSet Set, List<DbBeatmap> Diffs)> entries)
    {
        var runs = new List<(BeatmapSet Set, List<DbBeatmap> Diffs)>();
        foreach (var (set, d) in entries.SelectMany(e => e.Diffs.Select(d => (e.Set, d))).OrderBy(x => x.d.SortRank))
            if (runs.Count > 0 && runs[^1].Set == set) runs[^1].Diffs.Add(d);
            else runs.Add((set, [d]));
        return runs;
    }

    // stable's carousel (units): rows sit at 200 + 48 per row, and stable adds 10 more around each difficulty of the
    // opened set: (n + 1) x 10 across the block, and 10 before a group header that follows a non-header (here: the
    // first header after the open group's entries). It scrolls so the selected row's centre sits at 220 (never
    // above the first row); scrollRow eases toward the selection. Hovering a panel spreads the rows around it by 10.
    float Extra(float row)
    {
        if (Grouped)
        {
            if (expanded < 0) return 0;
            float e = Math.Clamp(row - expanded - ExpandedCount, 0, 1); // before the header after the entries
            if (expanded == groupIndex && view.Count > 0) e += Math.Clamp(row - (expanded + 1 + setIndex) + 1, 0, 2); // around the selection
            return 10 * U * e;
        }
        var gaps = Layout().Gaps;
        if (gaps.Length == 0) return 0;
        float r = Math.Clamp(row, 0, gaps.Length - 1);
        int f = (int)r;
        return 10 * U * (f + 1 < gaps.Length ? gaps[f] + (r - f) * (gaps[f + 1] - gaps[f]) : gaps[f]);
    }

    float Scroll => MathF.Max(0, scrollRow * RowH + Extra(scrollRow) - 20 * U);
    float RowCentre(int row) => 200 * U + row * RowH + Extra(row) - Scroll + (rowEase.TryGetValue(row, out var e) ? e.Y * U : 0);
    float RowY(int row) => RowCentre(row) - RowH / 2;

    // Panel x (units, from w/u - 340): the open set's difficulties and group headers 50 left, the hovered panel 45
    // more, plus min(200, |y/480 - 0.5| x 75) for the curve. Both x and the hover spread ease per row like stable's
    // (x by 0.95, y by 0.875 per 60Hz frame).
    readonly Dictionary<int, (float X, float Y)> rowEase = [];
    int hoveredRow = -1;
    double hoveredAt;

    // stable: a panel coming into view starts as far right of its target as its neighbour is (at most 200 units); with
    // no panel in view, 200. Rebuilding the list (entering song select, F5, a grouping change) pushes the whole list 500
    // units right while the panels are placed, which is how they slide in (then 0.95 per 60Hz frame like any panel).
    float rebuildPush;
    void SlideIn() { rowEase.Clear(); rebuildPush = 500; }

    float TargetX(int row)
    {
        var (_, d, gi) = RowInfo(row);
        return (gi >= 0 || d >= 0 ? -50 : 0) + (row == hoveredRow ? -45 : 0)
            + MathF.Min(200, MathF.Abs(RowCentre(row) / Game.H - 0.5f) * 75);
    }

    float PanelX(int row) => (Game.W / U - 340 + leaveShift + (rowEase.TryGetValue(row, out var e) ? e.X : TargetX(row))) * U;

    // stable keeps song select on screen for a second after a map is started: everything in its upper half slides 110
    // units up and the lower half 110 down (800ms Out), the leaderboard fades over 500ms and the list is pushed 500
    // units right (0.95 per 60Hz tick). The player draws this over itself (under its Visual Settings strip).
    float leaveShift, leaveSlide, scoreFade = 1;

    internal void DrawLeaving(double ms)
    {
        if (ms >= 1000 || loading != null) return;
        int w = Game.W, h = Game.H;
        float s = S;
        leaveShift = 500 * (1 - (float)Math.Pow(0.95, ms / (1000 / 60.0)));
        leaveSlide = 110 * U * (float)Easing.Apply(1, Math.Min(1, ms / 800));
        scoreFade = 1 - (float)Math.Min(1, ms / 500);
        // stable's big mode icon as the map starts: its colour flashes sky blue back to its own grey and it fades out
        // over 1s, swelling to twice its size (4s, Out) and turning a radian (4s, In)
        if (Game.Skin.Tex(ModeIcon()) is { } mode && ms < 1000)
        {
            float t1 = (float)(ms / 1000), t4 = (float)(ms / 4000);
            var tint = ColorLerp(Xna.SkyBlue, new Color(80, 80, 80, 255), t1);
            BeginBlendMode(BlendMode.Additive);
            mode.Draw(new Vector2(w / 2f, 240 * U), s * (1 + (float)Easing.Apply(1, t4)), ColorAlpha(tint, 70 / 255f * (1 - t1)), (float)Easing.Apply(2, t4) * 180 / MathF.PI);
            EndBlendMode();
        }
        int first = Math.Max(0, RowAt(0)), last = Math.Min(RowCount - 1, RowAt(h));
        UpdateThumbs();
        for (int row = last; row >= first; row--) DrawRow(row, w, h, s);
        Rlgl.PushMatrix();
        Rlgl.Translatef(0, -leaveSlide, 0);
        DrawTopBar(w, s);
        Rlgl.PopMatrix();
        DrawScores(w, h, s);
        Rlgl.PushMatrix();
        Rlgl.Translatef(0, leaveSlide, 0);
        DrawBottomBar(w, h, s);
        Rlgl.PopMatrix();
        (leaveShift, leaveSlide, scoreFade) = (0, 0, 1);
    }

    void UpdateCarousel(double dtMs, Vector2 mouse)
    {
        int h = Game.H;
        int under = mouse.Y > TopH && mouse.Y < h - BottomH && RowAt(mouse.Y) is var r && r >= 0 && r < RowCount && mouse.X >= PanelX(r) ? r : -1;
        if (under != hoveredRow)
        {
            hoveredRow = under;
            hoveredAt = Game.NowMs;
            if (under >= 0) Game.Skin.Sound("click-short")?.Play(BeatmapSamples.EffectVolume);
        }
        float fx = (float)Math.Pow(0.95, dtMs / (1000 / 60.0)), fy = (float)Math.Pow(0.875, dtMs / (1000 / 60.0));
        int first = Math.Max(0, RowAt(0) - 1), last = Math.Min(RowCount - 1, RowAt(h) + 1);
        foreach (int row in rowEase.Keys.Where(k => k < first || k > last).ToList()) rowEase.Remove(row);
        for (int row = first; row <= last; row++)
        {
            float spread = hoveredRow < 0 || row == hoveredRow ? 0 : row > hoveredRow ? 10 : -10;
            float tx = TargetX(row);
            if (rowEase.TryGetValue(row, out var e)) rowEase[row] = (tx - (tx - e.X) * fx, spread - (spread - e.Y) * fy);
            else
            {
                float lag = rowEase.TryGetValue(row - 1, out var n) ? n.X - TargetX(row - 1) : rowEase.TryGetValue(row + 1, out n) ? n.X - TargetX(row + 1) : float.PositiveInfinity;
                rowEase[row] = (tx + Math.Min(lag, 200 + rebuildPush), spread);
            }
        }
        rebuildPush = 0;
    }

    int RowAt(float y)
    {
        int row = (int)MathF.Floor((y - 200 * U + Scroll) / RowH);
        while (RowY(row) > y) row--;
        while (RowY(row + 1) <= y) row++;
        return row;
    }

    // the panel under a right-click becomes the selection (never plays); false off the panels or on a group header
    bool RightClickRow(float y)
    {
        int row = RowAt(y);
        if (row < 0 || row >= RowCount) return false;
        var (set, diff, group) = RowInfo(row);
        if (group >= 0) return false;
        if (Grouped) { if (diff < 0) SelectEntry(expanded, set); return true; }
        if (diff < 0) SelectRun(set);
        else if (set != setIndex || diff != diffIndex) { setIndex = set; diffIndex = diff; SelectionChanged(); }
        return true;
    }

    bool ClickRow(float y)
    {
        int row = RowAt(y);
        if (row < 0 || row >= RowCount) return false;
        var (set, diff, group) = RowInfo(row);
        if (group >= 0) { ToggleGroup(group); return true; }
        if (Grouped)
        {
            if (diff >= 0) Play(mods);
            else SelectEntry(expanded, set);
            return true;
        }
        if (diff < 0) SelectRun(set);
        else if (set == setIndex && diff == diffIndex) Play(mods);
        else { setIndex = set; diffIndex = diff; SelectionChanged(); }
        return true;
    }

    // ---------------------------------------------------------------- drawing (stable's layout)

    static string Length(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

    // stable's panel colours: other sets pink, the open set's difficulties blue, the selected one white.
    static readonly Color SetColour = new(235, 73, 153, 240), DiffColour = new(0, 150, 236, 240), SelectedColour = new(255, 255, 255, 240);

    // stable's background difficulty calculation: a notice once more than 50 maps wait (and one when they're done), and
    // the progress at song select's top right (size 12, right-aligned 5 units in, 1 above the top)
    static int ratingLeft, ratingTotal;
    static bool ratingNoticed;
    static void DrawRatingStatus(int w)
    {
        int left = Volatile.Read(ref ratingLeft);
        if (left <= 0) return;
        string text = left == ratingTotal ? "Difficulty calculation in progress..." : $"Difficulty calculation in progress ({left} beatmaps remaining)";
        float size = 12.4f * U;
        Game.Text(text, w - 5 * U - Game.TextWidth(text, size), -U, size, Color.White);
    }

    public override void Draw()
    {
        int w = Game.W, h = Game.H;
        float s = S;
        DrawBackground(w, h);
        DrawBackdrop(w, h, s);
        DrawModCaption();
        if (loading != null)
        {
            Game.TextBold(loadStage, 40 * s, 40 * s, 36 * s, Color.White);
            if (loadTotal > 0) Game.Text($"{loadDone} / {loadTotal}", 40 * s, 82 * s, 24 * s, Color.White);
            DrawCursor();
            return;
        }

        int first = Math.Max(0, RowAt(0)), last = Math.Min(RowCount - 1, RowAt(h));
        UpdateThumbs();
        for (int row = last; row >= first; row--) DrawRow(row, w, h, s);

        DrawTopBar(w, s);
        DrawScores(w, h, s); // over the top bar: stable's ranking box overlaps its lower edge
        DrawBottomBar(w, h, s);
        DrawRatingStatus(w);
        // stable: a new map (not while the random spin runs) restarts the header fade and flashes the screen white,
        // 10% fading out over 500ms
        if (Selected != fadeFor && !spinning)
        {
            fadeFor = Selected;
            headerFadeAt = Game.NowMs;
            int was = statusKind;
            statusKind = Selected?.Status ?? 0;
            if (statusKind != 1) // (a not submitted map keeps the last icon, fading it out)
            {
                string icon = statusKind switch { 4 => "selection-ranked", 5 or 6 => "selection-approved", 7 => "selection-loved", _ => "selection-question" };
                statusFadesIn = icon != statusIcon || was == 1;
                statusIcon = icon;
            }
        }
        if (Game.NowMs - headerFadeAt < 500) DrawRectangle(0, 0, w, h, ColorAlpha(Color.White, 0.1f * (1 - (float)((Game.NowMs - headerFadeAt) / 500))));
        Snow.Update(this);
        Snow.Draw();
        if (webTip && !modSelect && dialog == null) Options.DrawScreenTooltip("Quick web access for this beatmap", Input.GetMousePosition());
        else if (scoreTip != null && !modSelect && dialog == null) Options.DrawScreenTooltip(ScoreTip(scoreTip), Input.GetMousePosition());
        else if (diffTipShown && !modSelect && dialog == null && openDrop == 0) Options.DrawScreenTooltip(diffTip, Input.GetMousePosition());
        diffTipShown = false;
        webTip = false;
        scoreTip = null;
        DrawModeMenu();
        DrawDropdown();
        if (modSelect) DrawModSelect(w, h);
        DrawDialog();
        DrawCursor(); // over the overlays, like stable
    }

    // Group header: stable's group panel colours (open group lighter), "Name (N maps)".
    // stable's song select backdrop, between the background and the carousel: the mode icon at the screen centre (additive,
    // alpha 80 -> 50 over each beat), and star bursts: on every update in a window just before each beat (300ms early,
    // min(100ms, beat/10) long; beats over 150ms) five star2s fly left from 100 units past the right edge along the
    // selected panel (+-20 units), 140-900 units over a second (Out), growing 1 -> 2, fading out and turning randomly.
    // Stable spawns once per update, so its density follows its frame rate; here at most one burst per 4ms.
    readonly List<(double At, float Y, float Dist, float R0, float R1)> sparkles = [];
    double lastSparkle;

    void UpdateSparkles()
    {
        double now = Game.NowMs;
        sparkles.RemoveAll(sp => now - sp.At >= 1000);
        if (modSelect || view.Count == 0 || now - lastSparkle < 4 || Beat() is not { } b || b.Length <= 150) return;
        if (Math.Abs(MusicMs + 300 - b.RedTime) % b.Length >= Math.Min(100, b.Length / 10)) return;
        lastSparkle = now;
        float y = RowCentre(SelectedRow);
        for (int i = 0; i < 5; i++)
            sparkles.Add((now, y + Random.Shared.Next(-20, 20) * U, Random.Shared.Next(140, 900), Random.Shared.NextSingle() * 4 - 2, Random.Shared.NextSingle() * 4 - 2));
    }

    void DrawBackdrop(int w, int h, float s)
    {
        float u = U;
        if (Game.Skin.Tex(ModeIcon()) is { } mode)
        {
            double phase = BeatPhase() is var p && !double.IsNaN(p) ? p : 0;
            BeginBlendMode(BlendMode.Additive);
            mode.Draw(new Vector2(w / 2f, 240 * u), s, ColorAlpha(Color.White, (float)((80 - phase * 30) / 255 * 70 / 255))); // its colour carries alpha 70 too
            EndBlendMode();
        }
        if (sparkles.Count == 0 || Game.Skin.Tex("star2") is not { } star) return;
        double now = Game.NowMs;
        BeginBlendMode(BlendMode.Additive);
        foreach (var (at, y, dist, r0, r1) in sparkles)
        {
            float e = (float)Easing.Apply(1, (now - at) / 1000);
            star.Draw(new Vector2(w + (100 - dist * e) * u, y), (1 + e) * s, ColorAlpha(Color.White, 1 - e), (r0 + (r1 - r0) * e) * 180 / MathF.PI);
        }
        EndBlendMode();
    }

    // The panel sprite at scale 1, its left edge at x and its centre line at cy, tinted. A hovered panel flashes 30%
    // lighter and fades back over a second.
    void DrawPanel(int row, float x, float cy, float s, Color colour)
    {
        if (row == hoveredRow) colour = ColorLerp(ColorLerp(colour, Color.White, 0.3f), colour, (float)Math.Clamp((Game.NowMs - hoveredAt) / 1000, 0, 1));
        if (Game.Skin.Tex("menu-button-background") is { } panel)
            DrawTexturePro(panel.Tex, new Rectangle(0, 0, panel.Tex.Width, panel.Tex.Height),
                new Rectangle(x, cy - panel.Height * s / 2, panel.Width * s, panel.Height * s), Vector2.Zero, 0, colour);
        else DrawRectangleRec(new Rectangle(x, cy - 27 * U, 430 * U, 54 * U), colour);
    }

    // stable's header colours (measured): the expanded group yellow when focused, else lime (black text); a focused
    // header (50,72,205), the selection's (35,90,193), others (35,50,143).
    void DrawGroupHeader(int row, int g, float x, float cy, float s)
    {
        bool focused = focusGroup == g && focusEntry < 0, open = g == expanded;
        var colour = open ? focused ? new Color(237, 255, 64, 255) : new Color(170, 250, 46, 255)
            : focused ? new Color(50, 72, 205, 255) : g == groupIndex ? new Color(35, 90, 193, 255) : new Color(35, 50, 143, 255);
        DrawPanel(row, x, cy, s, colour);
        var (name, entries) = groups[g];
        int maps = entries.Sum(e => e.Diffs.Count);
        Game.Text(GroupLabel(name, maps), x + 15 * U, cy - 12 * U, 24 * U, open ? Color.Black : Color.White); // stable: 24 at (15, 0), centred
    }

    void DrawRow(int row, int w, int h, float s)
    {
        var (si, d, gi) = RowInfo(row);
        float x = PanelX(row), cy = RowCentre(row), u = U;
        if (gi >= 0) { DrawGroupHeader(row, gi, x, cy, s); return; }
        var set = RowEntries[si];
        bool entry = Grouped; // grouped: every panel is one difficulty (pink, the focused one brighter, the selected one white)
        var b = d < 0 ? set.Diffs[0] : set.Diffs[d];
        bool selected = d == diffIndex && si == setIndex && d >= 0;
        bool focused = !selected && (entry ? focusGroup == expanded && focusEntry == si : row == focusRow);
        // stable lightens a focused panel's colour by 1.4
        DrawPanel(row, x, cy, s, selected ? SelectedColour : d >= 0 && !entry ? focused ? new Color(0, 210, 255, 240) : DiffColour
            : focused ? new Color(255, 103, 214, 240) : SetColour);
        entry |= d < 0 && set.Diffs.Count == 1; // stable: a lone difficulty's panel shows its version and stars even closed

        // stable's panel text (offsets from the panel's left edge and centre line, in 640x480 units): 75 in when the
        // panel has a thumbnail slot (else 5) plus 8, or 25 beside a difficulty's grade icon; title 16 at -16, artist //
        // creator 12 at -4, version 12 (bold) at +7 (one further in), stars at +18. The grade icon sits at slot + 4.
        // Old skins' own panels (version < 2.2 with their own menu-button-background) take no thumbnail, and their
        // text sits 10 further right and 3 higher.
        var grade = (d >= 0 || entry) && BestGrade(b) is { } gr ? Game.Skin.Tex(SmallGrade(gr)) : null;
        bool old = OldPanels, slotted = !old && b.SetId > 0 && Config.GetBool("SongSelectThumbnails", true);
        float slot = slotted ? 75 : 5, edge = old ? 15 : 5, oy = old ? -3 * u : 0;
        if (slotted) DrawThumb(b, x, cy, u, s);
        var text = selected ? Game.Skin.SongSelectActiveText : Game.Skin.SongSelectInactiveText;
        // stable centres each line on its offset and its text bitmaps carry padding, so the glyphs sit 1.67 units right
        // of a plain top-left layout and a unit higher; its text also runs ~3% wider than ours at the same height
        // (measured on the default skin at 720p).
        float tx = x + (slot + edge + (grade != null ? 20 : 3)) * u, lx = tx + 1.67f * u, dy = oy - u;
        var faded = d >= 0 && !selected && !entry ? new Color(text.R, text.G, text.B, (byte)50) : text; // an open set's other difficulties: title and artist at alpha 50
        // a taiko / catch / mania map: its mode's icon before the text, which moves 16 units right (measured)
        if (b.Mode is >= 1 and <= 3 && Game.Skin.Tex(new[] { "", "mode-taiko-small", "mode-fruits-small", "mode-mania-small" }[b.Mode]) is { } modeIcon)
        {
            modeIcon.Draw(new Vector2(lx - 2 * u, cy - 12 * u + dy), 15 * u / Math.Max(1, modeIcon.Width), faded);
            lx += 16 * u; tx += 16 * u;
        }
        Game.Text(b.ShownTitle, lx, cy - 16 * u - 8 * u + dy, 16.55f * u, faded); // stable says 16; renders this wide (measured)
        Game.Text(b.PanelSubtitle, lx + u, cy - 4 * u - 6 * u + dy, 12.1f * u, faded);
        if (d < 0 && !entry) return;
        Game.TextBold(b.Version + KeyCountText(b), lx + u, cy + 7 * u - 6 * u + dy + u, 12.3f * u, text); // a further unit lower (measured)
        if (d >= 0 && RowEntries[si].Set != starsSet) { starsSet = RowEntries[si].Set; starsAt = Game.NowMs; } // a set just opened: pop its stars in
        float step = (Game.Skin.Tex("star")?.Width ?? 46) * 0.6f * s;
        DrawStars(b.StarsFor(mods), tx + step / 2, cy + oy + 18 * u, u, text, d >= 0 ? Game.NowMs - starsAt : double.MaxValue, old);
        if (grade is { } g) g.Draw(new Vector2(x + (edge + slot - 1) * u + g.Width * s / 2, cy + oy), s, Color.White);
    }

    static bool OldPanels => Game.Skin.Version < 2.2 && Game.Skin.Own("menu-button-background");

    // stable's song select thumbnails: the set's thumbnail from osu!'s own cache, Data\bt\<set id>.jpg (80x60, or
    // <set id>l.jpg at 160x120 when the window is 768+ tall), which stable fills while online; offline it shows what's
    // cached, and so does this. Centre-left 5.2 units into the panel at 1.425x (half that for the large ones). A
    // thumbnail loads once its panel has been on screen for 500ms (100 on the selected set; gaps under 200ms don't
    // count as leaving) and fades in over 400ms. Other sets' thumbnails are added onto the panel at grey 50; the
    // selected set's are drawn normally in white, fading in from nothing over a second when it gets selected (300ms
    // for the grey <-> white change). Checked against stable with test thumbnails at 1600x900.
    // Files are decoded off the main thread and textures unloaded once their panel has been out of sight for 5s.
    readonly Dictionary<int, (Sprite? Tex, double At, double Seen, double Since, bool Asked)> thumbs = [];
    readonly System.Collections.Concurrent.ConcurrentQueue<(int Set, Image Img)> thumbsLoaded = new();
    double thumbsSweptAt;
    string? thumbSet, thumbSetBefore;
    double thumbSetAt = double.NegativeInfinity, thumbSetBeforeAt = double.NegativeInfinity;

    void DrawThumb(DbBeatmap b, float x, float cy, float u, float s)
    {
        double now = Game.NowMs;
        if (Selected?.Folder != thumbSet) // the first selection on entering is instant, like stable's
            (thumbSetBefore, thumbSetBeforeAt, thumbSet, thumbSetAt) = (thumbSet, thumbSetAt, Selected?.Folder, thumbSet == null ? double.NegativeInfinity : now);
        bool sel = b.Folder == thumbSet, before = b.Folder == thumbSetBefore;
        thumbs.TryGetValue(b.SetId, out var t);
        if (now - t.Seen >= 200) t.Since = now;
        t.Seen = now;
        if (!t.Asked && now - t.Since >= (sel ? 100 : 500))
        {
            t.Asked = true;
            int set = b.SetId;
            string file = Path.Combine(Config.OsuDir, "Data", "bt", $"{set}{(Game.H >= 768 ? "l" : "")}.jpg");
            Task.Run(() => { if (File.Exists(file) && LoadImage(file) is { Width: > 0 } img) thumbsLoaded.Enqueue((set, img)); });
        }
        thumbs[b.SetId] = t;
        if (t.Tex is not { } tex) return;
        float lit = (float)Math.Clamp((now - thumbSetAt) / 300, 0, 1);
        lit = sel ? lit : before ? 1 - lit : 0;
        byte grey = (byte)(50 + 205 * lit);
        // alpha: the later of the load fade (400ms) and the selection fade (1000ms) restarts it from zero
        double selAt = sel ? thumbSetAt : before ? thumbSetBeforeAt : double.NegativeInfinity;
        double alpha = selAt > t.At ? (now - selAt) / 1000 : (now - t.At) / 400;
        float scale = 1.425f * s, w = tex.Width * scale;
        if (!sel) BeginBlendMode(BlendMode.Additive);
        tex.Draw(new Vector2(x + 5.2f * u + w / 2, cy + 0.25f * u), scale, new Color(grey, grey, grey, (byte)(255 * Math.Clamp(alpha, 0, 1))));
        if (!sel) EndBlendMode();
    }

    // once a frame: textures for decoded thumbnails, and the ones not drawn for 5s unloaded
    void UpdateThumbs()
    {
        double now = Game.NowMs;
        while (thumbsLoaded.TryDequeue(out var l))
        {
            if (!thumbs.TryGetValue(l.Set, out var t)) { UnloadImage(l.Img); continue; } // swept meanwhile
            var tex = LoadTextureFromImage(l.Img);
            UnloadImage(l.Img);
            SetTextureFilter(tex, TextureFilter.Bilinear);
            thumbs[l.Set] = t with { Tex = new Sprite(tex, Game.H >= 768 ? 0.5f : 1), At = now };
        }
        if (now - thumbsSweptAt < 1000) return;
        thumbsSweptAt = now;
        foreach (var (set, t) in thumbs.Where(kv => now - kv.Value.Seen > 5000).ToList())
        {
            if (t.Tex is { } sp) UnloadTexture(sp.Tex);
            thumbs.Remove(set);
        }
    }

    // stable's star row: ten faint stars (alpha 30) under the filled ones, the last filled one scaled by the
    // fractional part; the sprite at scale 0.6, spaced by its own width. x is the first star's centre.
    BeatmapSet? starsSet;
    double starsAt;

    static void DrawStars(float stars, float x, float y, float u, Color tint, double age = double.MaxValue, bool oldPanels = false)
    {
        if (stars <= 0 || stars > 100) return;
        // stable: spaced by 0.6 of the sprite's width; the faint row at 0.6 x 0.35, white, additive, alpha 30;
        // filled stars at 0.6 x 0.6 x max(0.5, fill + 0.04 per index), so later stars are a touch bigger.
        // When a set opens they pop in from nothing: BackOut over 500ms, the first after 50ms, then 80ms apart.
        var star = Game.Skin.Tex("star");
        float sd = u * 480 / 768f, step = (star?.Width ?? 46) * 0.6f * sd;
        for (int i = 0; i < 10; i++)
        {
            var c = new Vector2(x + i * step, y);
            float f = Math.Clamp(stars - i, 0, 1);
            float filled = f > 0 ? 0.36f * MathF.Max(0.5f, f + 0.04f * i) * (float)Easing.Apply(30, (age - 50 - 80 * i) / 500) : 0;
            if (star is { } sp)
            {
                BeginBlendMode(BlendMode.Additive);
                sp.Draw(c, (oldPanels ? 0.6f : 0.21f) * sd, new Color(255, 255, 255, 30)); // white on every panel (measured); old skins' panels keep them at 0.6
                EndBlendMode();
                if (filled > 0) sp.Draw(c, filled * sd, tint);
            }
            else if (filled > 0) DrawCircleV(c, step / 3 * filled / 0.6f, tint);
        }
    }

    string? BestGrade(DbBeatmap b)
    {
        if (!scores.TryGetValue(ScoreKey(b), out var list)) return null;
        string me = Config.Get("Username", ""); // like stable: the local player's own scores only (as Personal Best)
        foreach (var r in list) if (!r.FromStable || r.Player.Equals(me, StringComparison.OrdinalIgnoreCase)) return GradeOf(r);
        return null;
    }

    static string GradeOf(ReplayInfo r) => Player.GradeFor(r.Mode, r.Mods, r.N300, r.N100, r.N50, r.NMiss, r.NGeki, r.NKatu);

    // the scores are kept per map and mode (osu! ones under the plain MD5)
    internal static string ScoreKey(string md5, int mode) => mode == 0 ? md5 : $"{md5}:{mode}";
    static string ScoreKey(DbBeatmap b) => ScoreKey(b.Md5, b.Mode is >= 1 and <= 3 ? b.Mode : PlayMode);

    // stable's song select header (stable's sizes/positions, in 640x480 units; the right-hand group was
    // measured from a stable capture): map info top left, Group/Sort boxes and the group tabs top right, search at
    // the top of the song list.
    static Rectangle SortBox(int w) { float u = Game.H / 480f; return new Rectangle(w - 129 * u, 18 * u, 119 * u, 14 * u); }
    static Rectangle GroupBox(int w) => SortBox(w) with { X = SortBox(w).X - 168 * Game.H / 480f };
    // The group tabs: the selection-tab sprite at 0.6 units per texel (86 x 14), 73.9 apart so each overlaps the one
    // before, the first 392 units in from the right (measured).
    static Rectangle TabRect(int w, int t) { float u = Game.H / 480f; return new Rectangle(w - (392 - t * 73.9f) * u, 34 * u, 143 * 0.6f * u, 24 * 0.6f * u); }

    void DrawTopBar(int w, float s)
    {
        float u = Game.H / 480f;
        if (Game.Skin.Tex("songselect-top") is { } top) DrawTexturePro(top.Tex, new Rectangle(0, 0, top.Tex.Width, top.Tex.Height),
            new Rectangle(0, 0, Math.Max(w, top.Width * s), top.Height * s), Vector2.Zero, 0, Color.White);
        else DrawRectangle(0, 0, w, (int)TopH, new Color(0, 0, 0, 220));

        if (Selected is { } b)
        {
            // Ranked-status icon (osu!.db status: 4 ranked, 5 approved, 6 qualified, 7 loved).
            // Unknown / not submitted (an install that never went online): stable's question mark, at scale 1 (measured).
            // stable's icon is one of question / ranked / approved (also qualified) / loved, animated on each new map:
            // ranked, qualified and loved pop (0.92 -> 1.4 -> 1 over 400ms, In), approved swings in (-0.4 rad and 1.4 -> 1
            // over 400ms, Out), a not submitted map's icon shrinks to 0.8 and fades out over 500ms, and the others fade
            // in over 400ms when the icon changed (or was faded out)
            double st = Game.NowMs - headerFadeAt;
            float sc = 1, rot = 0, ia = 1;
            switch (statusKind)
            {
                case 4 or 6 or 7: sc = st < 200 ? 0.92f + 0.48f * (float)Easing.Apply(2, st / 200) : 1.4f - 0.4f * (float)Easing.Apply(2, (st - 200) / 200); break;
                case 5: rot = -0.4f * (1 - (float)Easing.Apply(1, st / 400)); sc = 1.4f - 0.4f * (float)Easing.Apply(1, st / 400); break;
                case 1: ia = 1 - (float)Math.Clamp(st / 500, 0, 1); sc = 1 - 0.2f * (float)Easing.Apply(1, st / 500); break;
                default: if (statusFadesIn) ia = (float)Math.Clamp(st / 400, 0, 1); break;
            }
            var iconTint = ColorAlpha(Color.White, ia);
            if (statusIcon == "selection-question" && Game.Skin.Tex(statusIcon) is { } q) q.Draw(new Vector2(12 * u, 12 * u), s * sc, iconTint, rot * 180 / MathF.PI);
            else if (Game.Skin.Tex(statusIcon) is { } icon) icon.Draw(new Vector2(11 * u, 11 * u), 15 * u / Math.Max(1, icon.Width) * sc, iconTint, rot * 180 / MathF.PI);
            if (headerFor != (b, mods, b.StarsFor(mods))) // the header text only changes with the selection, mods or star rating
            {
                headerFor = (b, mods, b.StarsFor(mods));
                double rate = mods.Rate();
                string bpm = b.MaxBpm <= 0 || b.MaxBpm > 100000 ? "?"
                    : Math.Abs(b.MaxBpm - b.MinBpm) < 0.5 ? $"{b.MaxBpm * rate:0}"
                    : $"{b.MinBpm * rate:0}-{b.MaxBpm * rate:0} ({b.MainBpm * rate:0})";
                int secs = (int)(b.TotalMs / 1000 / rate);
                float k = mods.HasFlag(Mods.HardRock) ? 1.4f : mods.HasFlag(Mods.Easy) ? 0.5f : 1;
                float kcs = mods.HasFlag(Mods.HardRock) ? 1.3f : k;
                float stars = b.StarsFor(mods);
                statArrow = rate > 1 ? 1 : rate < 1 ? -1 : 0;
                lengthColour = rate > 1 ? Harder : rate < 1 ? Easier : Color.White;
                statsColour = mods.HasFlag(Mods.HardRock) ? Harder : mods.HasFlag(Mods.Easy) ? Easier : Color.White; // speed mods leave it white (HT checked)
                (string artist, string title) = b.BothUnicode ? (b.ArtistUnicode, b.TitleUnicode) : (b.Artist, b.Title);
                header = [$"{(b.Source.Length > 0 ? $"{b.Source} ({artist})" : artist)} - {title} [{b.Version}]", // stable: "Source (Artist)" when there is one
                    $"Mapped by {b.Creator}",
                    $"Length: {secs / 60:00}:{secs % 60:00} BPM: {bpm} Objects: {b.Circles + b.Sliders + b.Spinners}",
                    $"Circles: {b.Circles} Sliders: {b.Sliders} Spinners: {b.Spinners}",
                    $"CS:{Math.Min(10, b.CS * kcs):0.#} AR:{Math.Min(10, b.AR * k):0.#} OD:{Math.Min(10, b.OD * k):0.#} HP:{Math.Min(10, b.HP * k):0.#}{(stars is > 0 and < 1000 ? $" Star Rating: {Math.Floor(stars * 100) / 100:0.##}" : "")}"]; // stable truncates (3.628 shows 3.62); the star glyph is drawn after. Not calculated yet: no rating (stable's)
                // stable: a second line when the map has a local or online offset
                int local = Config.GetInt($"LocalOffset_{b.Md5}", b.LocalOffset), online = b.OnlineOffset;
                offsetLine = local != 0 || online != 0 ? $"Your Offset: {(local > 0 ? "+" : "")}{local}ms / Online Offset: {(online > 0 ? "+" : "")}{online}ms" : "";
                double kd = mods.HasFlag(Mods.HardRock) ? 1.4 : mods.HasFlag(Mods.Easy) ? 0.5 : 1, kc = mods.HasFlag(Mods.HardRock) ? 1.3 : kd;
                double ar = Math.Min(10, b.AR * kd), od = Math.Min(10, b.OD * kd);
                double Window(double at0, double at5, double at10) => ((int)Beatmap.Range(od, at0, at5, at10) - 0.5) / rate;
                diffTip = $"Approach time: {(int)Beatmap.Range(ar, 1800, 1200, 450) / rate:0.##}ms\n300: +-{Window(80, 50, 20):0.##}ms\n100: +-{Window(140, 100, 60):0.##}ms\n" +
                    $"50: +-{Window(200, 150, 100):0.##}ms\nSpinner difficulty: {Beatmap.Range(od, 3, 5, 7.5):0.##}\nHit object radius: {new Beatmap { CS = Math.Min(10, b.CS * kc) }.Radius:0.##}";
            }
            // stable fades the lines in one after another whenever the map changes (and on entering): title 0-400ms,
            // creator 100-500, length 200-600, counts 200-700, stats and offset 300-800
            Color Faded(Color c, double from, double to) => ColorAlpha(c, (float)Math.Clamp((Game.NowMs - headerFadeAt - from) / (to - from), 0, 1) * c.A / 255f);
            var statsTint = Faded(statsColour, 300, 800);
            Game.Text(header[0], 23.4f * u, -3 * u, 18.4f * u, Faded(Color.White, 0, 400)); // stable's sizes 18, 12, 12 bold, 12, 8 render this wide (measured)
            Game.Text(header[1], 24.6f * u, 12 * u, 12.45f * u, Faded(Color.White, 100, 500));
            Game.TextBold(header[2], 2.5f * u, 24 * u, 12.5f * u, Faded(lengthColour, 200, 600));
            Game.Text(header[3], 2.5f * u, 36.5f * u, 12.4f * u, Faded(Color.White, 200, 700));
            // the stats word by word: the arrow (3x2.2 units, top at the digits' top, 0.5 after the value) follows AR, OD, HP
            float statX = 2 * u, size = 8.2f * u, space = Game.TextWidth(" ", size);
            var parts = header[4].Split(' ', 5);
            for (int p = 0; p < parts.Length; p++)
            {
                if (p > 0) statX += space;
                Game.Text(parts[p], statX, 48 * u, size, statsTint);
                statX += Game.TextWidth(parts[p], size);
                if (statArrow == 0 || p is 0 or 4) continue;
                float ax = statX + 0.5f * u, at = 50.2f * u, ab = at + 2.2f * u;
                if (statArrow > 0) DrawTriangle(new(ax + 1.5f * u, at), new(ax, ab), new(ax + 3 * u, ab), statsTint);
                else DrawTriangle(new(ax, at), new(ax + 1.5f * u, ab), new(ax + 3 * u, at), statsTint);
                statX += 3.5f * u;
            }
            if (parts.Length == 5) Options.DrawIcon(0xf005, statX + 4.5f * u, 52.5f * u, 8 * u, statsTint); // Aller has no star: stable's comes from a fallback font
            // stable: its own text 8 units under the stats line, left white with HR / EZ
            if (offsetLine.Length > 0) Game.Text(offsetLine, 2 * u, 56 * u, size, Faded(Color.White, 300, 800));
            diffTipShown = CheckCollisionPointRec(Input.GetMousePosition(), new Rectangle(2 * u, 47 * u, statX - 2 * u, 10 * u));
        }

        // Group / Sort: label, then a boxed value. Only sorting is functional; grouping shows stable's default.
        void Box(Rectangle r, string label, Color labelColour, string value, bool open)
        {
            float size = 23 * u; // measured sizes throughout: stable's text renders ~3-7% wider than its nominal size here
            Game.TextLight(label, r.X - 3 * u - Game.LightWidth(label, size), r.Y + r.Height - size * 0.8f + 2 * u, size, labelColour); // Aller Light
            DrawRectangleRec(r, open ? labelColour : Color.Black);
            DropdownBorder(r, new Color((byte)(labelColour.R * 0.83f), (byte)(labelColour.G * 0.83f), (byte)(labelColour.B * 0.83f), (byte)255)); // label colour at 83% (measured)
            Game.Text(value, r.X + 0.7f * u, r.Y + r.Height / 2 - 7.3f * u, 13.4f * u, Color.White);
            Options.DrawIcon(0xf078, r.X + r.Width - 9.8f * u, r.Y + r.Height / 2 + 0.7f * u, 12.5f * u, Color.White); // stable's chevron (measured)
        }
        var sortBox = SortBox(w);
        string sortLabel = SortModes.FirstOrDefault(m => m.Key.Equals(SortMode, StringComparison.OrdinalIgnoreCase)).Label ?? "By Artist";
        Box(sortBox, "Sort", new Color(174, 210, 139, 255), sortLabel, openDrop == 2);
        Box(GroupBox(w), "Group", new Color(146, 195, 230, 255), GroupList.FirstOrDefault(m => m.Key == GroupMode).Label ?? "No Grouping", openDrop == 1);

        // Group tabs (stable's selection-tab sprite), the current mode active.
        var tabs = GroupTabLabels;
        for (int t = 0; t < tabs.Length; t++)
        {
            bool active = GroupModes[t].Key == GroupMode;
            var tr = TabRect(w, t);
            if (Game.Skin.Tex("selection-tab") is { } tab)
                DrawTexturePro(tab.Tex, new Rectangle(0, 0, tab.Tex.Width, tab.Tex.Height), tr, Vector2.Zero, 0, active ? Color.White : new Color(235, 30, 70, 255));
            else DrawRectangleRec(tr, active ? Color.White : new Color(235, 30, 70, 255));
            float size = 9.9f * u;
            Game.TextBold(tabs[t], tr.X + tr.Width / 2 - Game.BoldWidth(tabs[t], size) / 2, tr.Y + tr.Height / 2 - size / 2, size, active ? Color.Black : Color.White);
        }

        // Search, at the top of the song list.
        float sx = w - 218 * u;
        string search = filter.Length == 0 ? "Type to search!" : filter;
        if (filter.Length == 0) DrawRectangleRec(new Rectangle(w - 302 * u, 50.7f * u, 302 * u, 11.3f * u), new Color(235, 73, 153, 195)); // the empty search box: a pink bar (measured)
        else DrawRectangleRec(new Rectangle(w - 227.5f * u, 51.1f * u, 227.5f * u, 29.9f * u), new Color(0, 0, 0, 95)); // a search typed: a dark box behind it (measured; stable's edges are soft)
        Game.TextBold("Search:", sx, 54 * u, 13.6f * u, new Color(173, 255, 47, 255)); // GreenYellow
        Game.TextBold(search, sx + Game.BoldWidth("Search: ", 13.6f * u), 54 * u, 13.6f * u, Color.White);
        if (message.Length > 0 || filter.Length > 0)
            Game.TextBold(message.Length > 0 ? message : matchCount == 0 ? "No matches found. Hit ESC to reset." : matchesText.Of(matchCount), sx, 66.5f * u, 11.35f * u, message.Length > 0 ? new Color(255, 120, 120, 255) : Color.White);
    }

    // The user panel (offline, so always the guest): name, "Click to sign in!" and an empty level bar (measured).
    // x, y: the name's text origin in units (song select 443, 430; main menu 53.1, 0.6).
    internal static void DrawUserPanel(float x, float y, float alpha)
    {
        float u = U;
        Game.TextLight("Guest", x * u, (y + 0.9f) * u, 14.5f * u, ColorAlpha(Color.White, alpha)); // measured at 1080p
        Game.TextLight("Click to sign in!", (x - 0.3f) * u, (y + 15) * u, 10.8f * u, ColorAlpha(Color.White, alpha));
        var bar = new Rectangle((x + 25.7f) * u, (y + 42.7f) * u, 123.3f * u, 6.7f * u);
        DrawRectangleRounded(bar, 1, 8, ColorAlpha(new Color(38, 38, 38, 255), alpha));
        DrawRectangleRoundedLinesEx(bar, 1, 8, Math.Max(1, u), ColorAlpha(new Color(90, 90, 90, 255), alpha));
    }

    // Bottom bar buttons: left edges at 140, 197.6, 245.6 and 293.6 units on widescreen, here in
    // 768-scale texels (units / 0.625). Hover swaps to the -over sprite like stable.
    static readonly (string Name, float X)[] BottomButtons = [("selection-mode", 224), ("selection-mods", 316.16f), ("selection-random", 392.96f), ("selection-options", 469.76f)];

    readonly BackButton backButton = new();

    Rectangle ButtonRect(string name, float x, int h, float s)
    {
        var sp = Game.Skin.Tex(name);
        float bw = (sp?.Width ?? 77) * s, bh = (sp?.Height ?? 90) * s;
        return new Rectangle(x * s, h - bh, bw, bh);
    }

    void DrawBottomBar(int w, int h, float s)
    {
        if (Game.Skin.Tex("songselect-bottom") is { } bottom) DrawTexturePro(bottom.Tex, new Rectangle(0, 0, bottom.Tex.Width, bottom.Tex.Height),
            new Rectangle(0, h - bottom.Height * s, w, bottom.Height * s), Vector2.Zero, 0, Color.White);
        else DrawRectangle(0, h - (int)BottomH, w, (int)BottomH, new Color(0, 0, 0, 220));

        var mouse = Input.GetMousePosition();
        backButton.Draw(h, mouse, Beat() is { } bt ? (bt.Phase * bt.Length, bt.Length) : null);
        foreach (var (name, bx) in BottomButtons)
        {
            var r = ButtonRect(name, bx, h, s);
            bool hover = CheckCollisionPointRec(mouse, r) || name == "selection-mods" && modSelect;
            var sp = (hover ? Game.Skin.Tex(name + "-over") : null) ?? Game.Skin.Tex(name);
            sp?.Draw(new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2), s, Color.White);
            if (hover && name != "selection-mode" && Game.Skin.Tex(name + "-over") == null) DrawRectangleRec(r, new Color(255, 255, 255, 40));
        }
        // Active mod icons next to the buttons.
        // The current mode's icon sits in the Mode button (additive, centred 28.8 units into it, 35 up).
        float u = h / 480f;
        if (Game.Skin.Tex(ModeIcon("-small")) is { } modeIcon)
        {
            BeginBlendMode(BlendMode.Additive);
            modeIcon.Draw(new Vector2((140 + 28.8f) * u, h - 35 * u), s, Color.White);
            EndBlendMode();
        }
        float mx = 546.56f * s; // 341.6 units: past Options, where stable continues
        foreach (var (flag, icon) in ModIcons)
            if (mods.HasFlag(flag) && !(flag == Mods.DoubleTime && mods.HasFlag(Mods.Nightcore)) && !(flag == Mods.SuddenDeath && mods.HasFlag(Mods.Perfect))
                && Game.Skin.Tex("selection-mod-" + icon) is { } m)
            {
                m.Draw(new Vector2(mx, h - 40 * s), 0.6f * s, Color.White);
                mx += 30 * s;
            }

        DrawUserPanel(443, 430, 1);

        // The osu! cookie: 40 units in from the right at y 450, scale 0.45 -> 0.4 over each beat, with a
        // copy swelling 0.45 -> 0.49 and fading 0.7 -> 0 (a 1s cycle without timing).
        if (Game.Skin.Tex("menu-osu") is { } cookie)
        {
            float p = (float)(BeatPhase() is var ph && !double.IsNaN(ph) ? ph : Game.NowMs % 1000 / 1000);
            var at = new Vector2(w - 40 * u, 450 * u);
            cookie.Draw(at, (0.45f - 0.05f * p) * s, Color.White);
            cookie.Draw(at, (0.45f + 0.04f * p) * s, ColorAlpha(Color.White, 0.7f * (1 - p)));
        }
    }

    internal static readonly (Mods Flag, string Icon)[] ModIcons =
    [
        (Mods.Easy, "easy"), (Mods.NoFail, "nofail"), (Mods.HalfTime, "halftime"), (Mods.HardRock, "hardrock"),
        (Mods.SuddenDeath, "suddendeath"), (Mods.Perfect, "perfect"), (Mods.DoubleTime, "doubletime"), (Mods.Nightcore, "nightcore"),
        (Mods.Hidden, "hidden"), (Mods.Flashlight, "flashlight"), (Mods.Relax, "relax"), (Mods.Autopilot, "relax2"),
        (Mods.SpunOut, "spunout"), (Mods.Autoplay, "autoplay"), (Mods.ScoreV2, "scorev2"),
        (Mods.FadeIn, "fadein"), (Mods.Random, "random"), (Mods.Mirror, "mirror"), (Mods.Key1, "key1"), (Mods.Key2, "key2"), (Mods.Key3, "key3"),
        (Mods.Key4, "key4"), (Mods.Key5, "key5"), (Mods.Key6, "key6"), (Mods.Key7, "key7"), (Mods.Key8, "key8"), (Mods.Key9, "key9"), (Mods.KeyCoop, "keycoop"),
    ];

    // ---- stable's mode menu (the Mode button): osu!, osu!taiko, osu!catch, osu!mania upward from y 344 at x 140 (units),
    // each a 280x80 box of black at 160/255 (purple (165,90,234,230) while hovered), the osu!ui mode-*-med icon centred
    // 40 in and the mode's name (bold 24, shadowed) 90 in; rows fade in over 200ms, 20ms apart, and out over 100ms.
    // Hovering grows the icon to 1.1 and nudges the name 10 units right. Any click closes it; only osu! is playable here.
    static readonly (string Icon, string Name)[] ModeRows = [("mode-osu-med", "osu!"), ("mode-taiko-med", "osu!taiko"), ("mode-fruits-med", "osu!catch"), ("mode-mania-med", "osu!mania")];
    // stable's play mode (LastPlayMode): song select lists that mode's maps, and in taiko / catch / mania the osu! maps too
    // (converted when played)
    internal static int PlayMode = DbBeatmap.ViewMode = Config.Get("LastPlayMode", "Osu") switch { "Taiko" => 1, "CatchTheBeat" => 2, "OsuMania" => 3, _ => 0 };
    static readonly string[] ModeKeys = ["Osu", "Taiko", "CatchTheBeat", "OsuMania"];
    internal static string ModeIcon(string size = "") => new[] { "mode-osu", "mode-taiko", "mode-fruits", "mode-mania" }[PlayMode] + size;
    // stable lists every map in every mode: taiko / catch / mania maps (marked with their mode's icon) play in their own
    // mode, osu! maps in the chosen one (converted)
    void SetMode(int mode)
    {
        if (mode == PlayMode) return;
        PlayMode = DbBeatmap.ViewMode = mode;
        Config.SetOwn("LastPlayMode", ModeKeys[mode]);
        mods = ModInfo.ForMode(mods, mode); // (the mods the new mode doesn't have go)
        rankedFor = null;
        ApplyFilter(Selected);
    }

    // stable's key count after a mania map's version (an osu! map's too in mania, where a single key mod picks it),
    // with an arrow when the map's OD rounds to 4 or less
    string KeyCountText(DbBeatmap b) => b.Mode == 3 || b.Mode == 0 && PlayMode == 3
        ? $" ({(b.Mode == 0 ? (mods.Keys() > 0 ? mods.Keys() : ManiaKeys(b)) * (mods.HasFlag(Mods.KeyCoop) ? 2 : 1) : ManiaKeys(b))}K{(Math.Round(b.OD) <= 4 ? "↓" : "")})" : "";

    double modeMenuAt = -1, modeMenuClosedAt = double.NegativeInfinity;
    int modeHover = -1;
    readonly double[] modeHoverAt = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
    readonly double[] modeLeaveAt = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];

    void OpenModeMenu() { modeMenuAt = Game.NowMs; modeHover = -1; }
    void CloseModeMenu() { if (modeMenuAt >= 0) { modeMenuAt = -1; modeMenuClosedAt = Game.NowMs; } }
    static Rectangle ModeRowRect(int i, float u) => new(140 * u, (344 - 80 * i) * u, 280 * u, 80 * u);

    // true when the menu took the click
    bool UpdateModeMenu(Vector2 mouse)
    {
        if (modeMenuAt < 0) return false;
        float u = U;
        int over = -1;
        for (int i = 0; i < ModeRows.Length; i++) if (CheckCollisionPointRec(mouse, ModeRowRect(i, u))) over = i;
        if (over != modeHover)
        {
            if (modeHover >= 0) modeLeaveAt[modeHover] = Game.NowMs;
            if (over >= 0) { modeHoverAt[over] = Game.NowMs; Game.Skin.Sound("click-short")?.Play(BeatmapSamples.EffectVolume); }
            modeHover = over;
        }
        if (Input.IsKeyPressed(KeyboardKey.Escape)) { CloseModeMenu(); return true; }
        if (!Input.IsMouseButtonPressed(MouseButton.Left) && !Input.IsMouseButtonPressed(MouseButton.Right)) return false;
        if (over >= 0) { Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume); SetMode(over); }
        CloseModeMenu();
        return over >= 0;
    }

    void DrawModeMenu()
    {
        double now = Game.NowMs;
        bool open = modeMenuAt >= 0;
        if (!open && now - modeMenuClosedAt > 100) return;
        float u = U, s = S;
        for (int i = 0; i < ModeRows.Length; i++)
        {
            float a = open ? (float)Math.Clamp((now - modeMenuAt - 20 * i) / 200, 0, 1) : 1 - (float)Math.Clamp((now - modeMenuClosedAt) / 100, 0, 1);
            if (a <= 0) continue;
            var r = ModeRowRect(i, u);
            bool hover = open && modeHover == i;
            var bg = new Color(0, 0, 0, 160);
            var lit = new Color(165, 90, 234, 230);
            var colour = hover ? ColorLerp(bg, lit, (float)Math.Clamp((now - modeHoverAt[i]) / 10, 0, 1))
                : ColorLerp(lit, bg, (float)Math.Clamp((now - modeLeaveAt[i]) / 100, 0, 1));
            DrawRectangleRec(r, ColorAlpha(colour, colour.A / 255f * a));
            // icon 1 -> 1.1 over a second (elastic) while hovered, and back
            double since = hover ? now - modeHoverAt[i] : now - modeLeaveAt[i];
            float e = (float)Easing.Apply(25, Math.Clamp(since / 1000, 0, 1));
            float grow = hover ? 1 + 0.1f * e : 1 + 0.1f * (1 - e);
            if (since > 1000 && !hover) grow = 1;
            if (Game.Skin.DefaultTex(ModeRows[i].Icon) is { } icon) icon.Draw(new Vector2(r.X + 40 * u, r.Y + 40 * u), s * grow, ColorAlpha(Color.White, a));
            float nudge = 10 * (float)Easing.Apply(1, Math.Clamp(since / 300, 0, 1));
            float tx = r.X + (93.5f + (hover ? nudge : 10 - nudge)) * u, size = 24.8f * u, ty = r.Y + 38.7f * u - size / 2; // measured at 1080p
            Game.TextBold(ModeRows[i].Name, tx + 1, ty + 1, size, ColorAlpha(Color.Black, 0.5f * a));
            Game.TextBold(ModeRows[i].Name, tx, ty, size, ColorAlpha(Color.White, a));
        }
    }

    bool ClickBottomBar(Vector2 mouse)
    {
        int w = Game.W, h = Game.H;
        float s = S;
        if (CheckCollisionPointRec(mouse, BackButton.Rect(h)))
        {
            Back();
            return true;
        }
        foreach (var (name, bx) in BottomButtons)
        {
            if (!CheckCollisionPointRec(mouse, ButtonRect(name, bx, h, s))) continue;
            Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
            if (name == "selection-mode") { if (modeMenuAt < 0) OpenModeMenu(); else CloseModeMenu(); }
            else if (name == "selection-mods") OpenModSelect(!modSelect);
            else if (name == "selection-random" && view.Count > 0) RandomKey(Input.ShiftHeld);
            else if (name == "selection-options") OpenBeatmapOptions(); // stable's Beatmap Options dialog
            return true;
        }
        if (Vector2.Distance(mouse, new Vector2(w - 70 * s, h - 60 * s)) < 60 * s) { Play(mods); return true; }
        return false;
    }

    // Local scores, left side, as stable's leaderboard panels.
    // stable's local leaderboard (measured from a stable capture, in 640x480 units): a ranking box at 73-88, then
    // up to eight rows of 33.3 from 96: rank, grade, name (16), "Score: N (Mx)" (12), and right-aligned mods /
    // accuracy / gap to the next score (10).
    static float U => Game.H / 480f;
    float ScoreTop => 96 * U;
    float ScoreRowH => 33.3f * U;
    int ScoreRowAt(float y) => (int)MathF.Floor((y - ScoreTop) / ScoreRowH);

    void DrawScoreRow(ReplayInfo r, float y, long? nextScore, bool hover)
    {
        float u = U;
        var white = ColorAlpha(Color.White, scoreFade);
        var row = new Rectangle(4.3f * u, y, 235.7f * u, ScoreRowH - 2 * u);
        DrawRectangleRec(row, hover ? new Color(100, 100, 100, (int)(77 * scoreFade)) : new Color(0, 0, 0, (int)(77 * scoreFade))); // stable: black at 0.3, grey (100) on hover (measured 10 -> 7 over a dark background)
        // (a row shows its place only while hovered: DrawScores)
        if (Game.Skin.Tex(SmallGrade(GradeOf(r))) is { } g) g.Draw(new Vector2(49 * u, y + 14 * u), 21 * u / Math.Max(1, g.Height), white);
        string name = r.FromStable ? (r.Player.Length > 0 ? r.Player : "stable") : Config.Get("Username", "You");
        Game.Text(name, 63 * u, y + 7.7f * u - 8 * u, 16 * u, white);
        var text = RowText(r);
        Game.Text(text.Score, 63 * u, y + 20.5f * u - 6 * u, 12 * u, white);
        float right = 237.7f * u, size = 10 * u;
        string[] lines = rowLines;
        lines[0] = text.Mods; lines[1] = text.Acc; lines[2] = nextScore is long nx && r.Score > nx ? leadText.Of(r.Score - nx) : "-";
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Length > 0) Game.Text(lines[i], right - Game.TextWidth(lines[i], size), y + (5 + 10.3f * i) * u - size / 2, size, white);
        // recent scores: an up-arrow circle and the age beside the row (measured at 1080p)
        if (AgoText(r) is { } ago)
        {
            Options.DrawIcon(0xf01b, 250 * u, y + 14.05f * u, 15.1f * u, white);
            Game.Text(ago, 261.3f * u, y + 13.8f * u - 5 * u, 10.8f * u, white);
        }
    }

    // stable's web access button (rank-forum, a 25-texel sprite at (214, 81) units) for maps with an online id: fades in/out over
    // 300ms, grows 1.1x while hovered. Offline it opens nothing; the tooltip stays.
    double webShownAt = double.MinValue;
    bool webShown;
    void DrawWebButton(float u)
    {
        bool show = Selected is { BeatmapId: > 0 };
        if (show != webShown) { webShown = show; webShownAt = Game.NowMs; }
        float f = (float)Math.Clamp((Game.NowMs - webShownAt) / 300, 0, 1), a = webShown ? f : 1 - f;
        if (a <= 0 || Game.Skin.Tex("rank-forum") is not { } tex) return;
        var at = new Vector2(214 * u, 81 * u);
        float sd = Game.H / 768f, half = 12.5f * sd;
        bool hover = webShown && Math.Abs(Input.GetMousePosition().X - at.X) < half && Math.Abs(Input.GetMousePosition().Y - at.Y) < half;
        tex.Draw(at, sd * (hover ? 1.1f : 1), ColorAlpha(Color.White, a));
        if (hover) webTip = true;
    }
    bool webTip;
    readonly float[] rankAlpha = new float[8];
    double rankFadeAt;
    int hoveredScore = -1;
    static readonly string[] PlaceTexts = ["1", "2", "3", "4", "5", "6", "7", "8"];
    ReplayInfo? scoreTip; // the score under the cursor
    ReplayInfo? scoreTipFor;
    string scoreTipText = "";

    // stable's tooltip over a local score: when, the judgement counts, accuracy and the mods by name ("None" without)
    string ScoreTip(ReplayInfo r)
    {
        if (r == scoreTipFor) return scoreTipText;
        scoreTipFor = r;
        var date = r.Date.Kind == DateTimeKind.Unspecified ? r.Date : r.Date.ToLocalTime();
        string mods = string.Join(",", CaptionOrder.Where(c => r.Mods.HasFlag(c.Flag)
            && !(c.Flag == Mods.DoubleTime && r.Mods.HasFlag(Mods.Nightcore)) && !(c.Flag == Mods.SuddenDeath && r.Mods.HasFlag(Mods.Perfect)))
            .Select(c => c.Flag == Mods.Autoplay ? "Auto" : c.Name));
        double acc = r.N300 + r.N100 + r.N50 + r.NMiss == 0 ? 100 : r.Accuracy;
        return scoreTipText = string.Format("Achieved on {0}\n300:{1} 100:{2} 50:{3} Miss:{4}\nAccuracy:{5:0.00}%\nMods:{6}",
            date, r.N300, r.N100, r.N50, r.NMiss, acc, mods.Length > 0 ? mods : "None");
    }

    // stable's dropdown outline (measured): about half a unit thick with ~1.3-unit rounded corners.
    void DropdownBorder(Rectangle r, Color c) =>
        DrawRectangleRoundedLinesEx(r, 2.6f * U / r.Height, 8, Math.Max(1, U / 2), c);

    string? noRecordsFor;
    double noRecordsAt;

    void DrawScores(int w, int h, float s)
    {
        float u = U;
        var box = new Rectangle(5 * u, 73 * u, 191 * u, 15 * u);
        Rlgl.PushMatrix();
        Rlgl.Translatef(0, -leaveSlide, 0);
        DrawRectangleRec(box, Color.Black);
        DropdownBorder(box, new Color(22, 130, 179, 255));
        Game.Text("Local Ranking", box.X + 3 * u, box.Y + box.Height / 2 - 7 * u, 14 * u, Color.White);
        Options.DrawIcon(0xf078, box.X + box.Width - 9.8f * u, box.Y + box.Height / 2 + 0.7f * u, 12.5f * u, Color.White); // stable's chevron (measured)
        DrawWebButton(u);
        Rlgl.PopMatrix();

        var list = SelectedScores;
        // stable: osu!ui's selection-norecords centred at (118, 170) units, fading in and sliding 30 down over 400ms (Out)
        if (Selected?.Md5 != noRecordsFor) { noRecordsFor = Selected?.Md5; noRecordsAt = Game.NowMs; }
        if (list.Count == 0 && Selected != null)
        {
            float k = (float)Math.Clamp((Game.NowMs - noRecordsAt) / 400, 0, 1);
            var at = new Vector2(118 * u, (170 + 30 * (float)Easing.Apply(1, k)) * u);
            if (Game.Skin.DefaultTex("selection-norecords") is { } nr) nr.Draw(at, s, ColorAlpha(Color.White, k * scoreFade));
            else Game.Text("No records set!", at.X - Game.TextWidth("No records set!", 14 * u) / 2, at.Y - 7 * u, 14 * u, ColorAlpha(Color.White, k * scoreFade));
        }
        var mouse = Input.GetMousePosition();
        double now = Game.NowMs;
        float step = (float)Math.Min(1, (now - rankFadeAt) / 100);
        rankFadeAt = now;
        int hovered = -1;
        for (int n = 0; n < Math.Min(8, list.Count); n++)
        {
            float y = ScoreTop + n * ScoreRowH;
            bool hover = mouse.X < 240 * u && ScoreRowAt(mouse.Y) == n && mouse.Y >= ScoreTop;
            DrawScoreRow(list[n], y, n + 1 < list.Count ? list[n + 1].Score : null, hover);
            if (hover) { scoreTip = list[n]; hovered = n; }
            // stable: a row's place shows while it's hovered (size 18, centred 18 units in and 15 down; in and out over
            // 100ms)
            rankAlpha[n] = Math.Clamp(rankAlpha[n] + (hover ? step : -step), 0, 1);
            if (rankAlpha[n] > 0)
            {
                string place = PlaceTexts[n];
                float ps = 18 * u;
                Game.Text(place, 22.3f * u - Game.TextWidth(place, ps) / 2, y + 15 * u - ps / 2, ps, ColorAlpha(Color.White, rankAlpha[n] * scoreFade));
            }
        }
        if (hovered >= 0 && hovered != hoveredScore) Game.Skin.Sound("click-short")?.Play(BeatmapSamples.EffectVolume); // stable's hover sound
        hoveredScore = hovered;
        // (stable shows its "Personal Best" box only for the online ranking types, never for Local Ranking)
    }
    // ---- stable's song select dialogs (F3 Beatmap Options and the ones it leads to): a title over numbered buttons;
    // number keys and clicks choose, Esc / F3 / Enter take the last one (stable's dialogs take Enter as a plain close).
    // A dialog with content of its own (Manage Collections) draws it between title and buttons, takes the typing
    // (number keys too) and may take Enter; its buttons can sit at a fixed height (units).
    sealed record DialogSpec(string Title, (string Label, Color Colour, Action? Act)[] Buttons, float? ButtonsY = null,
        Action<float>? Draw = null, Func<bool>? Update = null);
    DialogSpec? dialog;
    double dialogAt;
    float dialogY;
    static readonly Color OrangeRed = new(255, 69, 0, 255), OptionsPurple = new(207, 124, 215, 255), YellowGreen = new(154, 205, 50, 255);
    static readonly (string, Color, Action?) NoDelete = ("Nooooo! I didn't mean to!", Xna.Gray, null);

    void OpenDialog(string title, params (string, Color, Action?)[] buttons) { dialog = new(title, buttons); dialogAt = Game.NowMs; }

    void UpdateDialog()
    {
        var d = dialog!;
        var mouse = Input.GetMousePosition();
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        bool tookEnter = d.Update?.Invoke() == true;
        if (dialog != d) return; // its content opened another one
        int chosen = Input.IsKeyPressed(KeyboardKey.Escape) || Input.IsKeyPressed(KeyboardKey.F3) || !tookEnter && Input.IsKeyPressed(KeyboardKey.Enter) ? d.Buttons.Length - 1 : -1;
        for (int i = 0; i < d.Buttons.Length && chosen < 0; i++)
            if (d.Update == null && Input.IsKeyPressed(KeyboardKey.One + i) || click && CheckCollisionPointRec(mouse, Dialog.ButtonRect(dialogY + i * 50))) chosen = i;
        if (chosen < 0) return;
        dialog = null;
        d.Buttons[chosen].Act?.Invoke();
    }

    void DrawDialog()
    {
        if (dialog is not { } d) return;
        double age = Game.NowMs - dialogAt;
        float fade = (float)Math.Clamp(age / 200, 0, 1);
        Dialog.Backdrop(fade);
        dialogY = Dialog.Title(d.Title, fade);
        d.Draw?.Invoke(fade);
        dialogY = d.ButtonsY ?? dialogY;
        var mouse = Input.GetMousePosition();
        for (int i = 0; i < d.Buttons.Length; i++)
            Dialog.Button(i, d.Buttons[i].Label, d.Buttons[i].Colour, dialogY + i * 50, age, mouse);
    }

    // F3 / Beatmap Options: stable's dialog for the selected map
    void OpenBeatmapOptions()
    {
        if (Selected is not { } b) return;
        OpenDialog($"{b.Artist} - {b.Title}\nWhat do you want to do with this beatmap?",
            ("Manage Collections", new Color(154, 205, 50, 255), OpenCollections),
            ("Delete...", OrangeRed, () => OpenDelete(b)),
            ("Remove from Unplayed", OptionsPurple, () => RemoveFromUnplayed(b)),
            ("Clear local scores", OptionsPurple, () => OpenDialog("Are you sure you want to delete all local scores on this beatmap?", ("Let's do it!", YellowGreen, () => ClearScores(b)), ("Cancel", OrangeRed, null))),
            ("Edit", OrangeRed, () => OpenEditor(b)),
            ("Cancel", Xna.Gray, null));
    }

    // stable's Delete...: the whole set, this difficulty, or every difficulty the list shows (after a second question)
    // go to the Recycle Bin, silently like stable's. osu!.db keeps listing them until stable rewrites it; the library
    // load leaves out what's no longer on disk.
    void OpenDelete(DbBeatmap b)
    {
        bool several;
        try { several = Directory.EnumerateFiles(Path.Combine(Config.SongsDir, b.Folder), "*.osu").Skip(1).Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { several = false; }
        string name = $"{b.Artist} - {b.Title} [{b.Version}]", title = $"Are you sure you wish to delete '{name}' from disk?";
        if (several)
            OpenDialog(title, ("Yes, delete all difficulties!", OrangeRed, () => DeleteSet(b, name)),
                ("Yes, but only this difficulty", OrangeRed, () => DeleteMaps([b])),
                ("Delete ALL VISIBLE beatmaps!!", Xna.Red, DeleteVisible), NoDelete);
        else OpenDialog(title, ("Yes, delete this beatmap!", OrangeRed, () => DeleteSet(b, name)), NoDelete);
    }

    void DeleteSet(DbBeatmap b, string name)
    {
        ReleaseMusic();
        bool ok = Recycle([Path.Combine(Config.SongsDir, b.Folder)]);
        Toast.Show(ok ? $"{name} has been moved to Recycle Bin!" : $"{name} couldn't be moved to the Recycle Bin.", Xna.Red, 3000);
        Forget(Gone(sets.FirstOrDefault(s => s.Diffs.Contains(b))?.Diffs.ToList() ?? [b]));
    }

    void DeleteMaps(List<DbBeatmap> maps)
    {
        if (!Recycle(maps.Select(m => m.FilePath))) Toast.Show("Some beatmaps couldn't be moved to the Recycle Bin.", Xna.Red, 3000);
        Forget(Gone(maps));
    }

    void DeleteVisible()
    {
        // what the list shows: with grouping, the open group's maps (none when every group is closed)
        var shown = !Grouped ? view : expanded >= 0 && expanded < groups.Count ? groups[expanded].Entries : [];
        var visible = shown.SelectMany(v => v.Diffs).ToList();
        if (visible.Count == 0) Toast.Show("There are no maps visible!");
        else if (visible.Count == sets.Sum(s => s.Diffs.Count)) Toast.Show("You can't delete all your beatmaps at once!");
        else OpenDialog($"Are you sure you want to delete {visible.Count} beatmaps?", ("Let's do it!", YellowGreen, () => DeleteMaps(visible)), ("Cancel", OrangeRed, null));
    }

    // stable's Remove from Unplayed (written into osu!.db, see OsuDb.SetPlayed)
    void RemoveFromUnplayed(DbBeatmap b)
    {
        if (b.UnplayedAt > 0 && OsuDb.SetPlayed(b) < 0) { Toast.Show(CloseStableFirst); return; }
        b.Unplayed = false;
        Toast.Show($"Removed {b.Artist} - {b.Title} from unplayed songs.", Xna.SkyBlue, 3000);
        ApplyFilter(b);
    }

    const string CloseStableFirst = "Close osu!stable first: it rewrites osu!.db when it closes.";

    // Options > Maintenance > Mark all maps as played (stable's): every unplayed flag in osu!.db cleared
    internal void MarkAllPlayed()
    {
        if (OsuDb.SetAllPlayed() < 0) { Toast.Show(CloseStableFirst); return; }
        foreach (var set in sets) foreach (var b in set.Diffs) b.Unplayed = false;
        Toast.Show("All maps have been marked as 'Played'.", new Color(154, 205, 50, 255), 4000);
        ApplyFilter(Selected);
    }

    // Options > Maintenance > Delete all unranked maps (stable's): maps not submitted or pending (status 1 or 2) that
    // someone else made, in sets with no ranked map and none of the player's own; each one's whole set goes to the
    // Recycle Bin. Sets with a map osu!.db doesn't list are kept: the remake can't tell whether those are ranked (old
    // ranked maps have no set id in their .osu), nor whose a difficulty is that osu!.db doesn't know yet (one made in
    // the editor, say) in a folder it lists.
    internal List<DbBeatmap> UnrankedMaps()
    {
        string me = Config.Get("Username", "");
        bool Mine(DbBeatmap d) => me.Length > 0 && d.Creator == me;
        bool Unknown(List<DbBeatmap> diffs)
        {
            try
            {
                var known = diffs.Select(d => d.OsuFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return Directory.EnumerateFiles(Path.Combine(Config.SongsDir, diffs[0].Folder), "*.osu").Any(f => !known.Contains(Path.GetFileName(f)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
        }
        return [.. sets.Where(s => !s.Diffs.Any(d => d.Status == 4 || Mine(d) || d.Scanned) && !Unknown(s.Diffs)).SelectMany(s => s.Diffs).Where(d => d.Status is 1 or 2 && !Mine(d))];
    }

    internal void DeleteSetsOf(List<DbBeatmap> maps)
    {
        var folders = maps.Select(m => m.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (folders.Count == 0) return;
        ReleaseMusic();
        if (!Recycle(folders.Select(f => Path.Combine(Config.SongsDir, f)))) Toast.Show("Some beatmaps couldn't be moved to the Recycle Bin.", Xna.Red, 3000);
        Forget(Gone([.. sets.Where(s => folders.Contains(s.First.Folder)).SelectMany(s => s.Diffs)]));
    }

    // stable's Clear local scores empties the map's list in its scores.db. The remake's list comes from the replays
    // themselves, which stay where they are: it leaves out the map's scores up to the newest one at the time
    // (remake.cfg ClearedScores_<md5>), so later plays show again.
    void ClearScores(DbBeatmap b)
    {
        if (scores.Remove(b.Md5, out var list) && list.Count > 0)
            Config.SetOwn($"ClearedScores_{b.Md5}", list.Max(r => r.Date.Ticks).ToString(CultureInfo.InvariantCulture));
        rankedFor = null;
        Toast.Show($"Cleared all local scores for {b.Artist} - {b.Title}", new Color(50, 205, 50, 255), 3000);
    }

    static bool Cleared(ReplayInfo r) =>
        Config.Get($"ClearedScores_{r.BeatmapMd5}", "") is { Length: > 0 } v && long.TryParse(v, CultureInfo.InvariantCulture, out long t) && r.Date.Ticks <= t;

    // the music's file stays open while it plays or loads: let go of it before its folder goes (it restarts for the
    // next selection)
    void ReleaseMusic()
    {
        try { if (previewLoading != null) previewLoading.Result.Item2?.Dispose(); } catch (AggregateException) { }
        previewLoading = null;
        StopPreview();
    }

    // takes deleted maps out of the list, selecting the next one along (or the one before)
    void Forget(List<DbBeatmap> gone)
    {
        var g = gone.ToHashSet();
        var flat = view.SelectMany(v => v.Diffs).ToList();
        int at = Selected is { } s ? flat.IndexOf(s) : -1;
        var next = Selected is { } kept && !g.Contains(kept) ? kept
            : flat.Skip(at + 1).FirstOrDefault(d => !g.Contains(d)) ?? flat.Take(Math.Max(0, at)).LastOrDefault(d => !g.Contains(d));
        int before = sets.Sum(x => x.Diffs.Count);
        foreach (var set in sets) set.Diffs.RemoveAll(g.Contains);
        sets.RemoveAll(x => x.Diffs.Count == 0);
        TotalBeatmaps -= before - sets.Sum(x => x.Diffs.Count);
        ApplyFilter(next);
        if (keptSelection != null && g.Contains(keptSelection)) keptSelection = null; // (nothing left to show)
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct ShFileOp { public nint Hwnd; public uint Func; public string From; public string? To; public ushort Flags; public int Aborted; public nint Mappings; public string? Title; }
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SHFileOperationW(ref ShFileOp op);

    // stable's delete: to the Recycle Bin with no confirmation, progress or error UI (FO_DELETE; FOF_ALLOWUNDO,
    // FOF_SILENT, FOF_NOCONFIRMATION, FOF_NOERRORUI)
    // (only maps whose file is really gone leave the list: a delete that failed, all or in part, leaves the rest)
    static List<DbBeatmap> Gone(List<DbBeatmap> maps) => [.. maps.Where(m => !File.Exists(m.FilePath))];

    static bool Recycle(IEnumerable<string> paths)
    {
        IgnoreSongsUntil = double.MaxValue; // the watcher: our own deletes aren't news (until 2s after they finish)
        var op = new ShFileOp { Func = 3, From = string.Join("\0", paths.Select(Path.GetFullPath)) + "\0", Flags = 0x40 | 0x4 | 0x10 | 0x400 };
        int r = SHFileOperationW(ref op);
        if (r != 0) Console.Error.WriteLine($"Recycle Bin: error {r:x}");
        IgnoreSongsUntil = Game.NowMs + 2000;
        return r == 0 && op.Aborted == 0;
    }

    // stable's mod select: a dialog titled with its blurb, "Score Multiplier" centred 20 units above the content, three
    // labelled rows (size 24 at x 20; green, orange-red, white) of icons 66 units apart from x 240, rows 60 apart from 35
    // units under the content start, then Reset All Mods / Close. A selected icon springs to 1.2x and 0.1 rad (elastic,
    // 400ms) over a sky-blue glow. 1 / 2 press the buttons; stable's letter hotkeys toggle mods.
    // (per mode, like stable's: mania has Fade In / Hidden, the key mods, Mirror and Random; taiko and catch no Autopilot or
    // Spun Out. Mania's Key Coop isn't here.)
    static (string Label, Color Colour, (KeyboardKey Key, Mods Flag, Mods Alt, string Icon, string AltIcon)[] Mods)[] ModRows => modRows[PlayMode];
    static readonly (string Label, Color Colour, (KeyboardKey Key, Mods Flag, Mods Alt, string Icon, string AltIcon)[] Mods)[][] modRows =
        [.. Enumerable.Range(0, 4).Select(mode => new (string, Color, (KeyboardKey, Mods, Mods, string, string)[])[]
        {
            ("Difficulty Reduction", new Color(50, 205, 50, 255), [(KeyboardKey.Q, Mods.Easy, Mods.None, "easy", ""), (KeyboardKey.W, Mods.NoFail, Mods.None, "nofail", ""), (KeyboardKey.E, Mods.HalfTime, Mods.None, "halftime", "")]),
            ("Difficulty Increase", new Color(255, 69, 0, 255), [(KeyboardKey.A, Mods.HardRock, Mods.None, "hardrock", ""), (KeyboardKey.S, Mods.SuddenDeath, Mods.Perfect, "suddendeath", "perfect"),
                (KeyboardKey.D, Mods.DoubleTime, Mods.Nightcore, "doubletime", "nightcore"),
                mode == 3 ? (KeyboardKey.F, Mods.FadeIn, Mods.Hidden, "fadein", "hidden") : (KeyboardKey.F, Mods.Hidden, Mods.None, "hidden", ""),
                (KeyboardKey.G, Mods.Flashlight, Mods.None, "flashlight", "")]),
            ("Special", Color.White, mode switch
            {
                0 => [(KeyboardKey.Z, Mods.Relax, Mods.None, "relax", ""), (KeyboardKey.X, Mods.Autopilot, Mods.None, "relax2", ""), (KeyboardKey.C, Mods.SpunOut, Mods.None, "spunout", ""),
                    (KeyboardKey.V, Mods.Autoplay, Mods.Cinema, "autoplay", "cinema"), (KeyboardKey.B, Mods.ScoreV2, Mods.None, "scorev2", "")],
                3 => [(KeyboardKey.Z, ModInfo.KeyMods, Mods.None, "key4", ""), (KeyboardKey.C, Mods.KeyCoop, Mods.None, "keycoop", ""), (KeyboardKey.Null, Mods.Mirror, Mods.None, "mirror", ""), (KeyboardKey.X, Mods.Random, Mods.None, "random", ""),
                    (KeyboardKey.V, Mods.Autoplay, Mods.Cinema, "autoplay", "cinema"), (KeyboardKey.B, Mods.ScoreV2, Mods.None, "scorev2", "")],
                _ => [(KeyboardKey.Z, Mods.Relax, Mods.None, "relax", ""), (KeyboardKey.V, Mods.Autoplay, Mods.Cinema, "autoplay", "cinema"), (KeyboardKey.B, Mods.ScoreV2, Mods.None, "scorev2", "")],
            }),
        })];

    // stable's mode-specific texts over some mods
    static string? ModTip(Mods m) => (PlayMode, m) switch
    {
        (3, Mods.Easy) => "Reduces overall difficulty - more forgiving HP drain, less accuracy required.",
        (1, Mods.Easy) => "Reduces overall difficulty - notes move slower, less accuracy required.",
        (3 or 1, Mods.Hidden) => "The notes fade out before you hit them!",
        (3, Mods.FadeIn) => "The notes appear lower from the top!",
        (3, Mods.Random) => "Shuffle around the notes!",
        (3, Mods.KeyCoop) => "Double the key amount, double the fun!",
        (2, Mods.Relax) => "Use the mouse to control the catcher.\n** UNRANKED **",
        (1, Mods.Relax) => "Relax! You will no longer get dizzyfied by ninja-like spinners, demanding drumrolls or unexpected katu's.\n** UNRANKED **",
        _ => ModTips.TryGetValue(m, out var t) ? t : null,
    };
    const string ModBlurb = "Mods provide different ways to enjoy gameplay. Some have an effect on the score you can achieve during ranked play. Others are just for fun.";
    // stable's tooltips over the mod icons (its English texts); a two-state icon describes the state it shows
    static readonly Dictionary<Mods, string> ModTips = new()
    {
        [Mods.Easy] = "Reduces overall difficulty - larger circles, more forgiving HP drain, less accuracy required.",
        [Mods.NoFail] = "You can't fail. No matter what.",
        [Mods.HalfTime] = "Less zoom.",
        [Mods.HardRock] = "Everything just got a bit harder...",
        [Mods.SuddenDeath] = "Miss a note and fail.",
        [Mods.Perfect] = "SS or quit.",
        [Mods.DoubleTime] = "Zoooooooooom.",
        [Mods.Nightcore] = "uguuuuuuuu",
        [Mods.Hidden] = "Play with no approach circles and fading notes for a slight score advantage.",
        [Mods.Flashlight] = "Restricted view area.",
        [Mods.Relax] = "You don't need to click.\nGive your clicking/tapping fingers a break from the heat of things.\n** UNRANKED **",
        [Mods.Autopilot] = "Automatic cursor movement - just follow the rhythm.\n** UNRANKED **",
        [Mods.SpunOut] = "Spinners will be automatically completed.",
        [Mods.Autoplay] = "Watch a perfect automated play through the song.",
        [Mods.ScoreV2] = "Try the future scoring system.\n** UNRANKED **",
    };
    double modSelectAt, modButtonFlashAt = double.MinValue;
    int modButtonFlashed = -1;
    readonly Dictionary<Mods, (bool On, double At)> modAnim = [];
    Color multFrom = Color.White, multTo = Color.White, multNow = Color.White;
    double multAt;
    float modContentY; // units, from the last draw (the title wraps with the window width)

    void OpenModSelect(bool open)
    {
        modSelect = open;
        if (open) { modSelectAt = Game.NowMs; modButtonFlashed = -1; }
    }

    Vector2 ModIconPos(int row, int col) { float u = U; return new Vector2((240 + col * 66) * u, (modContentY + 35 + row * 60) * u); }

    void UpdateModSelect()
    {
        // each mod's key follows its binding (keyEasy ... keyAuto); Toggle works on stable's default keys
        for (int i = 0; i < ModKeys.Length; i++)
            if (Input.IsKeyPressed(Config.Key(ModBindings[i], ModKeys[i]))) mods = Input.ShiftHeld ? ModInfo.ShiftToggle(mods, ModKeys[i]) : ModInfo.Toggle(mods, ModKeys[i]);
        var mouse = Input.GetMousePosition();
        bool click = Input.IsMouseButtonPressed(MouseButton.Left), rclick = Input.IsMouseButtonPressed(MouseButton.Right);
        if (click || rclick)
            for (int r = 0; r < ModRows.Length; r++)
                for (int c = 0; c < ModRows[r].Mods.Length; c++)
                    if (Vector2.Distance(mouse, ModIconPos(r, c)) < 21 * U)
                        mods = click ? ModInfo.Toggle(mods, ModRows[r].Mods[c].Key) : ModInfo.ToggleBack(mods, ModRows[r].Mods[c].Key);
        float buttonsY = modContentY + 35 + 180;
        bool reset = Input.IsKeyPressed(KeyboardKey.One) || click && CheckCollisionPointRec(mouse, Dialog.ButtonRect(buttonsY));
        bool close = Input.IsKeyPressed(KeyboardKey.Two) || Input.IsKeyPressed(KeyboardKey.Escape) || Input.IsKeyPressed(KeyboardKey.Enter)
            || click && CheckCollisionPointRec(mouse, Dialog.ButtonRect(buttonsY + 50));
        if (reset) { mods = Mods.None; modButtonFlashed = 0; modButtonFlashAt = Game.NowMs; }
        if (close) OpenModSelect(false);
    }

    void DrawModSelect(int w, int h)
    {
        float u = U, s = S;
        double now = Game.NowMs, age = now - modSelectAt;
        float fade = (float)Math.Clamp(age / 200, 0, 1);
        Dialog.Backdrop(fade);
        modContentY = Dialog.Title(ModBlurb, fade);
        int ownKeys = PlayMode == 3 && Selected is { Mode: 0 } sb ? ManiaKeys(sb) : 0; // (a convert: Key Coop doubles the keys played, like stable)
        int keyChange = ownKeys > 0 ? (mods.Keys() > 0 ? mods.Keys() : ownKeys) * (mods.HasFlag(Mods.KeyCoop) ? 2 : 1) - ownKeys : 0;
        string mult = $"Score Multiplier: {mods.Multiplier(PlayMode, keyChange):0.00}x";
        float ms = 30.8f * u; // stable's 30, measured at 1080p
        // stable: the text fades over 400ms to green-yellow above 1x, orange-red below, white at 1x
        double mul = mods.Multiplier(PlayMode, keyChange);
        var multTarget = mul > 1 ? new Color(173, 255, 47, 255) : mul < 1 ? new Color(255, 69, 0, 255) : Color.White;
        if (multTarget.R != multTo.R || multTarget.G != multTo.G || multTarget.B != multTo.B) { multFrom = multNow; multTo = multTarget; multAt = now; }
        multNow = ColorLerp(multFrom, multTo, (float)Math.Clamp((now - multAt) / 400, 0, 1));
        Game.Text(mult, w / 2f - Game.TextWidth(mult, ms) / 2, (modContentY - 21.3f) * u - ms / 2, ms, ColorAlpha(multNow, fade));
        for (int r = 0; r < ModRows.Length; r++)
        {
            var (label, colour, list) = ModRows[r];
            float ls = 25.2f * u, cy = (modContentY + 35 + r * 60) * u;
            Game.Text(label, 22.7f * u, cy - ls / 2, ls, ColorAlpha(colour, fade)); // measured at 1080p
            for (int c = 0; c < list.Length; c++)
            {
                var (_, flag, alt, icon, altIcon) = list[c];
                bool on = (mods & flag) != 0 || alt != Mods.None && mods.HasFlag(alt);
                if (!modAnim.TryGetValue(flag, out var an) || an.On != on) modAnim[flag] = an = (on, now);
                float e = (float)Easing.Apply(25, (now - an.At) / 400);
                float scale = on ? 1 + 0.2f * e : 1.2f - 0.2f * e, rot = on ? 0.1f * e : 0.1f * (1 - e);
                var pos = ModIconPos(r, c);
                if (on && Game.Skin.Tex("lighting") is { } glow)
                {
                    BeginBlendMode(BlendMode.Additive);
                    glow.Draw(pos, s, ColorAlpha(Xna.SkyBlue, 0.2f * fade));
                    EndBlendMode();
                }
                string name = flag == ModInfo.KeyMods ? $"key{(mods.Keys() is > 0 and var k ? k : 4)}" : alt != Mods.None && mods.HasFlag(alt) ? altIcon : icon;
                Game.Skin.Tex("selection-mod-" + name)?.Draw(pos, scale * s, ColorAlpha(Color.White, fade), rot * 180 / MathF.PI);
            }
        }
        var mouse = Input.GetMousePosition();
        float buttonsY = modContentY + 35 + 180;
        Dialog.Button(0, "Reset All Mods", new Color(255, 69, 0, 255), buttonsY, age, mouse, modButtonFlashed == 0 ? now - modButtonFlashAt : double.MaxValue);
        Dialog.Button(1, "Close", Xna.Gray, buttonsY + 50, age, mouse);
        for (int r = 0; r < ModRows.Length; r++)
            for (int c = 0; c < ModRows[r].Mods.Length; c++)
                if (Vector2.Distance(mouse, ModIconPos(r, c)) < 21 * u)
                {
                    var (_, flag, alt, _, _) = ModRows[r].Mods[c];
                    if (ModTip(alt != Mods.None && mods.HasFlag(alt) ? alt : flag) is { } tip) Options.DrawScreenTooltip(tip, mouse); // (stable has no text for Cinema)
                }
    }

    // ---------------------------------------------------------------- background (loaded off the main thread)

    Texture2D bgTex;
    string bgKey = "";
    Task<(string Key, Image Img)>? bgLoading;
    double bgShownAt;
    Texture2D retiredBg; // the previous background, deleted a moment later: deleting it while the GPU may still use it stalled uploads ~15ms
    double retiredAt;

    void UpdateBackground()
    {
        if (retiredBg.Id != 0 && Game.NowMs - retiredAt > 100) { UnloadTexture(retiredBg); retiredBg = default; } // (deleted once the GPU is done with it)
        if (bgLoading is { IsCompleted: true })
        {
            var (key, img) = bgLoading.Result;
            bgLoading = null;
            if (key == bgKey && img.Width > 0)
            {
                if (bgTex.Id != 0) { if (retiredBg.Id != 0) UnloadTexture(retiredBg); (retiredBg, retiredAt) = (bgTex, Game.NowMs); }
                bgTex = LoadTextureFromImage(img);
                SetTextureFilter(bgTex, TextureFilter.Bilinear);
                bgShownAt = Game.NowMs;
            }
            if (img.Width > 0) UnloadImage(img);
        }
        if (Selected is not { } b || bgLoading != null || Game.NowMs - selectionChangedAt < 120) return;
        string file = b.FilePath;
        if (file == bgKey) return;
        bgKey = file;
        bgLoading = LoadBackgroundImage(file);
    }

    // Its own method so the closure is only made when a load starts (UpdateBackground runs every frame).
    static Task<(string, Image)> LoadBackgroundImage(string file) => Task.Run(() =>
    {
        try
        {
            var meta = Beatmap.Load(file, metadataOnly: true);
            var path = Path.Combine(meta.Folder, meta.Background);
            return (file, meta.Background.Length > 0 && File.Exists(path) ? LoadImage(path) : default);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return (file, default(Image)); } // (a malformed or unreadable map: no background)
    });

    internal void DrawBackground(int w, int h)
    {
        if (bgTex.Id == 0) return;
        // stable's menu background parallax (MenuParallax): 1/180 of the cursor's offset from the centre, scaled 1.01.
        bool parallax = Config.GetBool("MenuParallax", true);
        float k = Math.Max(w / (float)bgTex.Width, h / (float)bgTex.Height) * (parallax ? 1.01f : 1);
        var shift = parallax ? -(Input.GetMousePosition() - new Vector2(w / 2f, h / 2f)) / 180 : Vector2.Zero;
        float a = (float)Math.Clamp((Game.NowMs - bgShownAt) / 250, 0, 1) * 0.6f; // stable: the menu background at 60% (measured)
        DrawTexturePro(bgTex, new Rectangle(0, 0, bgTex.Width, bgTex.Height),
            new Rectangle((w - bgTex.Width * k) / 2 + shift.X, (h - bgTex.Height * k) / 2 + shift.Y, bgTex.Width * k, bgTex.Height * k),
            Vector2.Zero, 0, ColorAlpha(Color.White, a));
    }

    public static void DrawCursor() => DrawCursorAt(Input.GetMousePosition());

    // stable's classic cursor trail (skins without cursormiddle): at most every 60Hz frame a copy of cursortrail is
    // left at the cursor at its size (and its turn, with the skin's CursorTrailRotate), fading out over 150ms.
    static readonly List<(Vector2 Pos, double At, float Scale, float Rot)> trail = [];
    static double lastTrail = double.MinValue;
    static Vector2? smoothFrom;

    // Set by the player: stable's "Automatic cursor size" scales the cursor by 1 - 0.7 x (CS - 4) / 5 in play.
    internal static float CursorPlayScale = 1;

    // stable's cursor ripples (the Cursor ripples option): each click leaves the skin's cursor-ripple (else osu!ui's menu-osu-shockwave) at
    // the cursor, added on, growing 0.05 -> 0.5 (Out) and fading out from 20% over 700ms.
    static readonly List<(Vector2 Pos, double At)> ripples = [];
    // (stable leaves them out during active play)
    internal static void Ripple(Vector2 at) { if (Config.GetBool("CursorRipple", false) && Game.Screen is not (Player { ActivePlay: true } or Player { Cinema: true })) ripples.Add((at, Game.NowMs)); }

    static void DrawRipples()
    {
        ripples.RemoveAll(r => Game.NowMs - r.At > 700);
        if (ripples.Count == 0 || (Game.Skin.Own("cursor-ripple") ? Game.Skin.Tex("cursor-ripple") : Game.Skin.DefaultTex("menu-osu-shockwave")) is not { } tex) return;
        float sd = Game.H / 768f;
        BeginBlendMode(BlendMode.Additive);
        foreach (var (p, at) in ripples)
        {
            float f = (float)((Game.NowMs - at) / 700);
            tex.Draw(p, sd * (0.05f + 0.45f * (float)Easing.Apply(1, f)), ColorAlpha(Color.White, 0.2f * (1 - f)));
        }
        EndBlendMode();
    }

    // stable's cursor expand (skin CursorExpand, on by default): each newly pressed button grows the cursor 1 -> 1.3
    // over 100ms (Out), letting go of both shrinks it back to 1 over 100ms. In play the player's keys drive it
    // (live, a replay's or Auto's; bit 1|4 left, 2|8 right).
    internal static byte? CursorKeys;
    static int expandMask;
    static float expandFrom = 1, expandTo = 1;
    static double expandAt = double.MinValue;

    static float CursorExpandScale(double now)
    {
        float Current() => expandFrom + (expandTo - expandFrom) * (float)Easing.Apply(1, (now - expandAt) / 100);
        int mask = CursorKeys is byte k ? ((k & 5) != 0 ? 1 : 0) | ((k & 10) != 0 ? 2 : 0)
            : (Input.IsMouseButtonDown(MouseButton.Left) ? 1 : 0) | (Input.IsMouseButtonDown(MouseButton.Right) ? 2 : 0);
        if ((mask & ~expandMask) != 0) (expandFrom, expandTo, expandAt) = (1, 1.3f, now);
        else if (mask == 0 && expandMask != 0) (expandFrom, expandTo, expandAt) = (Current(), 1, now);
        expandMask = mask;
        return Game.Skin.CursorExpand ? Current() : 1;
    }

    // Screens ask for the cursor while drawing; it's drawn at the end of the frame (FlushCursor), over the volume meter
    // and notifications too, like stable's.
    static (Vector2 Pos, float PlayScale, byte? Keys)? pendingCursor;
    public static void DrawCursorAt(Vector2 pos) => pendingCursor = (pos, CursorPlayScale, CursorKeys);

    // stable hides the cursor in the main menu after 5s without input: out over 300ms, back in over 100ms.
    internal static bool CursorIdle;
    static float cursorAlpha = 1;
    static double cursorAlphaAt = double.NaN;

    internal static void FlushCursor()
    {
        double now = Game.NowMs, dt = double.IsNaN(cursorAlphaAt) ? 0 : now - cursorAlphaAt;
        cursorAlphaAt = now;
        cursorAlpha = CursorIdle ? Math.Max(0, cursorAlpha - (float)(dt / 300)) : Math.Min(1, cursorAlpha + (float)(dt / 100));
        CursorIdle = false; // set again by the menu every frame it's idle
        if (pendingCursor is not { } c) return;
        pendingCursor = null;
        (float scale, byte? keys) = (CursorPlayScale, CursorKeys);
        (CursorPlayScale, CursorKeys) = (c.PlayScale, c.Keys);
        DrawCursorNow(c.Pos);
        (CursorPlayScale, CursorKeys) = (scale, keys);
    }

    static void DrawCursorNow(Vector2 pos)
    {
        if (Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsMouseButtonPressed(MouseButton.Right)) Ripple(pos);
        DrawRipples();
        float s = Game.H / 768f * (float)Config.GetDouble("CursorSize", 1) * CursorPlayScale;
        var skin = Game.Skin;
        // CursorCentre: 0 anchors the sprite's top-left at the cursor instead of its centre.
        Vector2 Anchor(Sprite sp) => skin.CursorCentre ? pos : pos + new Vector2(sp.Width, sp.Height) * s / 2;
        double now = Game.NowMs;
        bool middle = skin.Own("cursor") ? skin.Own("cursormiddle") : skin.Tex("cursormiddle") != null;
        float e = s * CursorExpandScale(now);
        float turn = skin.CursorRotate ? (float)(now % 10000 / 10000 * 360) : 0; // a turn per 10s
        if (skin.Tex("cursortrail") is { Width: >= 5 } tr && !middle)
        {
            if (now - lastTrail >= 1000 / 60.0) { trail.Add((Anchor(tr), now, e, skin.CursorTrailRotate ? turn : 0)); lastTrail = now; }
            int kept = 0; // RemoveAll without a per-frame closure
            for (int i = 0; i < trail.Count; i++) if (now - trail[i].At < 150) trail[kept++] = trail[i];
            trail.RemoveRange(kept, trail.Count - kept);
            foreach (var (p, at, ts, tr0) in trail) tr.Draw(p, ts, ColorAlpha(Color.White, (1 - (float)((now - at) / 150)) * cursorAlpha), tr0);
        }
        else if (skin.Tex("cursortrail") is { Width: >= 5 } smooth)
        {
            // stable's smooth trail (with cursormiddle): copies every 1/2.5 of the sprite's width along the path,
            // added on, fading (1 - age/300ms)^1.7.
            var at = Anchor(smooth);
            float step = smooth.Width * s / 2.5f;
            if (smoothFrom is not { } from || Vector2.Distance(from, at) > Game.W) smoothFrom = at;
            else
            {
                var d = at - from;
                float len = d.Length();
                var dir = len > 0 ? d / len : Vector2.Zero; // one unit step direction for the whole walk
                while (step > 0 && len >= step)
                {
                    from += dir * step;
                    len -= step;
                    trail.Add((from, now, s, 0));
                }
                smoothFrom = from;
            }
            int kept = 0;
            for (int i = 0; i < trail.Count; i++) if (now - trail[i].At < 300) trail[kept++] = trail[i];
            trail.RemoveRange(kept, trail.Count - kept);
            BeginBlendMode(BlendMode.Additive);
            foreach (var (p, t, _, _) in trail) smooth.Draw(p, s, ColorAlpha(Color.White, MathF.Pow(1 - (float)((now - t) / 300), 1.7f) * cursorAlpha));
            EndBlendMode();
        }
        Vector2 Anchored(Sprite sp) => skin.CursorCentre ? pos : pos + new Vector2(sp.Width, sp.Height) * e / 2;
        if (skin.Tex("cursor") is { } c) c.Draw(Anchored(c), e, ColorAlpha(Color.White, cursorAlpha), turn);
        else DrawCircleV(pos, 12 * e, new Color(255, 200, 80, 255));
        if (middle && skin.Tex("cursormiddle") is { } m) m.Draw(Anchored(m), e, ColorAlpha(Color.White, cursorAlpha)); // the default cursor's blue centre
    }
}
