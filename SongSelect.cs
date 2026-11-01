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

sealed class SongSelect : Screen
{
    Task<List<BeatmapSet>>? loading;
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
    static float S => GetScreenHeight() / 768f;
    static float RowH => 76.8f * S; // stable's panel spacing: 144px at 1440p (measured on p057/p059)
    static float TopH => 100 * S;
    static float BottomH => 80 * S;

    // Gameplay's sprites decode in the background while a map is picked (and upload a frame at a time in Update).
    public override void Entered() => Game.Skin.PrefetchGameplay();

    public SongSelect(string filter = "", Mods mods = Mods.None)
    {
        this.filter = filter;
        this.mods = mods;
        loading = Task.Run(LoadLibrary);
        scoresLoading = Task.Run(Replays.Index);
    }

    // Local leaderboard (saved replays), keyed by beatmap MD5.
    Task<Dictionary<string, List<ReplayInfo>>>? scoresLoading;
    Dictionary<string, List<ReplayInfo>> scores = [];
    static readonly List<ReplayInfo> NoScores = []; // read-only: returned when a map has no scores
    List<ReplayInfo> SelectedScores => Selected is { } b && scores.TryGetValue(b.Md5, out var l) ? l : NoScores;
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
        if (!scores.TryGetValue(info.BeatmapMd5, out var list)) scores[info.BeatmapMd5] = list = [];
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

    static List<BeatmapSet> LoadLibrary()
    {
        List<DbBeatmap> all;
        try { all = OsuDb.Load(Path.Combine(Config.OsuDir, "osu!.db")); }
        catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
        {
            all = ScanFolders(); // TODO: slow fallback (minutes for big libraries) if osu!.db is missing/unreadable
        }
        TotalBeatmaps = all.Count;
        for (int i = 0; i < all.Count; i++) all[i].BuildSearchKey(i > 0 ? all[i - 1] : null); // here, not on the first keystroke
        return Sorted(all.Where(b => b.Mode == 0)
            .GroupBy(b => b.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BeatmapSet(g.OrderBy(b => b.StarsFor(Mods.None)).ToList())), SortMode);
    }

    // stable's sort modes (TreeSortMode2 in its cfg; ours is saved to remake.cfg).
    static readonly (string Key, string Label)[] SortModes =
        [("Artist", "By Artist"), ("Bpm", "By BPM"), ("Creator", "By Creator"), ("Date", "By Date Added"), ("Difficulty", "By Difficulty"), ("Length", "By Length"), ("Title", "By Title")];
    static string SortMode => Config.Get("TreeSortMode2", "Artist");
    internal static int TotalBeatmaps; // every mode, as stable's menu counts them

    static List<BeatmapSet> Sorted(IEnumerable<BeatmapSet> sets, string mode)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        return (mode switch
        {
            "Title" => sets.OrderBy(s => s.First.Title, cmp),
            "Creator" => sets.OrderBy(s => s.First.Creator, cmp),
            "Bpm" => sets.OrderBy(s => s.First.MainBpm),
            "Length" => sets.OrderBy(s => s.Diffs.Max(d => d.TotalMs)),
            "Difficulty" => sets.OrderBy(s => s.First.StarsFor(Mods.None)),
            "Date" => sets.OrderBy(s => s.Diffs.Max(d => d.LastModified)),
            _ => sets.OrderBy(s => s.First.Artist, cmp),
        }).ThenBy(s => s.First.Artist, cmp).ThenBy(s => s.First.Title, cmp).ToList();
    }

    void CycleSort(int dir)
    {
        int i = Array.FindIndex(SortModes, m => m.Key.Equals(SortMode, StringComparison.OrdinalIgnoreCase));
        Config.SetOwn("TreeSortMode2", SortModes[((i < 0 ? 0 : i) + dir + SortModes.Length) % SortModes.Length].Key);
        sets = Sorted(sets, SortMode);
        ApplyFilter();
        Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
    }

    internal static List<DbBeatmap> ScanFolders()
    {
        var list = new List<DbBeatmap>();
        var songs = Config.SongsDir;
        if (!Directory.Exists(songs)) return list;
        foreach (var dir in Directory.EnumerateDirectories(songs))
            foreach (var file in Directory.EnumerateFiles(dir, "*.osu"))
            {
                try
                {
                    var m = Beatmap.Load(file, metadataOnly: true);
                    list.Add(new DbBeatmap
                    {
                        Artist = m.Artist, Title = m.Title, Creator = m.Creator, Version = m.Version, AudioFile = m.AudioFilename,
                        OsuFile = Path.GetFileName(file), Folder = Path.GetFileName(dir), Mode = (byte)m.Mode, Md5 = Replays.Md5Of(file),
                        AR = (float)m.AR, CS = (float)m.CS, OD = (float)m.OD, HP = (float)m.HP, PreviewMs = (int)m.PreviewTime,
                    });
                }
                catch (IOException) { }
            }
        return list;
    }

    // ---------------------------------------------------------------- filtering

    // stable-style search: plain words must all appear; key<op>value terms filter numbers (stars ar cs od hp bpm length).
    bool Matches(DbBeatmap b, string[] words, List<(string Key, string Op, double Value)> terms)
    {
        foreach (var w in words)
            if (!b.SearchMatches(w)) return false;
        foreach (var (key, op, value) in terms)
        {
            double v = key switch
            {
                "stars" or "star" => b.StarsFor(mods), "ar" => b.AR, "cs" => b.CS, "od" => b.OD, "hp" => b.HP,
                "bpm" => b.MaxBpm, "length" => b.DrainSeconds, _ => double.NaN,
            };
            if (double.IsNaN(v)) continue;
            bool ok = op switch
            {
                "<" => v < value, ">" => v > value, "<=" => v <= value, ">=" => v >= value,
                _ => Math.Abs(v - value) < (key is "stars" or "star" ? 0.5 : 0.05),
            };
            if (!ok) return false;
        }
        return true;
    }

    // this difficulty's osu!.db entry (null before osu!.db has loaded)
    internal DbBeatmap? DbEntry(string md5)
    {
        foreach (var set in sets) foreach (var b in set.Diffs) if (b.Md5 == md5) return b;
        return null;
    }

    // stable's local offset for this difficulty (0 before osu!.db has loaded)
    internal short LocalOffsetOf(string md5) => DbEntry(md5)?.LocalOffset ?? 0;

    // stable's mod caption: the selected mods' names (in its order, "," between) in size 30 at (64.4, 397), 0.5 apart, white at
    // quarter alpha, under the panels
    static readonly (Mods Flag, string Name)[] CaptionOrder =
    [
        (Mods.ScoreV2, "ScoreV2"), (Mods.Autoplay, "Autoplay"), (Mods.SpunOut, "SpunOut"), (Mods.Easy, "Easy"), (Mods.NoFail, "NoFail"),
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
            caption = string.Join(",", CaptionOrder.Where(c => mods.HasFlag(c.Flag)
                && !(c.Flag == Mods.DoubleTime && mods.HasFlag(Mods.Nightcore)) && !(c.Flag == Mods.SuddenDeath && mods.HasFlag(Mods.Perfect))).Select(c => c.Name));
        }
        if (caption.Length == 0) return;
        float u = GetScreenHeight() / 480f;
        Game.Text(caption, 64.4f * u, 397 * u, 30 * u, new Color(255, 255, 255, 64), 0.5f * u);
    }

    internal void ApplyFilter()
    {
        var keep = view.Count > 0 && setIndex < view.Count ? view[setIndex].Set : null;
        var keepDiff = Selected;
        var all = new List<(BeatmapSet Set, List<DbBeatmap> Diffs)>();
        var words = new List<string>();
        var terms = new List<(string, string, double)>();
        foreach (var token in filter.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(token, @"^([a-z]+)(<=|>=|<|>|=)([\d.]+)$");
            if (m.Success && double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                terms.Add((m.Groups[1].Value, m.Groups[2].Value, v));
            else words.Add(token);
        }
        var wordArr = words.ToArray();
        // the options' "Display beatmaps from .. up to .. stars" (10 = no upper limit)
        double lo = Config.GetDouble("DisplayStarsMinimum", 0), hi = Config.GetDouble("DisplayStarsMaximum", 10);
        bool starRange = lo > 0 || hi < 10;
        bool InRange(DbBeatmap b) => !starRange || b.StarsFor(mods) is var st && st >= lo && (hi >= 10 || st <= hi);
        foreach (var set in sets)
        {
            var diffs = filter.Length == 0 && !starRange ? set.Diffs : set.Diffs.Where(b => InRange(b) && Matches(b, wordArr, terms)).ToList();
            if (diffs.Count > 0) all.Add((set, diffs));
        }
        BuildGroups(all);
        int g = keepDiff == null ? -1 : groups.FindIndex(gr => gr.Entries.Any(e => e.Diffs.Contains(keepDiff)));
        if (g < 0 && keep != null) g = groups.FindIndex(gr => gr.Entries.Any(e => e.Set == keep));
        groupIndex = g >= 0 ? g : Math.Clamp(groupIndex, 0, Math.Max(0, groups.Count - 1));
        view.Clear();
        if (groups.Count > 0) view.AddRange(groups[groupIndex].Entries);
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
                if (view.FindIndex(v => v.Diffs.Contains(prev)) is var i and >= 0) { MoveSet(i - setIndex); diffIndex = view[i].Diffs.IndexOf(prev); break; }
                else if (Grouped && groups.FindIndex(g => g.Entries.Any(e => e.Diffs.Contains(prev))) is var gi and >= 0)
                { SelectEntry(gi, groups[gi].Entries.FindIndex(e => e.Diffs.Contains(prev))); break; }
        }
        else if (spinning) spinning = false;
        else
        {
            int n = FlatCount;
            if (n < 2) return;
            if (Selected is { } cur) randomHistory.Push(cur);
            spinning = true;
            spinThreshold = 0.0001f;
            spinTicks = 0;
            spinTick = Game.NowMs;
            SelectFlat(Random.Shared.Next(n));
            Game.Skin.Sound("menuclick")?.Play(BeatmapSamples.EffectVolume);
        }
        Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
    }

    void UpdateSpin(double dtMs)
    {
        for (; spinning && Game.NowMs - spinTick >= 1000 / 60.0; spinTick += 1000 / 60.0)
        {
            if (!(spinTicks++ > spinThreshold)) continue;
            int n = FlatCount;
            if (n <= 1) { spinning = false; break; }
            Game.Skin.Sound("menuclick")?.Play(BeatmapSamples.EffectVolume * Math.Min(50, spinThreshold + 20) / 50);
            spinTicks = 0;
            spinThreshold *= 1.6f;
            if (spinThreshold > Random.Shared.Next(20, 50)) { spinning = false; break; }
            SelectFlat((FlatIndex() + 1) % n);
        }
    }

    void SelectionChanged() { selectionChangedAt = Game.NowMs; wheelTarget = null; }

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

    void MoveSet(int delta)
    {
        if (Grouped) { SelectRelative(delta); return; }
        if (view.Count == 0) return;
        int before = setIndex;
        setIndex = Math.Clamp(setIndex + delta, 0, view.Count - 1);
        diffIndex = 0;
        if (setIndex != before) Game.Skin.Sound("select-expand")?.Play(BeatmapSamples.EffectVolume);
        SelectionChanged();
    }

    void MoveDiff(int delta)
    {
        if (Grouped) { SelectRelative(delta); return; }
        if (view.Count == 0) return;
        Game.Skin.Sound("select-difficulty")?.Play(BeatmapSamples.EffectVolume);
        diffIndex += delta;
        if (diffIndex < 0)
        {
            if (setIndex == 0) diffIndex = 0;
            else { setIndex--; diffIndex = view[setIndex].Diffs.Count - 1; }
        }
        else if (diffIndex >= view[setIndex].Diffs.Count)
        {
            if (setIndex == view.Count - 1) diffIndex = view[setIndex].Diffs.Count - 1;
            else { setIndex++; diffIndex = 0; }
        }
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
    Task<List<TimingPoint>>? timingLoading;
    List<TimingPoint> timing = [];

    void UpdateTiming()
    {
        if (timingLoading is { IsCompleted: true })
        {
            timing = timingLoading.Result;
            timingLoading = null;
        }
        if (Current is not { } b || timingLoading != null || b.FilePath == timingKey) return;
        timingKey = b.FilePath;
        timingLoading = LoadTiming(b.FilePath);
    }

    // Its own method so the closure is only made when a load starts (UpdateTiming runs every frame).
    static Task<List<TimingPoint>> LoadTiming(string file) => Task.Run(() =>
    {
        try { return Beatmap.Load(file).TimingPoints; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
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

    void Play(Mods mods)
    {
        if (Selected is not { } b) return;
        try
        {
            var map = Beatmap.Load(b.FilePath, mods: mods);
            if (map.Objects.Count == 0) { message = "Map has no hit objects"; return; }
            StopPreview();
            Game.Skin.Sound("menuhit")?.Play(BeatmapSamples.EffectVolume);
            Game.Screen = new Player(map, this, mods);
        }
        catch (Exception e) when (e is not OutOfMemoryException) // a malformed map mustn't take the game down
        {
            message = $"Failed to load: {e.Message}";
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

    // The main menu we return to on Esc/back (null: quit instead).
    internal Screen? Menu;
    internal DbBeatmap? Current => Selected;
    internal double MusicMs => preview?.PositionMs ?? double.NaN;
    internal Track? Music => preview;

    // Library/score loading, preview music and background. The main menu runs this too, so its music and
    // background are song select's. Returns false while the library is still loading.
    internal bool Ambient(bool menu = false)
    {
        if (loading != null)
        {
            if (!loading.IsCompleted) return false;
            sets = loading.Result;
            loading = null;
            // every non-Latin character the library's names use, into the fallback glyph atlases at once (one build
            // instead of one each time a new title scrolls into view)
            Game.PreloadGlyphs(sets.SelectMany(s => s.Diffs).SelectMany(d => new[] { d.Title, d.TitleUnicode, d.Artist, d.ArtistUnicode, d.Version, d.Creator, d.Source }));
            ApplyFilter();
            if (filter.Length == 0 && view.Count > 0)
            {
                // stable's menu theme (MenuMusic): circles! from the start when it's in the library, else a random song.
                int theme = Menu != null && Config.GetBool("MenuMusic", true) ? view.FindIndex(v => v.Set.First.Artist == "nekodex" && v.Set.First.Title == "circles!") : -1;
                setIndex = theme >= 0 ? theme : Random.Shared.Next(view.Count);
                themeFromStart = theme >= 0;
            }
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
        if (Menu != null) Game.Screen = Menu;
        else Game.Quit = true;
    }

    public override void Update(double dtMs)
    {
        Game.Skin.UploadPrefetched();
        if (!Ambient()) return;

        if (Input.IsKeyDown(KeyboardKey.LeftControl) && Input.IsKeyPressed(KeyboardKey.O)) { Game.Screen = new Options(this, this); return; }

        // F1: stable's mod select, with stable's hotkeys.
        if (beatmapOptions) { while (Input.GetCharPressed() != 0) { } UpdateBeatmapOptions(); return; }
        if (Input.IsKeyPressed(KeyboardKey.F3) && !modSelect) { OpenBeatmapOptions(); return; }
        if (Input.IsKeyPressed(KeyboardKey.F1)) OpenModSelect(!modSelect);
        else if (modSelect)
        {
            while (Input.GetCharPressed() != 0) { } // keep mod hotkeys out of the search box
            UpdateModSelect();
            return;
        }
        if (modSelect) { while (Input.GetCharPressed() != 0) { } return; }
        if (modeMenuAt >= 0) // the mode menu takes every click and Esc while open
        {
            bool click = Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsMouseButtonPressed(MouseButton.Right), esc = Input.IsKeyPressed(KeyboardKey.Escape);
            UpdateModeMenu(Input.GetMousePosition());
            if (click || esc) { while (Input.GetCharPressed() != 0) { } return; }
        }

        // stable: Ctrl+A toggles Auto outside the mod select too
        if (Input.IsKeyDown(KeyboardKey.LeftControl) && Input.IsKeyPressed(KeyboardKey.A)) { mods = ModInfo.Toggle(mods, KeyboardKey.V); while (Input.GetCharPressed() != 0) { } }
        int page = Math.Max(1, (int)((GetScreenHeight() - TopH - BottomH) / RowH) - 2);
        if (Grouped)
        {
            if (Pressed(KeyboardKey.Down)) MoveFocus(1);
            if (Pressed(KeyboardKey.Up)) MoveFocus(-1);
            if (Pressed(KeyboardKey.Right)) Sideways(1);
            if (Pressed(KeyboardKey.Left)) Sideways(-1);
        }
        else
        {
            if (Pressed(KeyboardKey.Down)) MoveDiff(1);
            if (Pressed(KeyboardKey.Up)) MoveDiff(-1);
            if (Pressed(KeyboardKey.Right)) MoveSet(1);
            if (Pressed(KeyboardKey.Left)) MoveSet(-1);
        }
        if (Pressed(KeyboardKey.PageDown)) MoveSet(page);
        if (Pressed(KeyboardKey.PageUp)) MoveSet(-page);
        if (Input.IsKeyPressed(KeyboardKey.Home)) MoveSet(-view.Count);
        if (Input.IsKeyPressed(KeyboardKey.End)) MoveSet(view.Count);
        // F2: random map (stable's spin; F2 again stops it); Shift+F2: back to the one before the last random pick.
        if (Input.IsKeyPressed(KeyboardKey.F2) && view.Count > 0) RandomKey(Input.IsKeyDown(KeyboardKey.LeftShift) || Input.IsKeyDown(KeyboardKey.RightShift));
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
            if (Input.IsMouseButtonPressed(MouseButton.Right) && m.X >= 200 * U && m.Y > TopH && m.Y < GetScreenHeight() - BottomH
                && !CheckCollisionPointRec(m, GroupBox(GetScreenWidth())) && !CheckCollisionPointRec(m, SortBox(GetScreenWidth()))) rightScroll = true;
            if (!Input.IsMouseButtonDown(MouseButton.Right)) rightScroll = false;
            // (y - 70) / (480 - 150) of the way down the whole list
            if (rightScroll && !Input.IsMouseButtonDown(MouseButton.Left))
                wheelTarget = Math.Clamp((m.Y / U - 70) / 330, 0, 1) * MaxFreeScroll;
        }

        var mouse = Input.GetMousePosition();
        if ((Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsMouseButtonPressed(MouseButton.Right)) && CheckCollisionPointRec(mouse, GroupBox(GetScreenWidth())))
        {
            int gi = Array.FindIndex(GroupModes, m => m.Key == GroupMode);
            SetGroupMode(GroupModes[((gi < 0 ? 0 : gi) + (Input.IsMouseButtonPressed(MouseButton.Right) ? -1 : 1) + GroupModes.Length) % GroupModes.Length].Key);
            return;
        }
        for (int t = 0; t < GroupModes.Length; t++)
            if (Input.IsMouseButtonPressed(MouseButton.Left) && CheckCollisionPointRec(mouse, TabRect(GetScreenWidth(), t))) { SetGroupMode(GroupModes[t].Key); return; }
        if ((Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsMouseButtonPressed(MouseButton.Right)) && CheckCollisionPointRec(mouse, SortBox(GetScreenWidth())))
        {
            CycleSort(Input.IsMouseButtonPressed(MouseButton.Right) ? -1 : 1);
            return;
        }
        if (Input.IsMouseButtonPressed(MouseButton.Left) && ClickBottomBar(mouse)) return;
        if (Input.IsMouseButtonPressed(MouseButton.Left) && mouse.Y > TopH && mouse.Y < GetScreenHeight() - BottomH)
        {
            if (mouse.X < GetScreenWidth() * 0.35f)
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
        float k768 = 768f / GetScreenHeight();
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
        // Ctrl+1..9 watches that local score.
        if (Input.IsKeyDown(KeyboardKey.LeftControl))
            for (int n = 0; n < 9; n++)
                if (Input.IsKeyPressed(KeyboardKey.One + n) && n < SelectedScores.Count) { Watch(SelectedScores[n]); return; }
        // Ctrl+Enter: this one play with Auto, like stable (the selected mods stay as they are).
        if (Input.IsKeyPressed(KeyboardKey.Enter) && !Input.IsKeyDown(KeyboardKey.LeftAlt))
        {
            if (Grouped && focusGroup >= 0 && focusEntry < 0) { ToggleGroup(focusGroup); return; }
            if (Grouped && focusGroup >= 0 && (focusGroup != groupIndex || focusEntry != setIndex)) { SelectEntry(focusGroup, focusEntry); return; }
            Play(Input.IsKeyDown(KeyboardKey.LeftControl) ? mods & ~(Mods.Relax | Mods.Autopilot) | Mods.Autoplay : mods);
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
            filter = Input.IsKeyDown(KeyboardKey.LeftControl) ? "" : filter[..^1];
            changed = true;
            TypingSound("key-delete");
        }
        if (changed) ApplyFilter();

        scrollRow += ((wheelTarget ?? FocusRow) - scrollRow) * (float)Math.Min(1, dtMs / 80);
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
    int EntryRows => view.Count == 0 ? 0 : view.Count - 1 + view[setIndex].Diffs.Count;
    int SelectedRow => !Grouped ? setIndex + diffIndex
        : expanded == groupIndex && view.Count > 0 ? HeaderRow(expanded) + 1 + setIndex : HeaderRow(groupIndex);
    int FocusRow => !Grouped || focusGroup < -1 ? SelectedRow : focusEntry < 0 ? HeaderRow(focusGroup) : HeaderRow(focusGroup) + 1 + focusEntry;
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
        if (row < setIndex) return (row, -1, -1);
        int diffs = view[setIndex].Diffs.Count;
        if (row < setIndex + diffs) return (setIndex, row - setIndex, -1);
        return (row - diffs + 1, -1, -1);
    }

    // ---- grouping (stable's tabs): Collections, Recently Played, By Artist, By Difficulty, No Grouping.
    static readonly (string Key, string Label)[] GroupModes =
        [("Collections", "Collections"), ("Recent", "Recently Played"), ("Artist", "By Artist"), ("Difficulty", "By Difficulty"), ("None", "No Grouping")];
    static readonly string[] GroupTabLabels = [.. GroupModes.Select(m => m.Label)];
    static string GroupMode => Config.Get("RemakeGroupMode", "None");
    // Header text and match count, rebuilt only when they change.
    (DbBeatmap?, Mods) headerFor;
    string[] header = [];
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
        ($"Score: {r.Score:N0} ({r.MaxCombo}x)", r.Mods == Mods.None ? "" : string.Join(",", r.Mods.Short().Split(' ', StringSplitOptions.RemoveEmptyEntries)),
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
    static bool Grouped => GroupMode != "None";
    readonly List<(string Name, List<(BeatmapSet Set, List<DbBeatmap> Diffs)> Entries)> groups = [];
    int groupIndex;
    static List<(string Name, HashSet<string> Md5s)>? collections;

    void SetGroupMode(string key)
    {
        if (key == GroupMode) return;
        Config.SetOwn("RemakeGroupMode", key);
        groupIndex = 0;
        ApplyFilter();
        if (Grouped && groups.Count > 0) { expanded = -1; focusGroup = groupIndex; focusEntry = -1; } // stable: all closed
        Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
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
        if (g != groupIndex) { groupIndex = g; view.Clear(); view.AddRange(groups[g].Entries); }
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
        int to = Math.Clamp(at + delta, 0, total - 1), gi = 0;
        while (to >= groups[gi].Entries.Count) { to -= groups[gi].Entries.Count; gi++; }
        SelectEntry(gi, to);
    }

    void MoveFocus(int delta)
    {
        if (RowCount == 0) return;
        var (e, _, g) = RowInfo(Math.Clamp(FocusRow + delta, 0, RowCount - 1));
        if (g >= 0) { focusGroup = g; focusEntry = -1; }
        else { focusGroup = expanded; focusEntry = e; }
    }

    void Sideways(int delta)
    {
        if (focusGroup >= 0 && focusEntry < 0) { ToggleGroup(focusGroup); return; }
        if (focusGroup >= 0 && (focusGroup != groupIndex || focusEntry != setIndex)) SelectEntry(focusGroup, focusEntry);
        else SelectRelative(delta);
    }

    void BuildGroups(List<(BeatmapSet Set, List<DbBeatmap> Diffs)> all)
    {
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
            case "Artist":
                ByDiff(d =>
                {
                    char c = d.Artist.Length > 0 ? char.ToUpperInvariant(d.Artist[0]) : '?';
                    return char.IsNumber(c) ? (0, "0-9") : c is >= 'A' and <= 'Z' ? (c, c.ToString()) : (101, "Other");
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
                var now = DateTime.UtcNow;
                ByDiff(d =>
                {
                    // stable: under a day, two, a week; "1 month(s) ago" up to 30 days, then one per 30 days to 150.
                    // Played: stable's last play (osu!.db) or this client's latest saved replay, whichever is newer.
                    double days = d.Unplayed || d.LastPlayed <= 0 ? double.MaxValue : (now - new DateTime(d.LastPlayed, DateTimeKind.Utc)).TotalDays;
                    if (scores.TryGetValue(d.Md5, out var local) && local.Count > 0) days = Math.Min(days, (now - local.Max(r => r.Date).ToUniversalTime()).TotalDays);
                    if (days == double.MaxValue) return (99, "Never");
                    if (days >= 150) return (98, "Over 5 months ago");
                    int months = days < 7 ? 0 : days < 30 ? 1 : (int)(days / 30) + 1;
                    return days < 1 ? (0, "Today") : days < 2 ? (1, "Yesterday") : days < 7 ? (2, "Last Week") : (2 + months, $"{months} month(s) ago");
                });
                break;
            case "Collections":
                collections ??= OsuDb.LoadCollections(Path.Combine(Config.OsuDir, "collection.db"));
                foreach (var (name, md5s) in collections.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var entries = all.Select(e => (e.Set, Diffs: e.Diffs.Where(d => md5s.Contains(d.Md5)).ToList())).Where(e => e.Diffs.Count > 0).ToList();
                    if (entries.Count > 0) groups.Add((name, entries));
                }
                break;
            default:
                groups.Add(("", all));
                return;
        }
        // stable lists every difficulty in a group as its own panel, in the sort's order (by stars when sorting by difficulty)
        for (int i = 0; i < groups.Count; i++)
        {
            var flat = groups[i].Entries.SelectMany(e => e.Diffs.Select(d => (e.Set, new List<DbBeatmap> { d })));
            if (SortMode == "Difficulty") flat = flat.OrderBy(e => e.Item2[0].StarsFor(Mods.None));
            groups[i] = (groups[i].Name, flat.ToList());
        }
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
        if (view.Count == 0) return 0;
        int n = view[setIndex].Diffs.Count;
        float start = setIndex;
        int lead = start > 0 ? 1 : 0; // the gap before the first difficulty needs a panel above it
        return 10 * U * Math.Clamp(row - start + lead, 0, n + lead);
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

    float TargetX(int row)
    {
        var (_, d, gi) = RowInfo(row);
        return (gi >= 0 || d >= 0 ? -50 : 0) + (row == hoveredRow ? -45 : 0)
            + MathF.Min(200, MathF.Abs(RowCentre(row) / GetScreenHeight() - 0.5f) * 75);
    }

    float PanelX(int row) => (GetScreenWidth() / U - 340 + (rowEase.TryGetValue(row, out var e) ? e.X : TargetX(row))) * U;

    void UpdateCarousel(double dtMs, Vector2 mouse)
    {
        int h = GetScreenHeight();
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
            rowEase[row] = rowEase.TryGetValue(row, out var e) ? (tx - (tx - e.X) * fx, spread - (spread - e.Y) * fy) : (tx, spread);
        }
    }

    int RowAt(float y)
    {
        int row = (int)MathF.Floor((y - 200 * U + Scroll) / RowH);
        while (RowY(row) > y) row--;
        while (RowY(row + 1) <= y) row++;
        return row;
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
        if (diff < 0)
        {
            if (set != setIndex) MoveSet(set - setIndex);
        }
        else if (diff == diffIndex) Play(mods);
        else { diffIndex = diff; SelectionChanged(); }
        return true;
    }

    // ---------------------------------------------------------------- drawing (stable's layout)

    static string Length(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

    // stable's panel colours: other sets pink, the open set's difficulties blue, the selected one white.
    static readonly Color SetColour = new(235, 73, 153, 240), DiffColour = new(0, 150, 236, 240), SelectedColour = new(255, 255, 255, 240);

    public override void Draw()
    {
        int w = GetScreenWidth(), h = GetScreenHeight();
        float s = S;
        DrawBackground(w, h);
        DrawBackdrop(w, h, s);
        DrawModCaption();
        if (loading != null)
        {
            Game.TextBold("Loading osu!.db…", 40 * s, 40 * s, 36 * s, Color.White);
            DrawCursor();
            return;
        }

        int first = Math.Max(0, RowAt(0)), last = Math.Min(RowCount - 1, RowAt(h));
        for (int row = last; row >= first; row--) DrawRow(row, w, h, s);

        DrawTopBar(w, s);
        DrawScores(w, h, s); // over the top bar: stable's ranking box overlaps its lower edge
        DrawBottomBar(w, h, s);
        Snow.Update(this);
        Snow.Draw();
        if (webTip && !modSelect && !beatmapOptions) Options.DrawTooltip("Quick web access for this beatmap", Input.GetMousePosition());
        webTip = false;
        DrawModeMenu();
        if (modSelect) DrawModSelect(w, h);
        if (beatmapOptions) DrawBeatmapOptions();
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
        if (Game.Skin.Tex("mode-osu") is { } mode)
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
        var b = d < 0 ? entry ? set.Diffs[0] : set.Set.First : set.Diffs[d];
        bool selected = d == diffIndex && si == setIndex && d >= 0;
        bool focused = entry && !selected && focusGroup == expanded && focusEntry == si;
        DrawPanel(row, x, cy, s, selected ? SelectedColour : focused ? new Color(255, 103, 214, 240) : d >= 0 && !entry ? DiffColour : SetColour);

        // stable's panel text (offsets from the panel's left edge and centre line, in 640x480 units): 75 in (room for
        // the online thumbnail) plus 8, or 25 beside a difficulty's grade icon; title 16 at -16, artist // creator 12
        // at -4, version 12 (bold) at +7 (one further in), stars at +18. The grade icon sits at 79.
        var grade = (d >= 0 || entry) && BestGrade(b) is { } gr ? Game.Skin.Tex(SmallGrade(gr)) : null;
        var text = selected ? Game.Skin.SongSelectActiveText : Game.Skin.SongSelectInactiveText;
        // stable centres each line on its offset and its text bitmaps carry padding, so the glyphs sit 1.67 units right
        // of a plain top-left layout and a unit higher; its text also runs ~3% wider than ours at the same height
        // (measured on the default skin at 720p).
        float tx = x + (grade != null ? 100 : 83) * u, lx = tx + 1.67f * u, dy = -u;
        var faded = d >= 0 && !selected && !entry ? new Color(text.R, text.G, text.B, (byte)50) : text; // an open set's other difficulties: title and artist at alpha 50
        Game.Text(b.ShownTitle, lx, cy - 16 * u - 8 * u + dy, 16.55f * u, faded); // stable says 16; renders this wide (measured)
        Game.Text(b.PanelSubtitle, lx + u, cy - 4 * u - 6 * u + dy, 12.1f * u, faded);
        if (d < 0 && !entry) return;
        Game.TextBold(b.Version, lx + u, cy + 7 * u - 6 * u + dy + u, 12.3f * u, text); // a further unit lower (measured)
        if (d >= 0 && RowEntries[si].Set != starsSet) { starsSet = RowEntries[si].Set; starsAt = Game.NowMs; } // a set just opened: pop its stars in
        float step = (Game.Skin.Tex("star")?.Width ?? 46) * 0.6f * s;
        DrawStars(b.StarsFor(mods), tx + step / 2, cy + 18 * u, u, text, d >= 0 ? Game.NowMs - starsAt : double.MaxValue);
        if (grade is { } g) g.Draw(new Vector2(x + 79 * u + g.Width * s / 2, cy), s, Color.White);
    }

    // stable's star row: ten faint stars (alpha 30) under the filled ones, the last filled one scaled by the
    // fractional part; the sprite at scale 0.6, spaced by its own width. x is the first star's centre.
    BeatmapSet? starsSet;
    double starsAt;

    static void DrawStars(float stars, float x, float y, float u, Color tint, double age = double.MaxValue)
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
                sp.Draw(c, 0.21f * sd, new Color(255, 255, 255, 30)); // white on every panel (measured)
                EndBlendMode();
                if (filled > 0) sp.Draw(c, filled * sd, tint);
            }
            else if (filled > 0) DrawCircleV(c, step / 3 * filled / 0.6f, tint);
        }
    }

    string? BestGrade(DbBeatmap b)
    {
        if (!scores.TryGetValue(b.Md5, out var list)) return null;
        string me = Config.Get("Username", ""); // like stable: the local player's own scores only (as Personal Best)
        foreach (var r in list) if (!r.FromStable || r.Player.Equals(me, StringComparison.OrdinalIgnoreCase)) return GradeOf(r);
        return null;
    }

    static string GradeOf(ReplayInfo r)
    {
        int total = r.N300 + r.N100 + r.N50 + r.NMiss;
        if (total == 0) return "D";
        double r300 = r.N300 / (double)total, r50 = r.N50 / (double)total;
        bool silver = r.Mods.HasFlag(Mods.Hidden) || r.Mods.HasFlag(Mods.Flashlight);
        if (r.N300 == total) return silver ? "XH" : "X";
        if (r300 > 0.9 && r50 < 0.01 && r.NMiss == 0) return silver ? "SH" : "S";
        if (r300 > 0.8 && r.NMiss == 0 || r300 > 0.9) return "A";
        if (r300 > 0.7 && r.NMiss == 0 || r300 > 0.8) return "B";
        return r300 > 0.6 ? "C" : "D";
    }

    // stable's song select header (stable's sizes/positions, in 640x480 units; the right-hand group was
    // measured from a stable capture): map info top left, Group/Sort boxes and the group tabs top right, search at
    // the top of the song list.
    static Rectangle SortBox(int w) { float u = GetScreenHeight() / 480f; return new Rectangle(w - 129 * u, 18 * u, 119 * u, 14 * u); }
    static Rectangle GroupBox(int w) => SortBox(w) with { X = SortBox(w).X - 168 * GetScreenHeight() / 480f };
    // The group tabs: the selection-tab sprite at 0.6 units per texel (86 x 14), 73.9 apart so each overlaps the one
    // before, the first 392 units in from the right (measured).
    static Rectangle TabRect(int w, int t) { float u = GetScreenHeight() / 480f; return new Rectangle(w - (392 - t * 73.9f) * u, 34 * u, 143 * 0.6f * u, 24 * 0.6f * u); }

    void DrawTopBar(int w, float s)
    {
        float u = GetScreenHeight() / 480f;
        if (Game.Skin.Tex("songselect-top") is { } top) DrawTexturePro(top.Tex, new Rectangle(0, 0, top.Tex.Width, top.Tex.Height),
            new Rectangle(0, 0, Math.Max(w, top.Width * s), top.Height * s), Vector2.Zero, 0, Color.White);
        else DrawRectangle(0, 0, w, (int)TopH, new Color(0, 0, 0, 220));

        if (Selected is { } b)
        {
            // Ranked-status icon (osu!.db status: 4 ranked, 5 approved, 6 qualified, 7 loved).
            // Unknown / not submitted (an install that never went online): stable's question mark, at scale 1 (measured).
            string status = b.Status switch { 4 => "selection-ranked", 5 or 6 => "selection-approved", 7 => "selection-loved", 2 or 3 => "selection-notranked", _ => "selection-question" };
            if (status == "selection-question" && Game.Skin.Tex(status) is { } q) q.Draw(new Vector2(12 * u, 12 * u), s, Color.White);
            else if (Game.Skin.Tex(status) is { } icon) icon.Draw(new Vector2(11 * u, 11 * u), 15 * u / Math.Max(1, icon.Width), Color.White);
            if (headerFor != (b, mods)) // the header text only changes with the selection or mods
            {
                headerFor = (b, mods);
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
                    $"CS:{Math.Min(10, b.CS * kcs):0.#} AR:{Math.Min(10, b.AR * k):0.#} OD:{Math.Min(10, b.OD * k):0.#} HP:{Math.Min(10, b.HP * k):0.#} Star Rating: {(stars is > 0 and < 1000 ? (Math.Floor(stars * 100) / 100).ToString("0.##") : "?")}"]; // stable truncates (3.628 shows 3.62); the star glyph is drawn after
            }
            Game.Text(header[0], 23.4f * u, -3 * u, 18.4f * u, Color.White); // stable's sizes 18, 12, 12 bold, 12, 8 render this wide (measured)
            Game.Text(header[1], 24.6f * u, 12 * u, 12.45f * u, Color.White);
            Game.TextBold(header[2], 2.5f * u, 24 * u, 12.5f * u, lengthColour);
            Game.Text(header[3], 2.5f * u, 36.5f * u, 12.4f * u, Color.White);
            // the stats word by word: the arrow (3x2.2 units, top at the digits' top, 0.5 after the value) follows AR, OD, HP
            float statX = 2 * u, size = 8.2f * u, space = Game.TextWidth(" ", size);
            var parts = header[4].Split(' ', 5);
            for (int p = 0; p < parts.Length; p++)
            {
                if (p > 0) statX += space;
                Game.Text(parts[p], statX, 48 * u, size, statsColour);
                statX += Game.TextWidth(parts[p], size);
                if (statArrow == 0 || p is 0 or 4) continue;
                float ax = statX + 0.5f * u, at = 50.2f * u, ab = at + 2.2f * u;
                if (statArrow > 0) DrawTriangle(new(ax + 1.5f * u, at), new(ax, ab), new(ax + 3 * u, ab), statsColour);
                else DrawTriangle(new(ax, at), new(ax + 1.5f * u, ab), new(ax + 3 * u, at), statsColour);
                statX += 3.5f * u;
            }
            Options.DrawIcon(0xf005, statX + 4.5f * u, 52.5f * u, 8 * u, statsColour); // Aller has no star: stable's comes from a fallback font
        }

        // Group / Sort: label, then a boxed value. Only sorting is functional; grouping shows stable's default.
        void Box(Rectangle r, string label, Color labelColour, string value)
        {
            float size = 23 * u; // measured sizes throughout: stable's text renders ~3-7% wider than its nominal size here
            Game.TextLight(label, r.X - 3 * u - Game.LightWidth(label, size), r.Y + r.Height - size * 0.8f + 2 * u, size, labelColour); // Aller Light
            DrawRectangleRec(r, Color.Black);
            DropdownBorder(r, new Color((byte)(labelColour.R * 0.83f), (byte)(labelColour.G * 0.83f), (byte)(labelColour.B * 0.83f), (byte)255)); // label colour at 83% (measured)
            Game.Text(value, r.X + 0.7f * u, r.Y + r.Height / 2 - 7.3f * u, 13.4f * u, Color.White);
            Options.DrawIcon(0xf078, r.X + r.Width - 9.8f * u, r.Y + r.Height / 2 + 0.7f * u, 12.5f * u, Color.White); // stable's chevron (measured)
        }
        var sortBox = SortBox(w);
        string sortLabel = SortModes.FirstOrDefault(m => m.Key.Equals(SortMode, StringComparison.OrdinalIgnoreCase)).Label ?? "By Artist";
        Box(sortBox, "Sort", new Color(174, 210, 139, 255), sortLabel);
        Box(GroupBox(w), "Group", new Color(146, 195, 230, 255), GroupModes.FirstOrDefault(m => m.Key == GroupMode).Label ?? "No Grouping");

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
        if (Game.Skin.Tex("mode-osu-small") is { } modeIcon)
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
    ];

    // ---- stable's mode menu (the Mode button): osu!, osu!taiko, osu!catch, osu!mania upward from y 344 at x 140 (units),
    // each a 280x80 box of black at 160/255 (purple (165,90,234,230) while hovered), the osu!ui mode-*-med icon centred
    // 40 in and the mode's name (bold 24, shadowed) 90 in; rows fade in over 200ms, 20ms apart, and out over 100ms.
    // Hovering grows the icon to 1.1 and nudges the name 10 units right. Any click closes it; only osu! is playable here.
    static readonly (string Icon, string Name)[] ModeRows = [("mode-osu-med", "osu!"), ("mode-taiko-med", "osu!taiko"), ("mode-fruits-med", "osu!catch"), ("mode-mania-med", "osu!mania")];
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
        if (over > 0) Notification.Show($"{ModeRows[over].Name} isn't part of this osu! remake.", 1000);
        if (over >= 0) Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
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
        int w = GetScreenWidth(), h = GetScreenHeight();
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
            else if (name == "selection-random" && view.Count > 0) RandomKey(Input.IsKeyDown(KeyboardKey.LeftShift) || Input.IsKeyDown(KeyboardKey.RightShift));
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
    static float U => GetScreenHeight() / 480f;
    float ScoreTop => 96 * U;
    float ScoreRowH => 33.3f * U;
    int ScoreRowAt(float y) => (int)MathF.Floor((y - ScoreTop) / ScoreRowH);

    void DrawScoreRow(ReplayInfo r, float y, long? nextScore, bool hover)
    {
        float u = U;
        var row = new Rectangle(4.3f * u, y, 235.7f * u, ScoreRowH - 2 * u);
        DrawRectangleRec(row, hover ? new Color(100, 100, 100, 77) : new Color(0, 0, 0, 77)); // stable: black at 0.3, grey (100) on hover (measured 10 -> 7 over a dark background)
        // (stable numbers a row only with its online rank, so local scores show none)
        if (Game.Skin.Tex(SmallGrade(GradeOf(r))) is { } g) g.Draw(new Vector2(49 * u, y + 14 * u), 21 * u / Math.Max(1, g.Height), Color.White);
        string name = r.FromStable ? (r.Player.Length > 0 ? r.Player : "stable") : Config.Get("Username", "You");
        Game.Text(name, 63 * u, y + 7.7f * u - 8 * u, 16 * u, Color.White);
        var text = RowText(r);
        Game.Text(text.Score, 63 * u, y + 20.5f * u - 6 * u, 12 * u, Color.White);
        float right = 237.7f * u, size = 10 * u;
        string[] lines = rowLines;
        lines[0] = text.Mods; lines[1] = text.Acc; lines[2] = nextScore is long nx && r.Score > nx ? leadText.Of(r.Score - nx) : "-";
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Length > 0) Game.Text(lines[i], right - Game.TextWidth(lines[i], size), y + (5 + 10.3f * i) * u - size / 2, size, Color.White);
        // recent scores: an up-arrow circle and the age beside the row (measured at 1080p)
        if (AgoText(r) is { } ago)
        {
            Options.DrawIcon(0xf01b, 250 * u, y + 14.05f * u, 15.1f * u, Color.White);
            Game.Text(ago, 261.3f * u, y + 13.8f * u - 5 * u, 10.8f * u, Color.White);
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
        float sd = GetScreenHeight() / 768f, half = 12.5f * sd;
        bool hover = webShown && Math.Abs(Input.GetMousePosition().X - at.X) < half && Math.Abs(Input.GetMousePosition().Y - at.Y) < half;
        tex.Draw(at, sd * (hover ? 1.1f : 1), ColorAlpha(Color.White, a));
        if (hover) webTip = true;
    }
    bool webTip;

    // stable's dropdown outline (measured): about half a unit thick with ~1.3-unit rounded corners.
    void DropdownBorder(Rectangle r, Color c) =>
        DrawRectangleRoundedLinesEx(r, 2.6f * U / r.Height, 8, Math.Max(1, U / 2), c);

    string? noRecordsFor;
    double noRecordsAt;

    void DrawScores(int w, int h, float s)
    {
        float u = U;
        var box = new Rectangle(5 * u, 73 * u, 191 * u, 15 * u);
        DrawRectangleRec(box, Color.Black);
        DropdownBorder(box, new Color(22, 130, 179, 255));
        Game.Text("Local Ranking", box.X + 3 * u, box.Y + box.Height / 2 - 7 * u, 14 * u, Color.White);
        Options.DrawIcon(0xf078, box.X + box.Width - 9.8f * u, box.Y + box.Height / 2 + 0.7f * u, 12.5f * u, Color.White); // stable's chevron (measured)
        DrawWebButton(u);

        var list = SelectedScores;
        // stable: osu!ui's selection-norecords centred at (118, 170) units, fading in and sliding 30 down over 400ms (Out)
        if (Selected?.Md5 != noRecordsFor) { noRecordsFor = Selected?.Md5; noRecordsAt = Game.NowMs; }
        if (list.Count == 0 && Selected != null)
        {
            float k = (float)Math.Clamp((Game.NowMs - noRecordsAt) / 400, 0, 1);
            var at = new Vector2(118 * u, (170 + 30 * (float)Easing.Apply(1, k)) * u);
            if (Game.Skin.DefaultTex("selection-norecords") is { } nr) nr.Draw(at, s, ColorAlpha(Color.White, k));
            else Game.Text("No records set!", at.X - Game.TextWidth("No records set!", 14 * u) / 2, at.Y - 7 * u, 14 * u, ColorAlpha(Color.White, k));
        }
        var mouse = Input.GetMousePosition();
        for (int n = 0; n < Math.Min(8, list.Count); n++)
        {
            float y = ScoreTop + n * ScoreRowH;
            bool hover = mouse.X < 240 * u && ScoreRowAt(mouse.Y) == n && mouse.Y >= ScoreTop;
            DrawScoreRow(list[n], y, n + 1 < list.Count ? list[n + 1].Score : null, hover);
        }
        // (stable shows its "Personal Best" box only for the online ranking types, never for Local Ranking)
    }
    // F3 / Beatmap Options: stable's dialog for the selected set. Offline, the actions that would change stable's files
    // (collections, deleting, the unplayed flag, local scores) or need the editor only explain themselves.
    static readonly (string Label, Color Colour, string? Notice)[] BeatmapOptions =
    [
        ("Manage Collections", new Color(154, 205, 50, 255), "Collections are read from stable; manage them in osu!stable."),
        ("Delete...", new Color(255, 69, 0, 255), "This remake never deletes your beatmaps; delete them in osu!stable."),
        ("Remove from Unplayed", new Color(207, 124, 215, 255), "The remake doesn't write osu!stable's database."),
        ("Clear local scores", new Color(207, 124, 215, 255), "Your scores are kept; clear them in osu!stable."),
        ("Edit", new Color(255, 69, 0, 255), "The beatmap editor isn't part of this offline remake."),
        ("Cancel", Xna.Gray, null),
    ];
    bool beatmapOptions;
    double beatmapOptionsAt;
    float beatmapOptionsY;

    void OpenBeatmapOptions() { if (Selected != null) { beatmapOptions = true; beatmapOptionsAt = Game.NowMs; } }

    void UpdateBeatmapOptions()
    {
        var mouse = Input.GetMousePosition();
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        int chosen = Input.IsKeyPressed(KeyboardKey.Escape) || Input.IsKeyPressed(KeyboardKey.F3) ? BeatmapOptions.Length - 1 : -1;
        for (int i = 0; i < BeatmapOptions.Length && chosen < 0; i++)
            if (Input.IsKeyPressed(KeyboardKey.One + i) || click && CheckCollisionPointRec(mouse, Dialog.ButtonRect(beatmapOptionsY + i * 50))) chosen = i;
        if (chosen < 0) return;
        beatmapOptions = false;
        if (BeatmapOptions[chosen].Notice is { } notice) Notification.Show(notice, 3000);
    }

    void DrawBeatmapOptions()
    {
        if (Selected is not { } b) return;
        double age = Game.NowMs - beatmapOptionsAt;
        float fade = (float)Math.Clamp(age / 200, 0, 1);
        Dialog.Backdrop(fade);
        beatmapOptionsY = Dialog.Title($"{b.Artist} - {b.Title}\nWhat do you want to do with this beatmap?", fade);
        var mouse = Input.GetMousePosition();
        for (int i = 0; i < BeatmapOptions.Length; i++)
            Dialog.Button(i, BeatmapOptions[i].Label, BeatmapOptions[i].Colour, beatmapOptionsY + i * 50, age, mouse);
    }

    // stable's mod select: a dialog titled with its blurb, "Score Multiplier" centred 20 units above the content, three
    // labelled rows (size 24 at x 20; green, orange-red, white) of icons 66 units apart from x 240, rows 60 apart from 35
    // units under the content start, then Reset All Mods / Close. A selected icon springs to 1.2x and 0.1 rad (elastic,
    // 400ms) over a sky-blue glow. 1 / 2 press the buttons; stable's letter hotkeys toggle mods.
    static readonly (string Label, Color Colour, (KeyboardKey Key, Mods Flag, Mods Alt, string Icon, string AltIcon)[] Mods)[] ModRows =
    [
        ("Difficulty Reduction", new Color(50, 205, 50, 255), [(KeyboardKey.Q, Mods.Easy, Mods.None, "easy", ""), (KeyboardKey.W, Mods.NoFail, Mods.None, "nofail", ""), (KeyboardKey.E, Mods.HalfTime, Mods.None, "halftime", "")]),
        ("Difficulty Increase", new Color(255, 69, 0, 255), [(KeyboardKey.A, Mods.HardRock, Mods.None, "hardrock", ""), (KeyboardKey.S, Mods.SuddenDeath, Mods.Perfect, "suddendeath", "perfect"),
            (KeyboardKey.D, Mods.DoubleTime, Mods.Nightcore, "doubletime", "nightcore"), (KeyboardKey.F, Mods.Hidden, Mods.None, "hidden", ""), (KeyboardKey.G, Mods.Flashlight, Mods.None, "flashlight", "")]),
        ("Special", Color.White, [(KeyboardKey.Z, Mods.Relax, Mods.None, "relax", ""), (KeyboardKey.X, Mods.Autopilot, Mods.None, "relax2", ""), (KeyboardKey.C, Mods.SpunOut, Mods.None, "spunout", ""), (KeyboardKey.V, Mods.Autoplay, Mods.None, "autoplay", ""),
            (KeyboardKey.B, Mods.ScoreV2, Mods.None, "scorev2", "")]),
    ];
    const string ModBlurb = "Mods provide different ways to enjoy gameplay. Some have an effect on the score you can achieve during ranked play. Others are just for fun.";
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
            if (Input.IsKeyPressed(Config.Key(ModBindings[i], ModKeys[i]))) mods = ModInfo.Toggle(mods, ModKeys[i]);
        var mouse = Input.GetMousePosition();
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        if (click)
            for (int r = 0; r < ModRows.Length; r++)
                for (int c = 0; c < ModRows[r].Mods.Length; c++)
                    if (Vector2.Distance(mouse, ModIconPos(r, c)) < 21 * U) mods = ModInfo.Toggle(mods, ModRows[r].Mods[c].Key);
        float buttonsY = modContentY + 35 + 180;
        bool reset = Input.IsKeyPressed(KeyboardKey.One) || Input.IsKeyPressed(KeyboardKey.Backspace) || click && CheckCollisionPointRec(mouse, Dialog.ButtonRect(buttonsY));
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
        string mult = $"Score Multiplier: {mods.Multiplier():0.00}x";
        float ms = 31.1f * u; // stable's 30, measured
        // stable: the text fades over 400ms to green-yellow above 1x, orange-red below, white at 1x
        double mul = mods.Multiplier();
        var multTarget = mul > 1 ? new Color(173, 255, 47, 255) : mul < 1 ? new Color(255, 69, 0, 255) : Color.White;
        if (multTarget.R != multTo.R || multTarget.G != multTo.G || multTarget.B != multTo.B) { multFrom = multNow; multTo = multTarget; multAt = now; }
        multNow = ColorLerp(multFrom, multTo, (float)Math.Clamp((now - multAt) / 400, 0, 1));
        Game.Text(mult, w / 2f - Game.TextWidth(mult, ms) / 2, (modContentY - 20) * u - ms / 2, ms, ColorAlpha(multNow, fade));
        for (int r = 0; r < ModRows.Length; r++)
        {
            var (label, colour, list) = ModRows[r];
            float ls = 25.2f * u, cy = (modContentY + 35 + r * 60) * u;
            Game.Text(label, 22.7f * u, cy - ls / 2, ls, ColorAlpha(colour, fade)); // measured at 1080p
            for (int c = 0; c < list.Length; c++)
            {
                var (_, flag, alt, icon, altIcon) = list[c];
                bool on = mods.HasFlag(flag);
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
                string name = alt != Mods.None && mods.HasFlag(alt) ? altIcon : icon;
                Game.Skin.Tex("selection-mod-" + name)?.Draw(pos, scale * s, ColorAlpha(Color.White, fade), rot * 180 / MathF.PI);
            }
        }
        var mouse = Input.GetMousePosition();
        float buttonsY = modContentY + 35 + 180;
        Dialog.Button(0, "Reset All Mods", new Color(255, 69, 0, 255), buttonsY, age, mouse, modButtonFlashed == 0 ? now - modButtonFlashAt : double.MaxValue);
        Dialog.Button(1, "Close", Xna.Gray, buttonsY + 50, age, mouse);
    }

    // ---------------------------------------------------------------- background (loaded off the main thread)

    Texture2D bgTex;
    string bgKey = "";
    Task<(string Key, Image Img)>? bgLoading;
    double bgShownAt;

    void UpdateBackground()
    {
        if (bgLoading is { IsCompleted: true })
        {
            var (key, img) = bgLoading.Result;
            bgLoading = null;
            if (key == bgKey && img.Width > 0)
            {
                if (bgTex.Id != 0) UnloadTexture(bgTex);
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
        catch (IOException) { return (file, default(Image)); }
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

    // stable's cursor ripples (the Cursor ripples option): each click leaves the skin's cursor-ripple (else a ring) at
    // the cursor, added on, growing 0.05 -> 0.5 (Out) and fading out from 20% over 700ms.
    static readonly List<(Vector2 Pos, double At)> ripples = [];
    internal static void Ripple(Vector2 at) { if (Config.GetBool("CursorRipple", false)) ripples.Add((at, Game.NowMs)); }

    static void DrawRipples()
    {
        ripples.RemoveAll(r => Game.NowMs - r.At > 700);
        if (ripples.Count == 0 || (Game.Skin.Own("cursor-ripple") ? Game.Skin.Tex("cursor-ripple") : Game.Skin.DefaultTex("approachcircle")) is not { } tex) return;
        float sd = GetScreenHeight() / 768f;
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
        float s = GetScreenHeight() / 768f * (float)Config.GetDouble("CursorSize", 1) * CursorPlayScale;
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
            if (smoothFrom is not { } from || Vector2.Distance(from, at) > GetScreenWidth()) smoothFrom = at;
            else
            {
                var d = at - from;
                float len = d.Length();
                var dir = len > 0 ? d / len : Vector2.Zero; // one unit step direction for the whole walk
                while (len >= step)
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
