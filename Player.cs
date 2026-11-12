using System.Diagnostics;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

sealed class ObjState
{
    public bool Done;
    public int Result = -1;
    public double JudgeTime;
    public bool HeadDone, HeadHit, HeadMissPending;
    public double HeadTime;
    public int HeadResult; // the head's timing judgement (Score V2 caps the slider with it)
    public int NextCheckpoint, CheckpointsHit;
    public bool Tracking;
    public double TrackingSince, FollowLostAt = double.NaN;
    public bool TrackedAtEnd;
    public int TrackButton; // stable: the button (1 left, 2 right) this slider is tracked with, 0 none
    // spinner (stable's momentum model; Rotation counts half-turns)
    public double Rotation, LastAngle, Angle, Velocity, Target, SmoothElapsed;
    public double SimTime = double.NaN, NextTick, LastInputTime = double.NaN;
    public bool HasAngle;
    public int SpinsCounted, ZeroCount;
    public double Rpm, ClearAt = double.NaN, BonusAt = double.NaN, SpinTextFadeAt = double.NaN;
    public int Bonus;
}

sealed partial class Player : Screen
{
    readonly Beatmap map;
    readonly SongSelect back;
    readonly List<HitObject> objs;
    readonly ObjState[] st;
    readonly Vector2[][] bodies;
    readonly (Vector2 Min, Vector2 Max)[] bodyBounds; // each body's path bounds (osu! pixels)
    readonly bool snaking = Config.GetBool("SnakingSliders", true);
    readonly (Vector2 From, Vector2 Pos, float Rotation, double Time)[] followPoints;
    readonly int fpFrames;
    readonly double fpFrameMs;
    int fpFirst;
    readonly BeatmapSamples samples;
    readonly Skin skin;
    string? missingSkin; // the map's preferred skin, not in Skins
    public override void Entered()
    {
        if (missingSkin != null) Toast.Show($"This beatmap is requesting a skin preference of '{missingSkin}'.  Please download this skin for the intended experience", new Color(255, 69, 0, 255), 6000);
        missingSkin = null;
    }
    Color[] colours; // per-map Skin toggle can swap these (VisualSettings.cs)

    // Clock: a Stopwatch that is continuously (and gently) pulled towards the audio position.
    readonly Stopwatch sw = new();
    double clockOrigin, time, clockFloor = double.NegativeInfinity;
    internal double SongTime => time; // test hook: --song-shots
    readonly Track? music;
    double driftSum, driftMax;
    long driftCount, snaps;
    bool musicStarted, musicEnded, paused, finished;
    readonly Storyboard? storyboard;
    bool wasInBreak;
    Texture2D bg;
    RenderTexture2D sliderRt;

    readonly KeyboardKey key1, key2;
    bool mouseButtons;
    Vector2 cursor;
    float scale;
    Vector2 origin;
    int firstActive;

    long score;
    internal long Score => score;
    internal int Geki => nGeki;
    internal int Katu => nKatu;
    internal double Time => time;
    int combo, maxCombo, n300, n100, n50, nMiss, nGeki, nKatu;
    internal readonly List<Vector2> hpGraph = []; // (time, hp 0..1) for the results screen
    internal readonly List<Vector2> perfGraph = []; // stable's ranking graph (see Final)
    readonly double diffMul;
    readonly int maniaKeyChange;
    // stable's Easy: two lives (not with Sudden Death). Running out of health uses one: the song stops, health gets 160
    // (when under it), the bar refills at 0.08 a ms, and once it's shown within 5 of that the play goes on
    int easyLives;
    bool recovering;
    double hpRecoverAt = double.MinValue; // (stable flashes the bar red, fading to white over 400ms)
    readonly List<double> hitErrors = [];
    readonly List<int> spinRpms = []; // the RPM readout while spinners turn (the ranking screen's "Spin" tooltip)
    readonly List<(Vector2 Pos, double Time, int Result, string Sprite, bool Spinner)> popups = [];
    readonly List<int> visible = [];

    // stable's audio-to-game shift: this map's local offset (stable's from osu!.db, or one set here with +/-), its
    // online offset and 24ms more on maps before file format v5, less the universal offset (options), which runs at the
    // play's speed (x1.5 with DT).
    double Offset => mapOffset + localOffset - Config.GetDouble("Offset", 0) * rate;
    int localOffset, mapOffset;
    double retryHold;
    bool retryArmed;
    double localOffsetChangedAt = double.MinValue;

    readonly Mods mods;
    readonly bool auto, relax, autopilot;
    // stable: running out of health fails a play unless NoFail, Relax or Autopilot (older own replays: Relax and Autopilot too)
    bool HealthFails => !mods.HasFlag(Mods.NoFail) && (OldModeRules || !relax && !autopilot);
    // stable's Cinema (Auto's second state): an Auto play showing only the background, video and storyboard - no
    // objects, interface, cursor or bursts (the hitsounds still play) - that goes back to song select at the end
    readonly bool cinema;
    internal bool Cinema => cinema;
    readonly double rate, modMul;
    readonly Health health;
    bool failed;
    int comboWorst = 300;
    double lastTime = double.NaN;
    int autoNext;
    double autoReleaseAt = double.NegativeInfinity; // map time starts negative: nothing held through the lead-in
    int autoAlt, autoButton = 4; byte autoKeys; byte autoPressed; // the key overlay's view of Auto: stable's M1/M2 choice (judging uses held)

    // Replays: recording during live play, or playback when watching.
    readonly ReplayFrame[]? playback;
    readonly ReplayInfo? watching;
    readonly int mode; // 0 osu!, 1 taiko, 2 catch, 3 mania (map.PlayMode)
    int seed = Environment.TickCount; // (a replay: its own, set in the constructor)
    bool ModeDone => mode switch { 1 => TaikoDone, 2 => CatchDone, 3 => ManiaDone, _ => firstActive >= objs.Count };
    readonly List<ReplayFrame> recording = [];
    internal IReadOnlyList<ReplayFrame> Recording => recording;
    ReplayInfo? savedReplay;
    int pbIndex;
    byte inputKeys;
    // stable's left/right button states (K1/M1 = left 1, K2/M2 = right 2) at the last three changes, and the
    // buttons newly down this update; slider tracking depends on them (ButtonHolds).
    int btnCur, btnPrev, btnPrev2, btnNew;
    int pressButton = 1; // the button (1 left, 2 right) of the press being handled; a slider head takes it (stable)
    Vector2 pbCursor = new(256, 192);
    Vector2 lastRecorded = new(float.NaN), liveCursor;

    // stable: a play started from song select waits for song select's music to fade out (0.02 of full volume a 60Hz
    // tick) before its clock starts, showing the Visual Settings strip meanwhile; an opened panel holds the start.
    Track? fadeOut;
    float fadeVolume;
    internal double LeftSongSelectAt = double.NaN; // song select lingers for a second over a play started from it
    internal Player FadingOut(Track music)
    {
        fadeOut = music;
        fadeVolume = 1;
        sw.Reset();
        return this;
    }

    // true while the start is held
    bool UpdateStartHold(double dtMs)
    {
        if (fadeOut == null) return false;
        // stable: Esc (or the pause key) before the start goes back to song select
        if (Input.IsKeyPressed(KeyboardKey.Escape) || Input.IsKeyPressed(Config.Key("keyPause", KeyboardKey.Escape))) { Leave(back); return true; }
        UpdateVisualSettings(dtMs);
        fadeVolume -= 0.02f * (float)(Math.Min(dtMs, 33) / (1000 / 60.0)); // (our first frame carries the load time)
        fadeOut.Volume = Math.Max(0, fadeVolume) * Config.MusicVolume;
        if (fadeVolume > 0 || VisualOpen) { UpdateDim(dtMs); return true; }
        fadeOut.Dispose();
        fadeOut = null;
        sw.Start();
        return false;
    }

    public Player(Beatmap map, SongSelect back, Mods mods, ReplayFrame[]? playback = null, ReplayInfo? watching = null)
    {
        if (!VirtualClock) Beatmap.PlayfieldHeight = Game.H; // headless runs keep stable's configured height
        this.playback = playback;
        if (playback is { Length: > 0 }) pbCursor = new(playback[0].X, playback[0].Y); // stable: the first frame's spot until frames start (its lead frames sit off-screen at 256,-500)
        this.watching = watching;
        this.map = map;
        this.back = back;
        this.mods = mods;
        auto = mods.HasFlag(Mods.Autoplay);
        cinema = auto && mods.HasFlag(Mods.Cinema);
        relax = mods.HasFlag(Mods.Relax);
        autopilot = mods.HasFlag(Mods.Autopilot);
        rate = mods.Rate();
        health = new Health(map, mods.HasFlag(Mods.HalfTime));
        objs = map.Objects;
        mode = watching?.Mode ?? map.PlayMode; // (a replay: the mode it was played in)
        if (watching != null) seed = watching.Seed;
        // stable: a taiko play takes the skin named "taiko" with UseTaikoSkin, any other the map's SkinPreference (with
        // its beatmap skin on), when Skins has it - else the configured one
        bool editing = Game.Screen is Editor || Game.Screen is Player { ReturnTo: Editor };
        string? own = mode == 1 ? (Config.GetBool("UseTaikoSkin", false) ? "taiko" : null)
            : map.SkinPreference.Length > 0 && Toggles.Skin && !Config.GetBool("IgnoreBeatmapSkins", false) ? map.SkinPreference : null;
        if (!Game.UseSkin(own, editing) && mode != 1 && !VirtualClock) // (the taiko one goes unmentioned; said once the play shows, not for a
            missingSkin = own;                                          // replay's ranking screen)
        skin = Game.Skin;
        // (a mania convert: the keys played - the key mod's or its own, doubled by Key Coop - against its own count, like
        // stable's; a mania map's own keys never change)
        int ownKeys = mode == 3 && map.Mode != 3 ? ManiaConvertKeys(map) : 0;
        maniaKeyChange = ownKeys > 0 ? (mods.Keys() > 0 ? mods.Keys() : ownKeys) * (mods.HasFlag(Mods.KeyCoop) ? 2 : 1) - ownKeys : 0;
        modMul = mods.ScoreMultiplier(mode, maniaKeyChange);
        easyLives = mods.HasFlag(Mods.Easy) && !mods.HasFlag(Mods.SuddenDeath) ? 2 : 0;
        st = objs.Select(_ => new ObjState()).ToArray();
        samples = new BeatmapSamples(map.Folder);
        skin.PreloadGameplay();
        skin.Prefetch(SongSelect.ModIcons.Where(x => mods.HasFlag(x.Flag)).Select(x => "selection-mod-" + x.Icon)); // ranking screen
        colours = !Config.GetBool("IgnoreBeatmapSkins", false) && map.ComboColours.Count > 0
            ? map.ComboColours.Select(c => new Color(c.R, c.G, c.B, (byte)255)).ToArray()
            : skin.ComboColours;

        float step = Math.Max(1, map.Radius / 8);
        bodies = objs.Select(o =>
        {
            if (o.Type != ObjType.Slider) return [];
            var pts = new List<Vector2>();
            for (double d = 0; d < o.Length; d += step) pts.Add(o.PointAt(d) + map.StackOffset(o));
            pts.Add(o.PointAt(o.Length) + map.StackOffset(o));
            return pts.ToArray();
        }).ToArray();
        bodyBounds = bodies.Select(b => b.Length == 0 ? default
            : (b.Aggregate(b[0], Vector2.Min), b.Aggregate(b[0], Vector2.Max))).ToArray();

        // stable's follow points: every 32 units from 48 in, short of the last 32, over the whole-unit distance; each slides
        // in from 10% further back.
        var fps = new List<(Vector2, Vector2, float, double)>();
        for (int i = 1; i < objs.Count; i++)
        {
            HitObject a = objs[i - 1], b = objs[i];
            if (b.ComboNumber == 1 || a.Type == ObjType.Spinner || b.Type == ObjType.Spinner) continue;
            Vector2 from = a.EndPos + map.StackOffset(a), to = PosOf(b);
            int dist = (int)Vector2.Distance(from, to);
            float rot = MathF.Atan2(to.Y - from.Y, to.X - from.X) * 180 / MathF.PI;
            for (int d = 48; d < dist - 32; d += 32)
            {
                float f = (float)d / dist;
                fps.Add((from + (to - from) * (f - 0.1f), from + (to - from) * f, rot, a.EndTime + (b.Time - a.EndTime) * f));
            }
        }
        followPoints = fps.ToArray();
        fpFrames = skin.FrameCount("followpoint");
        fpFrameMs = skin.AnimationFramerate > 0 ? 1000 / skin.AnimationFramerate : 1000.0 / Math.Max(1, fpFrames);

        key1 =Config.Key("keyOsuLeft", KeyboardKey.Z);
        key2 = Config.Key("keyOsuRight", KeyboardKey.X);
        keySmoke = Config.Key("keyOsuSmoke", KeyboardKey.C);
        mouseButtons = !Config.GetBool("MouseDisableButtons", false);
        // stable warns once a session, in a beige corner notice, when a live play starts with the mouse buttons off
        if (!mouseButtons && playback == null && !auto && !mouseWarned)
        {
            mouseWarned = true;
            Toast.Show($"Mouse buttons are currently disabled. Hit {Config.Get("keyDisableMouseButtons", "F10")} to enable them again.", new Color(245, 245, 220, 255), 10000);
        }

        diffMul = mode == 1 ? map.TaikoDifficultyMultiplier : map.DifficultyMultiplier;

        // stable counts retries in a row of a live play (any other way in starts over)
        if (playback == null && !auto) RetryStreak = retrying ? RetryStreak + 1 : 0;
        retrying = false;
        Bloom.On = false; // (a play left mid-effect doesn't carry it into this one)
        music = Track.Load(Path.Combine(map.Folder, map.AudioFilename), rate, mods.HasFlag(Mods.Nightcore), tempo: playback != null);
        if (music != null)
            music.Volume = Config.MusicVolume;
        // stable: a play starting with the master or music volume at 0 says so; a click puts it back to the default
        if (Config.GetInt("VolumeUniversal", 100) == 0 || Config.GetInt("VolumeMusic", 80) == 0)
            Toast.Show("Your music volume is set to 0%! Click here to restore it.", Xna.Red, 5000, () =>
            {
                if (Config.GetInt("VolumeUniversal", 100) == 0) Config.SetOwn("VolumeUniversal", "100");
                if (Config.GetInt("VolumeMusic", 80) == 0) Config.SetOwn("VolumeMusic", "80");
                ApplyMusicVolume();
            });

        float dimAlpha = 1 - LightestDim / 100f; // the storyboard can show through at the lightest dim reached
        if (!VirtualClock)
            try { storyboard = Storyboard.Load(map, textures: dimAlpha > 0); }
            catch (Exception e) when (e is not OutOfMemoryException) { Console.Error.WriteLine($"storyboard not loaded: {e.Message}"); } // (the play goes on without it)
        if (dimAlpha > 0) LoadBackground();
        sliderRt = LoadRenderTexture(Game.W, Game.H);
        if (!VirtualClock && objs.Any(o => o.Type == ObjType.Slider))
            for (int k = 0; k < 6; k++) bodyTexturePool.Add(LoadRenderTexture((Game.W / 2 + 127) / 128 * 128, (Game.H / 2 + 127) / 128 * 128));
        if (!VirtualClock) ApplyToggles();
        if (!VirtualClock)
        {
            mapMd5 ??= Replays.Md5Of(map.File);
            localOffset = Config.GetInt($"LocalOffset_{mapMd5}", back.LocalOffsetOf(mapMd5));
            mapOffset = (map.FormatVersion < 5 ? 24 : 0) + back.OnlineOffsetOf(mapMd5);
            if (localOffset != 0) Notification.Show($"Using local beatmap offset ({localOffset}ms)", 1000);
        }
        SetupCountdown();
        if (mode == 1) { TaikoSetup(); samples.Prefix = "taiko-"; }
        else if (mode == 2) CatchSetup();
        else if (mode == 3) ManiaSetup();
        InitScoreV2();

        // stable's lead-in: AudioLeadIn, the earliest storyboard event, or enough for max(1800ms, preempt) before the first object.
        clockOrigin = Math.Min(Math.Min(-map.AudioLeadIn, storyboard?.EarliestEvent ?? 0), objs[0].Time - Math.Max(1800, map.Preempt));
        if (map.TestFrom) clockOrigin = objs[0].Time - Math.Max(1800, map.Preempt); // (the editor's test: from its position)
        playStart = clockOrigin;
        SetupSkip();
        sw.Start();
    }

    // Background dim in percent, eased like stable (1/15 of the way per 60Hz tick) from the previous screen's
    // (none) to DimLevel, and 30 points lighter in the intro, breaks and outro unless IHateHavingFun is set.
    float bgDim;
    static float LightestDim => Math.Max(0, Config.GetInt("DimLevel", 80) - (Config.GetBool("IHateHavingFun", false) ? 0 : 30));

    void UpdateDim(double dtMs)
    {
        float target = Resting(time) ? LightestDim : Config.GetInt("DimLevel", 80);
        bgDim += (target - bgDim) * (1 - (float)Math.Pow(14 / 15.0, dtMs / (1000 / 60.0)));
        UpdateBloom(dtMs);
    }

    // stable's play effects, drawn by the Softening filter's pass (with stable's Shaders setting on, which softening turns on): below 20% health the frame is
    // redrawn with (40 - shown HP) / 160 added to red; a spinner reaching its last spin before the bonus glows
    // (spread 0.008, additive, up to 50% at 60% of the required spins past that), but only on updates where none of
    // these was on (so every other one); a combo burst flashes (additive, 20% fading out over 20 60Hz ticks).
    internal static (bool On, float Mag, float Alpha, float Red, bool Additive) Bloom;
    float bloomFlash;

    void UpdateBloom(double dtMs)
    {
        if (!Config.GetBool("Shaders", false) || failed) { Bloom.On = false; return; }
        var spinner = paused ? null : ActiveSpinner();
        if (health.Value < 40 && !paused && !Resting(time) && !finished)
            Bloom = (true, 0.0001f, 1, (float)Math.Max(0, (40 - hpShown) / 160), false);
        else if (spinner is { } sp && !Bloom.On)
        {
            int req = SpinsRequired(sp.O);
            Bloom = (true, 0.008f, req > 0 ? Math.Clamp((sp.S.SpinsCounted - req + 1) / (req * 0.6f), 0, 0.5f) : 0, 0, true);
        }
        else if (bloomFlash > 0)
        {
            Bloom = (true, 0.0001f, bloomFlash * 0.2f, 0, true);
            bloomFlash -= (float)(0.05 * dtMs / (1000 / 60.0));
        }
        else Bloom.On = false;
    }

    (HitObject O, ObjState S)? ActiveSpinner()
    {
        if (mode != 0) return null;
        for (int i = firstActive; i < objs.Count && objs[i].Time <= time; i++)
            if (objs[i].Type == ObjType.Spinner && time <= objs[i].EndTime && !st[i].Done) return (objs[i], st[i]);
        return null;
    }

    // Map time: real time scaled by the mod rate (DT 1.5x, HT 0.75x), like stable, and the replay speed.
    double Raw => clockOrigin + (VirtualClock ? virtualElapsed : sw.Elapsed.TotalMilliseconds) * rate * speed;

    // Headless verification (--verify-osr): time advances only by the dt passed to Update, no audio.
    public static bool VirtualClock;
    public static bool FrameStepped; // --fuzz-osr: always step through replay frames like stable's replay viewer
    public static bool LiveJudging; // --verify-osr: judge stable replays every ms, as they were played live
    bool Stepped => FrameStepped || StableReplay && !LiveJudging; // watching a stable replay steps like stable's viewer
    double lastStep;
    static readonly bool VerifyLog = Environment.GetEnvironmentVariable("VERIFY_LOG") == "1"; // per-judgement log for --verify-osr
    double virtualElapsed;
    public (int N300, int N100, int N50, int NMiss, int MaxCombo, bool Done, double Mean) Result =>
        (n300, n100, n50, nMiss, maxCombo, finished || failed, HitStats().Mean);
    Vector2 ToScreen(Vector2 p) => origin + p * scale;
    Vector2 PosOf(HitObject o) => o.Pos + map.StackOffset(o);
    Vector2 BallOf(HitObject o, double t) => o.PositionAtTime(t) + map.StackOffset(o);
    // stable's skip: the target is the first object less 8 beats (4 when a beat is 500ms or longer; 2000ms without a
    // timing point), offered when it lies more than 1200ms + preempt past the start; usable once, until the target.
    double skipTarget = double.NaN;
    bool skipped;
    bool CanSkip => !skipped && time < skipTarget || outroSkip;

    // stable's play end: complete 200ms after the last object (or when the music stops); from then on Esc / the pause
    // key go straight to the ranking screen, which otherwise comes at max(last object + 3s, storyboard end - 500ms).
    // A storyboard running over 3s past completion brings the Skip button back to end it early.
    bool Completed => !VirtualClock && (musicEnded || time > objs[^1].EndTime + 200);
    double SbEnd => storyboard is { IsEmpty: false } sb ? sb.LatestEvent : double.NegativeInfinity;
    bool outroSkip, endNow;

    void SetupSkip()
    {
        double beat = map.TimingPoints.Count > 0 ? map.TimingPoints[0].BeatLength : 0;
        double target = Math.Round(beat > 0 ? objs[0].Time - beat * (beat < 500 ? 8 : 4) : objs[0].Time - 2000);
        if (target - Math.Min(0, clockOrigin) > 1200 + map.Preempt) skipTarget = target;
    }
    // the most combo a play can reach: osu! sliders' every checkpoint; taiko's notes (Score V2: its drumrolls and swells
    // too); catch's fruits and droplets; mania's notes and holds with a hold's 100ms ticks (V2: its head and tail instead)
    internal int MapMaxCombo => mode switch
    {
        1 => tNotes.Count(n => n.Kind is TKind.Don or TKind.Kat || ModeV2),
        2 => cFruits.Count(f => f.Kind is CKind.Fruit or CKind.Droplet),
        3 => mNotes.Sum(n => !n.Hold ? 1 : ModeV2 ? 2 : OldModeRules ? 1 : 1 + (int)((n.End - n.Time) / 100)),
        _ => objs.Sum(o => o.Type == ObjType.Slider ? 1 + o.Checkpoints.Count : 1),
    };
    // full combo: osu!'s the map's whole combo; taiko's, catch's and mania's never broken (their combos don't count
    // the osu! objects: rolls, bananas, converted notes)
    internal bool FullCombo => mode == 0 ? maxCombo == MapMaxCombo : !comboBroke && maxCombo > 0;
    bool comboBroke;

    public void Dispose()
    {
        fadeOut?.Dispose();
        fadeOut = null;
        music?.Dispose();
        SoundPool.Stop(pauseLoop);
        SoundPool.Stop(spinLoop);
        if (bg.Id != 0) UnloadTexture(bg);
        video?.Dispose();
        skin.SetMapSkin(null);
        storyboard?.Unload();
        UnloadRenderTexture(sliderRt);
        foreach (var (mesh, _) in bodyMeshes.Values) UnloadMesh(mesh);
        bodyMeshes.Clear();
        foreach (var (rt, _, _) in bodyCache.Values) UnloadRenderTexture(rt);
        bodyCache.Clear();
        foreach (var rt in bodyTexturePool) UnloadRenderTexture(rt);
        bodyTexturePool.Clear();
        if (sliderGradient.Id != 0) UnloadTexture(sliderGradient);
        samples.Unload();
    }

    public override void CloseRequested() => Leave(back); // stable: back to song select

    // the editor's test play: back to the editor instead of song select (and no ranking screen, no score or replay kept)
    internal Screen? ReturnTo;

    void Leave(Screen next)
    {
        if (next == back && ReturnTo != null) next = ReturnTo;
        if ((next == back || next == ReturnTo) && !VirtualClock) { Game.FadeTo(next, Dispose); return; } // stable goes back through black
        Dispose();
        Game.Screen = next;
    }

    internal bool AutoPlay => mods.HasFlag(Mods.Autoplay) || mods.HasFlag(Mods.Cinema);

    // a skin reload during Auto: the play starts over, the new skin loaded while the screen is black
    internal void Restart(Action whileBlack) => Game.FadeTo(() => new Player(map, back, mods, playback, watching), () => { Dispose(); whileBlack(); });

    // stable restarts through black too; a quick retry (held key) is black already
    static bool retrying; // set by Retry for the play it starts

    void Retry(bool quick = false)
    {
        retrying = true;
        if (VirtualClock) { Leave(new Player(map, back, mods, playback, watching) { Retried = true, ReturnTo = ReturnTo }); return; }
        Game.FadeTo(() => new Player(map, back, mods, playback, watching) { Retried = true, ReturnTo = ReturnTo }, Dispose, quick);
    }

    void Fail()
    {
        if (failed || auto || finished || recovering) return;
        if (easyLives > 0 && !OldModeRules) // (older own replays: no lives)
        {
            easyLives--;
            recovering = true;
            if (health.Value < 160) { health.Add(160); hpFilling = true; hpRecoverAt = Game.NowMs; }
            sw.Stop();
            if (musicStarted && !musicEnded) music?.Pause();
            skin.Sound("readys")?.Play(BeatmapSamples.EffectVolume);
            Console.Error.WriteLine($"a life used at {time:0}ms ({easyLives} left); hp {health.Value:0.0}");
            return;
        }
        if (mods.HasFlag(Mods.Perfect) && playback == null)
        {
            // stable: Perfect starts the map again at once (the retry click, no fail screen)
            failed = true;
            sw.Stop();
            if (musicStarted && !musicEnded) music?.Pause();
            skin.Sound("pause-retry-click")?.Play(BeatmapSamples.EffectVolume);
            Console.Error.WriteLine($"failed at {time:0}ms (Perfect: starting again); score {score} acc {Accuracy:0.00}% 300/100/50/miss {n300}/{n100}/{n50}/{nMiss}");
            Retry(quick: true);
            return;
        }
        failed = true;
        if (storyboard != null) storyboard.Passing = false;
        menuShownAt = double.PositiveInfinity; // shown once the song has wound down
        sw.Stop(); // time follows the song from here (UpdateFailAnim)
        failFreq = music?.Frequency is > 0 and var f ? f : 44100;
        fallRot = new float[objs.Count];
        skin.Sound("failsound")?.Play(BeatmapSamples.EffectVolume);
        Console.Error.WriteLine($"failed at {time:0}ms; score {score} acc {Accuracy:0.00}% 300/100/50/miss {n300}/{n100}/{n50}/{nMiss}");
    }

    // ---- stable's fail animation (per 60Hz tick): the song's frequency drops 300 a tick, the playfield
    // fades 0.007 a tick, and every object sprite falls (y *= 1.01, above the field y *= 0.6) and spins (a random
    // +-0.02 rad, then 0.01 more each tick). The clock keeps following the slowing song; the menu shows once the
    // frequency is down to 101. stable spins each sprite separately; we turn each object around its head.
    float failFreq, failAlpha = 1, fallDown = 1, fallUp = 1;
    // stable fades the HUD and the playfield in at the start and out once the map is complete (200ms after the last
    // object ends, or when the music stops), 0.07 a 60Hz frame; with the first object over 6s away it waits until
    // 1s before the music starts. Cursor stars, the key overlay and the replay controls stay.
    float hudFade;
    float playfieldAlpha => failAlpha * hudFade;
    double lastEnd = double.NaN;
    bool Complete => time > (double.IsNaN(lastEnd) ? lastEnd = objs.Max(o => o.EndTime) : lastEnd) + 200 || musicEnded && time > 0;

    void UpdateHudFade(double dtMs)
    {
        float target = Complete || time < -1000 && objs[0].Time > 6000 ? 0 : 1, step = 0.07f * (float)(dtMs / (1000 / 60.0));
        hudFade = hudFade < target ? Math.Min(target, hudFade + step) : Math.Max(target, hudFade - step);
    }
    float[] fallRot = [];
    double failTickMs;

    void UpdateFailAnim(double dtMs)
    {
        // At most one tick per frame, the rest carried over (stable's 60Hz flag), so a stall slows it rather than skipping.
        failTickMs += dtMs;
        if (failTickMs >= 1000 / 60.0)
        {
            failTickMs -= 1000 / 60.0;
            if (failFreq <= 101) { menuShownAt = Game.NowMs; music?.Pause(); return; }
            if (music != null) music.Frequency = failFreq;
            failAlpha = Math.Max(0, failAlpha - 0.007f);
            fallDown *= 1.01f; fallUp *= 0.6f;
            for (int i = 0; i < fallRot.Length; i++)
                fallRot[i] = fallRot[i] == 0 ? Random.Shared.NextSingle() * 0.04f - 0.02f : fallRot[i] + 0.01f * MathF.Sign(fallRot[i]);
            failFreq -= 300;
        }
        if (music != null && musicStarted) time = Math.Max(time, music.PositionMs - Offset);
    }

    bool Falling => failed && fallRot.Length > 0;

    // Multiplies the current rlgl matrix so object i draws dropped and turned around its anchor.
    void PushFall(int i)
    {
        Rlgl.PushMatrix();
        var o = objs[i];
        var anchor = o.Type == ObjType.Spinner ? new Vector2(256, 192) : PosOf(o);
        var p = ToScreen(anchor);
        float y = anchor.Y >= 0 ? anchor.Y * fallDown : anchor.Y * fallUp;
        Rlgl.Translatef(p.X, origin.Y + y * scale, 0);
        Rlgl.Rotatef(fallRot[i] * 180 / MathF.PI, 0, 0, 1);
        Rlgl.Translatef(-p.X, -p.Y, 0);
    }

    // ---- stable's break overlays. Entering a break longer than 2880ms after the first object decides
    // pass/fail (health at least half, as for the storyboard); the section image shows at the break's middle when
    // half the break is over 2880ms, else 2880ms before it ends: pass flashes in at +20 (off 100-160, 230-280),
    // fail at +130 (off 230-280), both fade out over 1280-1480, each with its sound at its first flash.
    int breakSeen = -1;
    double sectionAt = double.NaN;
    bool sectionPass, sectionSounded;
    // pass/fail at each section break and at the end (HP at least half): stable's ranking applauds a live play that
    // passed more often than it failed
    readonly List<bool> passStates = [];
    internal bool Applause => playback == null && !auto && passStates.Count(x => x) > passStates.Count(x => !x);

    void UpdateBreakSection()
    {
        for (int b = breakSeen + 1; b < map.Breaks.Count; b++)
        {
            var (start, end) = map.Breaks[b];
            if (time < start || time <= objs[0].Time) break;
            breakSeen = b;
            if (time > end || end - start <= 2880 || failed) continue;
            double len = end - start;
            sectionPass = health.Value >= Health.Max / 2;
            passStates.Add(sectionPass);
            sectionAt = len / 2 > 2880 ? start + len / 2 : end - 2880;
            sectionSounded = false;
        }
        if (!sectionSounded && !double.IsNaN(sectionAt) && time >= sectionAt + (sectionPass ? 20 : 130))
        {
            sectionSounded = true;
            skin.Sound(sectionPass ? "sectionpass" : "sectionfail")?.Play(BeatmapSamples.EffectVolume);
        }
    }

    // ---- stable's countdown: beats of the timing point at the first object (doubled if <= 333ms,
    // halved/doubled for Countdown 3/2) are laid on its grid so "go" lands CountdownOffset beats before the first
    // object; there must be room for four. ready fades in 6 beats before go, 3/2/1 on the three beats before it
    // (v2+ skins: centred, popping 1.4 -> 1; older: 3 right and 2 left of centre), go on the beat.
    double cdGo = double.NaN, cdBeat;
    (double At, string Sound)[] cdSounds = [];
    int cdNext;

    void SetupCountdown()
    {
        double first = objs[0].Time;
        var tp = map.UninheritedAt(first);
        double origin = tp.Time, beat = tp.BeatLength;
        if (beat <= 0 || map.Countdown == 0) return;
        double t = origin - 5, step = beat <= 333 ? beat * 2 : beat;
        if (map.Countdown == 3) step /= 2;
        else if (map.Countdown == 2) step *= 2;
        first -= step * map.CountdownOffset;
        if (t >= first) while (t > first - step) t -= step;
        int div = 1;
        while (t < first - step / div)
        {
            t += step;
            double phase = (float)(t - origin) / beat % 4.0;
            div = phase > 1.5 && phase < 3.5 ? 2 : 1;
        }
        t -= step;
        if (t - 4 * step <= 0) return;
        cdGo = (int)t;
        cdBeat = (int)step;
        bool split = skin.Sound("count3s") != null;
        cdSounds = [((int)(cdGo - 5.9 * cdBeat), "readys"), (cdGo - 3 * cdBeat, split ? "count3s" : "count"),
            (cdGo - 2 * cdBeat, split ? "count2s" : "count"), (cdGo - cdBeat, split ? "count1s" : "count"), (cdGo, split ? "gos" : "count")];
    }

    void UpdateCountdown()
    {
        // Past cues are dropped rather than played late (a skip jumps over them).
        for (; cdNext < cdSounds.Length && cdSounds[cdNext].At <= time; cdNext++)
            if (time - cdSounds[cdNext].At < 100) skin.Sound(cdSounds[cdNext].Sound)?.Play(BeatmapSamples.EffectVolume);
    }

    void DrawCountdown(int w, int h)
    {
        if (double.IsNaN(cdGo) || time > cdGo + cdBeat) return;
        float sd = h / 768f, u = h / 480f;
        double g = cdGo, i = cdBeat, t = time;
        bool v2 = skin.Version > 1;
        float Seg(double a, double b) => (float)Math.Clamp((t - a) / Math.Max(1, b - a), 0, 1);
        var centre = new Vector2(w / 2f, h / 2f);
        void Put(string name, float alpha, float scale, float rot, float dx = 0, float alignX = 0.5f)
        {
            if (alpha <= 0 || skin.Tex(name) is not { } sp) return;
            float x = centre.X + dx + (0.5f - alignX) * sp.Width * sd * scale;
            sp.Draw(new Vector2(x, centre.Y), sd * scale, ColorAlpha(Color.White, alpha), rot * 180 / MathF.PI);
        }
        float rIn = Seg(g - 6 * i, g - 5 * i), rOut = Seg(g - 4 * i, g - 3 * i);
        Put("ready", rIn * (1 - rOut), v2 ? 1 + 0.2f * rOut : t < g - 4 * i ? 3 - 2 * rIn : 1 + 0.8f * rOut, v2 ? 0 : -0.4f * (1 - rIn));
        float offs = (skin.Tex("count1")?.Width ?? 0) / 6.8f * u;
        (string Name, double In)[] counts = [("count3", 3), ("count2", 2), ("count1", 1)];
        foreach (var (name, k) in counts)
        {
            float a = Seg(g - (k + 0.2) * i, g - k * i);
            float outA = v2 ? Seg(g - (k - 0.8) * i, g - (k - 1) * i) : Seg(g - 0.2 * i, g + 0.2 * i);
            float scale = v2 ? 1.4f - 0.4f * a : 1;
            float dx = v2 || k == 1 ? 0 : k == 3 ? -offs : offs;
            Put(name, a * (1 - outA), scale, 0, dx, v2 || k == 1 ? 0.5f : k == 3 ? 1 : 0);
        }
        float goOut = Seg(g + 0.3 * i, g + i);
        if (v2) Put("go", Seg(g - 0.2 * i, g) * (1 - goOut), 1.4f - 0.4f * Seg(g - 0.6 * i, g + 0.2 * i), 0);
        else
        {
            float e = (float)Easing.Apply(1, Seg(g - 0.6 * i, g + 0.2 * i));
            Put("go", Seg(g - 0.6 * i, g) * (1 - goOut), 0.2f + 0.8f * e, -4 * (1 - e));
        }
    }

    (double From, int Count)[]? blinks; // warning-arrow blink windows, built on first draw

    void DrawBreakOverlays(int w, int h)
    {
        DrawCountdown(w, h);
        float sd = h / 768f, u = h / 480f;
        if (!double.IsNaN(sectionAt) && skin.Tex(sectionPass ? "section-pass" : "section-fail") is { } sec)
        {
            double t = time - sectionAt;
            float a = sectionPass
                ? (t >= 20 && t < 100 || t >= 160 && t < 230 || t >= 280 ? 1 : 0)
                : (t >= 130 && t < 230 || t >= 280 ? 1 : 0);
            if (t >= 1280) a = (float)Math.Max(0, 1 - (t - 1280) / 200);
            if (a > 0) sec.Draw(new Vector2(w / 2f, h / 2f), sd, ColorAlpha(Color.White, a));
        }
        // Warning arrows, 80/100 units in from each corner (the right pair flipped): 100ms on, 100ms off, up to five
        // times before each break ends plus min(2, preempt/200) more after it.
        if ((skin.Tex("play-warningarrow") ?? skin.Tex("arrow-warning")) is not { } arrow) return;
        if (blinks == null)
        {
            int after = (int)Math.Min(2, map.Preempt / 200);
            var list = map.Breaks.Select(b => (Count: (int)Math.Min(5, (b.End - b.Start + 200) / 200) + after, End: b.End))
                .Select(b => (From: b.End - 200 * (b.Count - after), b.Count)).ToList();
            if (double.IsNaN(cdGo) && objs[0].Time > 6000) // no countdown: the arrows warn of the first object instead
                list.Add((objs[0].Time - map.Preempt - 900, 5 + after));
            blinks = [.. list];
        }
        foreach (var (from, count) in blinks)
        {
            if (time < from || time >= from + 200 * count || (time - from) % 200 >= 100) continue;
            var tint = skin.Version > 1 ? Xna.Red : Color.White; // stable reds the new-style arrow for v2+ skins
            for (int c = 0; c < 4; c++)
            {
                bool right = c >= 2;
                var at = new Vector2(right ? w - 80 * u : 80 * u, c % 2 == 0 ? 100 * u : h - 100 * u);
                arrow.Draw(at, sd, tint, flipX: right);
            }
        }
    }

    // stable's looping sounds: a followed slider loops its sampleset's "sliderslide" (with LayeredHitSounds, or when it
    // has no whistle) and, for whistle sliders, the addition set's "sliderwhistle"; a turning spinner loops the skin's
    // "spinnerspin", its frequency 20000 + 40000 x (spins / required) Hz (max 100000) with SpinnerFrequencyModulate.
    // Both stop when the slider is lost or ends, the spinner stops turning or ends, and on pause / fail / the end.
    int spinLoop;
    // stable: the slider and spinner sounds play at the current timing point's volume (5ms on), at least 8%
    int PointVolume => Math.Max(8, map.PointAt(time + 5).Volume);

    void UpdateLoopSounds()
    {
        bool live = !paused && !failed && !finished && !recovering;
        HitObject? slider = null;
        (HitObject O, ObjState S)? spinner = null;
        for (int i = firstActive; live && i < objs.Count && objs[i].Time <= time; i++)
        {
            var o = objs[i];
            if (time > o.EndTime || st[i].Done) continue;
            if (o.Type == ObjType.Slider && st[i].Tracking) slider ??= o;
            else if (o.Type == ObjType.Spinner) spinner ??= (o, st[i]);
        }
        if (slider is { } so)
        {
            // stable: the current timing point (5ms on) at its own volume, at least 8; the slider's own sample sets only in
            // v14+ maps
            var tp = map.PointAt(time + 5);
            bool own = map.FormatVersion >= 14;
            int set = own && so.NormalSet > 0 ? so.NormalSet : tp.SampleSet, addition = own && so.AdditionSet > 0 ? so.AdditionSet : set;
            int volume = PointVolume;
            bool whistle = (so.HitSound & 2) != 0;
            float pan = Pan(BallOf(so, time));
            if (skin.LayeredHitSounds || !whistle) samples.Loop("slide", set, "sliderslide", tp.SampleIndex, volume, pan); else samples.StopLoop("slide");
            if (whistle) samples.Loop("whistle", addition, "sliderwhistle", tp.SampleIndex, volume, pan); else samples.StopLoop("whistle");
        }
        else { samples.StopLoop("slide"); samples.StopLoop("whistle"); }

        if (spinner is { } sp && sp.S.Velocity != 0)
        {
            float vol = BeatmapSamples.EffectVolume * PointVolume / 100f;
            if (spinLoop == 0 || Bass.BASS_ChannelIsActive(spinLoop) != Bass.ActivePlaying)
            {
                if (spinLoop != 0) Bass.BASS_ChannelPlay(spinLoop, false);
                else spinLoop = skin.Sound("spinnerspin")?.PlayLooped(vol) ?? 0;
            }
            Bass.BASS_ChannelSetAttribute(spinLoop, Bass.AttribVol, vol);
            int req = SpinsRequired(sp.O);
            if (skin.SpinnerFrequencyModulate && req > 0)
                Bass.BASS_ChannelSetAttribute(spinLoop, Bass.AttribFreq, Math.Min(100000, 20000 + 40000f * sp.S.SpinsCounted / req));
        }
        else if (spinLoop != 0) Bass.BASS_ChannelPause(spinLoop);
    }

    // stable's "break" state: the intro (until the first object's approach starts), every break, and the outro (after
    // the last object's 50 window). Key counts, cursor stars and the background dim follow it.
    bool Resting(double t) => t < objs[0].Time - map.Preempt || t > objs[^1].EndTime + map.Hit50 || InBreak(t);

    bool InBreak(double t)
    {
        foreach (var (start, end) in map.Breaks)
            if (t >= start && t <= end) return true;
        return false;
    }

    bool InSpinner(double t)
    {
        for (int i = firstActive; i < objs.Count && objs[i].Time <= t; i++)
            if (objs[i].Type == ObjType.Spinner && t <= objs[i].EndTime) return true;
        return false;
    }

    void Drain(double upTo)
    {
        if (mode is 1 or 3) return; // taiko and mania: no drain (taiko's hits fill the bar from empty)
        double delta = double.IsNaN(lastTime) ? 0 : upTo - lastTime;
        if (delta < 0) return; // (a replay frame earlier than the last: drained already)
        lastTime = upTo;
        if (delta <= 0 || upTo < objs[0].Time - map.Preempt || upTo > objs[^1].EndTime || InBreak(upTo)) return;
        health.Add(-health.DropRate * delta * (InSpinner(upTo) ? 0.25 : 1)); // stable: a quarter of the drain during spinners
        if (health.Value <= 0 && HealthFails) Fail();
    }

    void UpdateClock(double dtMs)
    {
        if (music != null && !musicStarted && Raw >= 0)
        {
            if (Raw > 50) music.Seek(Raw);
            music.Play();
            musicStarted = true;
        }
        if (music != null && musicStarted && !musicEnded)
        {
            if (music.Stopped) musicEnded = true;
            else
            {
                double drift = music.PositionMs - Raw;
                driftSum += Math.Abs(drift); driftMax = Math.Max(driftMax, Math.Abs(drift)); driftCount++;
                if (Math.Abs(drift) > 40) snaps++;
                // Snap on big jumps; otherwise ease towards the audio so reported-position steps don't jitter gameplay.
                if (Math.Abs(drift) > 40) clockOrigin += drift;
                else clockOrigin += drift * Math.Min(1, dtMs / 500);
            }
        }
        // Whole microseconds: replays store integer µs, so live play and playback judge at identical times.
        // Never backwards: a sync snap or offset change just holds time until the clock catches up, so nothing
        // (replay input especially) gets judged twice.
        time = Math.Max(clockFloor, Math.Round((Raw - Offset) * 1000) / 1000);
        clockFloor = time;
    }

    internal bool Playing => !paused && !finished;
    internal bool Paused => paused;
    // stable's display name: "Artist - Title" (or the title, or the file's name) and " [Version]"
    internal string MapName => (map.Artist.Length > 0 ? $"{map.Artist} - {map.Title}" : map.Title.Length > 0 ? map.Title : Path.GetFileName(map.File))
        + (map.Version.Length > 0 ? $" [{map.Version}]" : "");
    // stable's "active play": a live play from the first object's approach to the last object's end, not paused or failed
    // (not before the clock starts: it waits for the song select preview to fade, and the time reads 0 meanwhile)
    internal bool ActivePlay => fadeOut == null && !paused && !failed && !finished && playback == null && !auto
        && time >= objs[0].Time - map.Preempt && time <= objs[^1].EndTime;
    internal void ApplyMusicVolume() { if (music != null) music.Volume = Config.MusicVolume; }
    // the Boss key pauses any play in progress (stable's does too)
    internal void BossPause() { if (!paused && !failed && !finished && !VirtualClock) SetPaused(true); }

    // stable: a live play can't pause again within a second (of song time) of its last pause
    double lastPauseAt = double.NaN;
    void TryPause(bool quiet = false)
    {
        if (playback == null && !auto && time - lastPauseAt < 1000)
        {
            if (!quiet) Toast.Show("Wait at least one second before pausing again.");
            return;
        }
        lastPauseAt = time;
        SetPaused(true);
    }

    void SetPaused(bool p)
    {
        paused = p;
        if (p)
        {
            sw.Stop();
            if (musicStarted) music?.Pause();
            menuShownAt = Game.NowMs;
            pauseLoop = skin.Sound("pause-loop")?.PlayLooped(BeatmapSamples.EffectVolume) ?? 0;
            pauseCursor = cursor;
            resumePending = false;
        }
        else
        {
            if (!recovering) { sw.Start(); if (musicStarted) music?.Play(); } // (an Easy life's refill keeps the song stopped)
            SoundPool.Stop(pauseLoop); pauseLoop = 0;
        }
    }

    // ---- stable's resume: live play outside breaks only continues once you click the orange ring left where your
    // cursor was when you paused (so the cursor can't jump), with a centred hint. Auto/replays/relax resume at once.
    bool resumePending;
    Vector2 pauseCursor;
    double resumeShownAt;
    static readonly Color Orange = new(255, 165, 0, 255);

    void Continue()
    {
        // (stable: only osu! asks for a click on the cursor; taiko, catch and mania carry on at once)
        if (mode == 0 && playback == null && !auto && !relax && !autopilot && !InBreak(time))
        {
            resumePending = true;
            resumeShownAt = Game.NowMs;
        }
        else SetPaused(false);
    }

    void UpdateResume()
    {
        float u = Game.H / 480f;
        bool over = Vector2.Distance(Input.GetMousePosition(), ToScreen(pauseCursor)) <= 12 * u; // 1.2 x 20 units / 2
        if (over && (Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsKeyPressed(key1) || Input.IsKeyPressed(key2)))
        {
            resumePending = false;
            SetPaused(false);
        }
    }

    void DrawResume()
    {
        int w = Game.W, h = Game.H;
        float u = h / 480f, sd = h / 768f;
        var at = ToScreen(pauseCursor);
        if (skin.DefaultTex("cursor") is { } ring) // stable's default cursor (osu!ui), tinted orange
        {
            bool over = Vector2.Distance(Input.GetMousePosition(), at) <= 12 * u;
            ring.Draw(at, 1.2f * sd, over ? Color.White : Orange);
            float t = (float)((Game.NowMs - resumeShownAt) % 1000 / 1000);
            BeginBlendMode(BlendMode.Additive);
            ring.Draw(at, (1.2f + 1.8f * t) * sd, ColorAlpha(Orange, 1 - (1 - (1 - t) * (1 - t))));
            EndBlendMode();
        }
        // The hint: osu!ui's "unpause" image (icon and text baked in) at screen centre.
        skin.DefaultTex("unpause")?.Draw(new Vector2(w / 2f, h / 2f), sd, Color.White);
        if (Vector2.Distance(Input.GetMousePosition(), at) <= 12 * u) Options.DrawScreenTooltip("Click to resume gameplay.", Input.GetMousePosition()); // stable's hover tooltip
    }

    void Skip()
    {
        if (outroSkip) { endNow = true; return; }
        skipped = true;
        double target = skipTarget + Random.Shared.Next(-80, 90); // stable's jitter
        if (music != null)
        {
            music.Seek(target);
            if (!musicStarted) { music.Play(); musicStarted = true; }
        }
        clockOrigin += target - Raw;
        storyboard?.SeekSamples(target);
    }

    public override void Update(double dtMs)
    {
        if (back.DialogOpen) back.HostDialog(); // (Manage Collections over a replay)
        int w = Game.W, h = Game.H;
        scale = h / 480f;
        origin = new Vector2((w - 512 * scale) / 2, (h - 384 * scale) / 2 + 8 * scale);

        if (UpdateStartHold(dtMs)) return;
        // The results screen takes over (and disposes us later), so the outro keeps playing like stable.
        if (finished && (cinema || ReturnTo != null)) { Leave(back); return; } // stable: Cinema ends at song select (a test play in the editor)
        if (finished) // stable goes to the ranking screen through black too
        {
            if (VirtualClock) Game.Screen = new Results(this);
            else Game.FadeTo(() => new Results(this));
            return;
        }
        bool visualClick = UpdateVisualSettings(dtMs);
        if (failed)
        {
            if (double.IsPositiveInfinity(menuShownAt)) { UpdateFailAnim(dtMs); return; }
            string? f = UpdateMenuKeys() ?? (visualClick ? null : UpdateMenu(dtMs));
            if (f == "pause-back" || menuSel < 0 && Input.IsKeyPressed(KeyboardKey.Enter) || Input.IsKeyPressed(KeyboardKey.Escape)) Leave(back);
            else if (f == "pause-retry" || Input.IsKeyPressed(KeyboardKey.R) || Input.IsKeyPressed(KeyboardKey.Grave)) Retry();
            else if (Input.IsKeyPressed(KeyboardKey.W) && (savedReplay ?? watching) is { } replay)
                Leave(new Player(map, back, replay.Mods, Replays.Frames(replay), replay));
            else FailReplayKeys();
            return;
        }
        if (TestPause is { } tp && testPauseStep < 2 && Game.NowMs / 1000 >= (testPauseStep == 0 ? tp.PauseAt : tp.ContinueAt))
        {
            if (testPauseStep++ == 0) SetPaused(true); else Continue();
        }
        if (!VirtualClock) { UpdateMapWarning(); UpdateDim(dtMs); UpdateHudFade(dtMs); UpdateLoopSounds(); }
        if (playback != null && TestSpeedAt.Count > 0 && Game.NowMs / 1000 >= TestSpeedAt[0]) { TestSpeedAt.RemoveAt(0); CycleSpeed(); }
        if (Completed && !paused && !failed)
        {
            if (Input.IsKeyPressed(KeyboardKey.Escape) || Input.IsKeyPressed(Config.Key("keyPause", KeyboardKey.Escape))) endNow = true;
            if (!outroSkip && !endNow && SbEnd - time > 3000) outroSkip = true;
        }
        bool esc = !endNow && Input.IsKeyPressed(KeyboardKey.Escape);
        if (playback != null && (esc || UpdateReplayOverlay())) { Leave(back); return; }
        if (auto && playback == null && (esc || UpdateReplayOverlay())) { Leave(back); return; } // stable: Auto is a replay, Esc / End Replay leave
        // stable: a live play also pauses / unpauses with the middle mouse button (not with the mouse wheel disabled in play)
        bool middle = playback == null && !auto && !Config.GetBool("MouseDisableWheel", false) && Input.IsMouseButtonPressed(MouseButton.Middle);
        if (!endNow && (Input.IsKeyPressed(KeyboardKey.Escape) || Input.IsKeyPressed(Config.Key("keyPause", KeyboardKey.Escape))) || middle)
        {
            if (Completed && !paused && !failed) endNow = true; // stable: after completion the pause key ends the play
            else if (!paused) TryPause();
            else if (resumePending) resumePending = false; // back to the pause menu
            else Continue();
        }
        else if (paused && resumePending) UpdateResume();
        else if (paused)
        {
            string? c = UpdateMenuKeys() ?? (visualClick ? null : UpdateMenu(dtMs));
            if (c == "pause-continue") { Continue(); return; } // not into gameplay input this frame
            else if (c == "pause-retry" || Input.IsKeyPressed(KeyboardKey.R)) Retry();
            else if (c == "pause-back" || Input.IsKeyPressed(KeyboardKey.Q)) Leave(back);
            else if (Input.IsKeyPressed(Config.Key("keyIncreaseAudioOffset", KeyboardKey.Equal)) || Input.IsKeyPressed(Config.Key("keyDecreaseAudioOffset", KeyboardKey.Minus)))
                Notification.Show("Offset can only be changed while game is not paused.", 1000);
        }
        // stable pauses a live play whose window loses focus, from the first object's approach to the last object's end
        if (ActivePlay && !Input.ScriptOnly && !VirtualClock && !IsWindowFocused()) TryPause(quiet: true);
        if (paused) return;
        // stable's quick retry: holding the Quick Retry key (or Ctrl+R) fills 10 a 60Hz tick and retries at 100;
        // letting go drains 3 a tick. The screen fades to black with it.
        bool retryHeld = Input.IsKeyDown(Config.Key("keyQuickRetry", KeyboardKey.Grave)) || Input.CtrlHeld && Input.IsKeyDown(KeyboardKey.R) && !mBind.Contains(KeyboardKey.R); // (not when R is a mania column)
        if (!retryHeld) retryArmed = true; // a retry needs the key let go first (it's usually still held when the new play starts)
        retryHold = retryHeld && retryArmed ? Math.Min(100, retryHold + 10 * dtMs / (1000 / 60.0)) : Math.Max(0, retryHold - 3 * dtMs / (1000 / 60.0));
        if (retryHold >= 100) { Retry(quick: true); return; }

        if (playback == null && !auto && (Input.IsKeyPressed(key1) || Input.IsKeyPressed(key2))) SongSelect.Ripple(Input.GetMousePosition()); // keys click too
        if (Input.IsKeyPressed(Config.Key("keyDisableMouseButtons", KeyboardKey.F10))) // stable: toggles the option in play
        {
            mouseButtons = !mouseButtons;
            Config.SetOwn("MouseDisableButtons", mouseButtons ? "0" : "1");
            Notification.Show(mouseButtons ? "Mouse buttons are enabled." : "Mouse buttons are disabled.", 1000);
        }
        if (mode == 3) ManiaSpeedKeys();
        int offsetStep = Input.AltHeld ? 1 : 5; // stable: Alt for 1ms steps
        if (Input.IsKeyPressed(Config.Key("keyIncreaseAudioOffset", KeyboardKey.Equal)) || Input.IsKeyPressed(KeyboardKey.KpAdd)) ChangeOffset(offsetStep);
        if (Input.IsKeyPressed(Config.Key("keyDecreaseAudioOffset", KeyboardKey.Minus)) || Input.IsKeyPressed(KeyboardKey.KpSubtract)) ChangeOffset(-offsetStep);
        // stable: the skip key does nothing during Auto (measured; its Skip button still shows)
        if (CanSkip && (!(auto && playback == null) && Input.IsKeyPressed(Config.Key("keySkip", KeyboardKey.Space)) || Input.IsMouseButtonPressed(MouseButton.Left) && SkipRect() is { } sr && CheckCollisionPointRec(Input.GetMousePosition(), sr)))
        {
            skin.Sound("menuhit")?.Play(BeatmapSamples.EffectVolume);
            Skip();
        }

        if (recovering)
        {
            if (!VirtualClock && hpShown <= health.Value - 5) return; // (the bar refills as it's drawn)
            recovering = false;
            hpFilling = false;
            skin.Sound("gos")?.Play(BeatmapSamples.EffectVolume);
            sw.Start();
            if (musicStarted && !musicEnded) music?.Play(); // (a song already over would start again)
        }
        if (VirtualClock) virtualElapsed += dtMs;
        UpdateClock(dtMs);
        // Replays are judged in 1ms steps of map time whatever the frame rate (like --verify-osr), so watching at
        // 60fps gives the same result as at 1000fps. Seeks (skip) jump straight through; nothing is judged there.
        // Only whole milliseconds are judged: a frame shorter than 1ms judges nothing and a longer one judges every
        // ms it covered, so the number of updates (sliders judge one checkpoint per update) never depends on fps.
        // Auto too (stable's is a replay): a stutter would otherwise press late (100s) and drop slider ticks.
        // A live osu! play as well: every whole millisecond before this frame is judged on the input as it stood, then
        // this frame's input comes in at its own time. Its replay takes the same steps, so the two can't differ.
        bool live = LiveStepped;
        if ((playback != null || auto || live) && !double.IsNaN(simTime) && time - simTime < 2000)
        {
            double target = time;
            for (double t = Math.Floor(simTime) + 1; (live ? t < target : t <= Math.Floor(target)) && !finished && !failed; t++)
            {
                // Test-only: stable's replay watcher runs game logic only when it takes the next replay frame (at
                // that frame's time), so --fuzz-osr can compare against it exactly. After the last frame it updates normally.
                if (Stepped && playback != null && pbIndex < playback.Length && playback[pbIndex].Time > t) { simTime = t; continue; }
                time = t;
                Step(Stepped ? t - lastStep : 1);
                lastStep = t;
                while (sameTimePending && !finished) { sameTimePending = false; Step(0); }
                simTime = t;
            }
            time = target;
            if (live && !finished && !failed) LiveInput();
        }
        else
        {
            if (!finished) Step(dtMs);
            simTime = time;
            if (live && !finished && !failed) LiveInput();
        }
    }

    bool LiveStepped => mode == 0 && playback == null && !auto;
    // a replay of such a play drains health up to each of its frames before what that frame presses, as the play did
    bool FrameDrain => playback != null && !OldModeRules && (mode == 2 || mode == 0 && !StableReplay && !FrameStepped && watching is not { FormatVersion: < 8 });

    double simTime = double.NaN;
    bool sameTimePending; // PlaybackInput stopped before a replay frame with the same time as the last one

    // One step of game logic at the current time.
    void Step(double dtMs)
    {
        NightcoreBeat();
        if (storyboard != null)
        {
            storyboard.PlaySamples(time);
            bool inBreak = InBreak(time);
            if (inBreak && !wasInBreak && storyboard.Passing != health.Value >= Health.Max / 2) // stable decides pass/fail at breaks
            {
                storyboard.Passing = !storyboard.Passing;
                storyboard.Fire(time, storyboard.Passing ? "Passing" : "Failing");
            }
            wasInBreak = inBreak;
        }
        // (a catch replay drains up to each of its frames before what it judges there, like the play it was made from)
        if (!FrameDrain) Drain(time);
        UpdateBreakSection();
        UpdateCountdown();
        if (mode != 0) { StepMode(dtMs); return; }
        if (playback == null) AdvanceSpinners(time); // live/auto: up to now on the previous frame's input
        cursor = auto || autopilot ? AutoCursor() : playback != null ? pbCursor : liveCursor;

        // Input first, then misses, like stable's update: a press in the same update as an object's miss time still
        // finds it unjudged (a press 1ms after the earlier circle's window closes is notelocked, not a hit on the next).
        if (playback != null) PlaybackInput();

        bool held;
        if (auto)
        {
            // Press every circle/slider head exactly when its time is reached, cursor on it.
            for (; autoNext < objs.Count && objs[autoNext].Time <= time; autoNext++)
            {
                var o = objs[autoNext];
                // stable's Auto clicks M1, alternating to M2 while objects follow closely; a gap of 266ms or a jump longer
                // than radius x (1.5 + 100 / gap) starts over on M1.
                // A spinner is aimed at the tangent point of its 50px spin circle, sqrt(d^2 - 50^2) away.
                if (autoNext > 0 && objs[autoNext - 1] is var p && (o.Time - p.EndTime) / rate is var gap)
                {
                    float d = Vector2.Distance(p.EndPos + map.StackOffset(p), PosOf(o));
                    if (o.Type == ObjType.Spinner) d = d > 50 ? MathF.Sqrt(d * d - 2500) : 50 - d;
                    autoAlt = gap > 0 && (d > map.Radius * (1.5 + 100 / gap) || gap >= 266) ? 0 : autoAlt + 1;
                }
                autoButton = autoAlt % 2 == 0 ? 4 : 8;
                if (o.Type == ObjType.Spinner) continue;
                cursor = PosOf(o);
                HandlePress();
                autoPressed |= (byte)autoButton; // counted on the overlay even if the press ends before the next frame
                autoReleaseAt = time + 50;
            }
            cursor = AutoCursor();
            held = time < autoReleaseAt;
            for (int i = firstActive; i < objs.Count && objs[i].Time <= time; i++)
                if (objs[i].Type != ObjType.Circle && time <= objs[i].EndTime) held = true;
            autoKeys = (byte)(held ? autoButton : 0);
        }
        else
        {
            // Relax: the click happens by itself once the object's time is reached and the cursor is on it.
            if (relax && NextPending() is int i && time >= objs[i].Time && Vector2.Distance(cursor, PosOf(objs[i])) <= map.Radius)
                HandlePress();
            held = relax || inputKeys != 0;
        }
        int lr = ((inputKeys & 5) != 0 ? 1 : 0) | ((inputKeys & 10) != 0 ? 2 : 0);
        btnNew = lr & ~btnCur;
        if (lr != btnCur) { btnPrev2 = btnPrev; btnPrev = btnCur; btnCur = lr; }
        UpdateObjects(held, dtMs);
        if (mods.HasFlag(Mods.Flashlight)) UpdateFlashlight(dtMs);
        StepEnd();
    }

    // the modes' step: their own input and judging, then the shared end
    void StepMode(double dtMs)
    {
        if (mode == 1) TaikoStep();
        else if (mode == 2) CatchStep();
        else if (mode == 3) ManiaStep();
        if (mods.HasFlag(Mods.Flashlight)) UpdateFlashlight(dtMs);
        StepEnd();
    }

    void StepEnd()
    {
        if (time >= objs[0].Time && time <= objs[^1].EndTime && (hpGraph.Count == 0 || time - hpGraph[^1].X >= (VirtualClock ? 10 : 250))) // verify-osr compares it with stable's life bar
            hpGraph.Add(new((float)time, (float)(health.Value / Health.Max)));

        while (firstActive < objs.Count && st[firstActive].Done && time > Math.Max(st[firstActive].JudgeTime, objs[firstActive].EndTime) + 1000)
            firstActive++;
        if (endNow || ModeDone && (musicEnded || music == null || time > objs[^1].EndTime + 3000 && time >= SbEnd - 500))
        {
            // stable's taiko: a play ending with the bar not over half is failed (not with NoFail or Relax)
            if (mode == 1 && health.Value <= Health.Max / 2 && HealthFails && !auto) { Fail(); return; } // (also when the play is ended early after completion: a replay ends the same way)
            finished = true;
            passStates.Add(health.Value >= Health.Max / 2);
            SaveReplay();
            Console.Error.WriteLine($"[{mods.Short()}] audio sync: mean |drift| {driftSum / Math.Max(1, driftCount):0.000}ms, max {driftMax:0.00}ms, snaps {snaps}; score {score} acc {Accuracy:0.00}% 300/100/50/miss {n300}/{n100}/{n50}/{nMiss} maxcombo {maxCombo}/{MapMaxCombo} hp {health.Value:0.0} drop {health.DropRate:0.0000}/ms");
        }
    }

    static bool Down(KeyboardKey k) => Input.IsKeyDown(k) || Input.IsKeyPressed(k); // Pressed catches a tap shorter than one frame
    static bool Down(MouseButton b) => Input.IsMouseButtonDown(b) || Input.IsMouseButtonPressed(b);

    void LiveInput()
    {
        var (keys, raw) = Bot ? BotInput() : DeviceInput();
        // 1/64 osu!px grid (far below tablet resolution) so replays store exact small integers.
        var mouse = new Vector2(MathF.Round(raw.X * Replays.PosScale), MathF.Round(raw.Y * Replays.PosScale)) / Replays.PosScale;
        // Record only changes: frames then follow the tablet/mouse report rate, not the render rate.
        bool smoke = !Bot && Down(keySmoke);
        bool changed = keys != inputKeys || mouse != lastRecorded || smoke != smokeHeld;
        if (!changed || recording.Count > 0 && recording[^1].Time >= time) return; // (one frame a moment: the next takes it)
        AdvanceSpinners(time); // spinners run up to here on the input before
        smokeHeld = smoke;
        recording.Add(new ReplayFrame(time, mouse.X, mouse.Y, (byte)(keys | (smoke ? SmokeBit : 0))));
        lastRecorded = liveCursor = mouse;
        cursor = autopilot ? AutoCursor() : mouse;
        int newly = keys & ~inputKeys;
        inputKeys = keys;
        Drain(time);
        if (failed) return;
        if (!relax)
            for (int b = 0; b < 4; b++)
                if ((newly >> b & 1) != 0) { pressButton = (b & 1) == 0 ? 1 : 2; HandlePress(); }
    }

    (byte, Vector2) DeviceInput()
    {
        byte keys = (byte)((Down(key1) ? 1 : 0) | (Down(key2) ? 2 : 0));
        if (mouseButtons) keys |= (byte)((Down(MouseButton.Left) ? 4 : 0) | (Down(MouseButton.Right) ? 8 : 0));
        return (keys, (Input.GetMousePosition() - origin) / scale);
    }

    // Test-only (--bot): stands in for the keyboard/tablet so the replay round trip can be checked without a
    // human. Auto's cursor, presses with seeded timing jitter (+-60ms) and every 37th object skipped.
    public static bool Bot;
    public static (double PauseAt, double ContinueAt)? TestPause; // --test-pause a,b (seconds since launch)
    public static List<double> TestSpeedAt = []; // --test-speed a,b,... (seconds since launch): press the replay speed button
    int testPauseStep;
    int botNext;
    double botReleaseAt;
    byte botKey = 1;

    (byte, Vector2) BotInput()
    {
        for (; botNext < objs.Count; botNext++)
        {
            var o = objs[botNext];
            int jitter = (int)((uint)(botNext * 2654435761u) % 121) - 60;
            if (o.Time + jitter > time) break;
            if (o.Type == ObjType.Spinner || botNext % 37 == 36) continue;
            botKey = (byte)(botKey == 1 ? 2 : 1);
            botReleaseAt = time + 40;
        }
        bool hold = time < botReleaseAt;
        for (int i = firstActive; i < objs.Count && objs[i].Time <= time; i++)
            if (objs[i].Type != ObjType.Circle && time <= objs[i].EndTime) hold = true;
        // Cursor moves at 1000Hz, like a tablet's report rate.
        double now = time;
        time = Math.Floor(time);
        var pos = AutoCursor(botPath: true);
        time = now;
        return (hold ? botKey : (byte)0, pos);
    }

    // Replays re-apply each press at its exact recorded map time, so hit errors come out identical.
    void PlaybackInput()
    {
        double now = time;
        while (pbIndex < playback!.Length && playback[pbIndex].Time <= now)
        {
            var f = playback[pbIndex++];
            AdvanceSpinners(f.Time); // spinners run up to this frame on the previous input
            pbCursor = new Vector2(f.X, f.Y);
            if (!autopilot) cursor = pbCursor;
            byte fk = (byte)(f.Keys & 15);
            smokeHeld = (f.Keys & SmokeBit) != 0;
            int newly = fk & ~inputKeys;
            inputKeys = fk;
            // the key overlay counts per replay frame (stable's watcher takes one frame per update), so a press that
            // starts and ends within one of our frames still counts
            if (newly != 0 && !Resting(f.Time) && !paused && !failed)
                for (int b = 0; b < 4; b++) if ((newly >> b & 1) != 0) keyCounts[b]++;
            if (StableReplay) foreach (var (so, ss) in ActiveSpinners(f.Time)) SpinnerInput(so, ss, f.Time);
            // stable runs an update per replay frame: frames sharing a time are judged one at a time, so stop here
            // and let the stepping loop run another (zero-length) update at this time for the next one.
            bool split = pbIndex < playback.Length && playback[pbIndex].Time == f.Time;
            if (FrameDrain && mode == 0) { Drain(f.Time); if (failed) break; }
            if (newly == 0 || relax) { if (split) { sameTimePending = true; break; } continue; }
            time = f.Time;
            if (FrameDrain && autopilot) cursor = AutoCursor();
            for (int b = 0; b < 4; b++)
                if ((newly >> b & 1) != 0) { pressButton = (b & 1) == 0 ? 1 : 2; HandlePress(); }
            time = now;
            if (split) { sameTimePending = true; break; }
        }
        AdvanceSpinners(now);
        if (FrameDrain && mode == 0) Drain(now);
        // Stable replays store the cursor at ~60Hz, but stable judged slider tracking live at a much higher
        // rate. Interpolating between frames approximates the cursor it actually saw. (Our own replays record
        // every change, so they're played back as-is to stay exact.)
        if (watching is { FromStable: true } && !autopilot && pbIndex > 0 && pbIndex < playback.Length)
        {
            var a = playback[pbIndex - 1];
            var b2 = playback[pbIndex];
            if (b2.Time > a.Time)
            {
                // clamped: past the next frame (not stepped there yet) it would overshoot along the path, and the trail draws that
                pbCursor = Vector2.Lerp(new Vector2(a.X, a.Y), new Vector2(b2.X, b2.Y), (float)Math.Clamp((now - a.Time) / (b2.Time - a.Time), 0, 1));
                cursor = pbCursor;
            }
        }
    }

    ReplayInfo CurrentInfo() => new()
    {
        BeatmapMd5 = Replays.Md5Of(map.File), Artist = map.Artist, Title = map.Title, Version = map.Version,
        Mods = mods, Score = score, MaxCombo = maxCombo, N300 = n300, N100 = n100, N50 = n50, NMiss = nMiss,
        NGeki = nGeki, NKatu = nKatu, Mode = mode, Seed = seed, Accuracy = Accuracy, Date = DateTime.Now,
    };

    void SaveReplay()
    {
        if (auto || playback != null || recording.Count == 0 || ReturnTo != null) return;
        try
        {
            savedReplay = Replays.Save(CurrentInfo(), recording);
            back.AddScore(savedReplay);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notification.Show($"Replay not saved: {e.Message}", 3000); }
    }

    // stable's fail screen keys for a live play: F1 watches the attempt, F2 saves its replay.
    bool LiveFail => failed && playback == null && !auto && recording.Count > 0;

    void FailReplayKeys()
    {
        if (!LiveFail) return;
        if (Input.IsKeyPressed(KeyboardKey.F1)) Leave(new Player(map, back, mods, recording.ToArray(), CurrentInfo()));
        else if (Input.IsKeyPressed(KeyboardKey.F2) && !failSaved)
            try { Toast.Show($"Saved replay to {Replays.Save(CurrentInfo(), recording, "Failed").Path}.", new Color(138, 43, 226, 255), 6000); failSaved = true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notification.Show($"Replay not saved: {e.Message}", 3000); }
    }
    bool failSaved;
    static bool mouseWarned;

    // botPath: the test bot's original smoothstep path, kept so replays generated earlier (and stable's recorded
    // results for them) stay reproducible.
    Vector2 AutoCursor(bool botPath = false) => AutoCursorAt(time, botPath);

    static readonly Vector2 SpinCentre = new(256, 192);

    Vector2 AutoCursorAt(double t, bool botPath = false)
    {
        var centre = SpinCentre;
        Vector2 prevPos = centre;
        double prevEnd = objs[0].Time - map.Preempt;
        if (!botPath)
        {
            // stable's Auto opens below the screen at (256, 500), rises to the centre between 1500 and 1000ms before
            // the first object, then moves on from there
            double first = objs[0].Time;
            if (t < first - 1000) return new Vector2(256, 500 - 308 * (float)Math.Clamp((t - (first - 1500)) / 500, 0, 1));
            prevEnd = first - 1000;
        }
        for (int i = Math.Max(0, firstActive - 1); i < objs.Count; i++)
        {
            var o = objs[i];
            bool spin = o.Type == ObjType.Spinner;
            if (o.EndTime < t) { prevPos = !spin ? o.EndPos + map.StackOffset(o) : botPath ? centre : AutoSpin(i, o.EndTime); prevEnd = o.EndTime; continue; }
            if (t >= o.Time)
                return o.Type switch
                {
                    ObjType.Slider => BallOf(o, t),
                    ObjType.Spinner when botPath => centre + 50 * new Vector2(MathF.Cos((float)t * 0.05f), MathF.Sin((float)t * 0.05f)),
                    ObjType.Spinner => AutoSpin(i, t),
                    _ => PosOf(o),
                };
            var target = !spin ? PosOf(o) : botPath ? centre : SpinEntry(SpinFrom(i));
            // stable's Auto: waits for the object to appear, then eases out (quad) to it (measured: 72% of the way at 46%)
            double start = botPath ? prevEnd : Math.Max(prevEnd, o.Time - map.Preempt);
            float f = (float)Math.Clamp((t - start) / Math.Max(1, o.Time - start), 0, 1);
            f = botPath ? f * f * (3 - 2 * f) : 1 - (1 - f) * (1 - f);
            return Vector2.Lerp(prevPos, target, f);
        }
        return prevPos;
    }

    // Where Auto comes from into spinner i: the previous object's end (a spinner before it: its last point).
    Vector2 SpinFrom(int i) => i == 0 ? SpinCentre : objs[i - 1].Type != ObjType.Spinner ? objs[i - 1].EndPos + map.StackOffset(objs[i - 1]) : AutoSpin(i - 1, objs[i - 1].EndTime);

    // stable's Auto enters a spinner at the tangent point of its 50-unit circle seen from where it was (inside it: the
    // circle point toward it), rotating the offset like stable does (its Y uses the already-rotated X).
    static Vector2 SpinEntry(Vector2 from)
    {
        var v = SpinCentre - from;
        float len = v.Length();
        if (len <= 50) return len > 0 ? SpinCentre - v * (50 / len) : SpinCentre + new Vector2(0, -50);
        float tangent = MathF.Sqrt(len * len - 2500), a = MathF.Asin(50 / len);
        float x = v.X * MathF.Cos(a) - v.Y * MathF.Sin(a);
        float y = x * MathF.Sin(a) + v.Y * MathF.Cos(a);
        return from + Vector2.Normalize(new Vector2(x, y)) * tangent;
    }

    // stable's Auto spin: replay frames every 16.67ms of real time stored at whole ms, angle -elapsed real ms / 20
    // from the entry point, straight lines between frames (so a third of the steps go faster than the spin cap).
    Vector2 AutoSpin(int i, double t)
    {
        var o = objs[i];
        var entry = SpinEntry(SpinFrom(i)) - SpinCentre;
        float a0 = MathF.Atan2(entry.Y, entry.X), step = (float)(1000 / 60.0 * rate);
        double len = o.EndTime - o.Time, el = Math.Clamp(t - o.Time, 0, len);
        Vector2 At(double e) => SpinCentre + 50 * new Vector2(MathF.Cos(a0 - (float)(e / rate / 20)), MathF.Sin(a0 - (float)(e / rate / 20)));
        double FrameTime(int k) => k == 0 ? o.Time : k * step >= len ? o.EndTime : (int)(o.Time + k * step);
        int k = (int)(el / step);
        if (k > 0 && t < FrameTime(k)) k--;
        double e0 = k * (double)step, e1 = Math.Min(len, (k + 1) * (double)step), t0 = FrameTime(k), t1 = FrameTime(k + 1);
        float f = t1 > t0 ? (float)Math.Clamp((t - t0) / (t1 - t0), 0, 1) : 1;
        return Vector2.Lerp(At(Math.Min(e0, len)), At(e1), f);
    }

    // stable: +/- change the map's local offset (5ms, 1 with Shift; within +-1000) in the first 10 seconds of the
    // map, in breaks, or anytime when watching; at most every 150ms. Kept in remake.cfg, never in osu!.db.
    void ChangeOffset(int delta)
    {
        if (!(time <= objs[0].Time + 10000 || InBreak(time) || auto || playback != null) || Game.NowMs - localOffsetChangedAt < 150) return;
        localOffsetChangedAt = Game.NowMs;
        localOffset = Math.Clamp(localOffset + delta, -1000, 1000);
        Config.SetOwn($"LocalOffset_{mapMd5 ??= Replays.Md5Of(map.File)}", localOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Notification.Show($"Local beatmap offset set to {localOffset}ms", 1000);
    }

    void MissExpired(int i)
    {
        var o = objs[i];
        var s = st[i];
        if (time <= o.Time + map.Hit50) return;
        if (o.Type == ObjType.Circle && !s.Done) Final(i, 0, PosOf(o));
        else if (o.Type == ObjType.Slider && !s.HeadDone)
        {
            s.HeadDone = true;
            if (VerifyLog) Console.Error.WriteLine($"  slider head #{i} at {o.Time} expired unclicked (cursor {Vector2.Distance(cursor, PosOf(o)) / map.Radius:0.00}r)");
            BreakCombo();
            health.Apply(HpEvent.SliderBreak); ColourMeterHit(0);
        }
    }

    // A slider shorter than its head's 50 window reaches its end before the head expires. Like stable, it's judged
    // then (the head counting as missed) and can't be clicked any more, but the missed head only breaks combo once
    // its window closes: the slider's own judgement still gets the combo bonus.
    void MissUnclickedHead(int i)
    {
        var s = st[i];
        if (s.HeadDone) return;
        s.HeadDone = true;
        s.HeadMissPending = true;
    }

    // Notelock target: the earliest circle/slider head that hasn't been judged yet.
    int? NextPending()
    {
        for (int i = firstActive; i < objs.Count; i++)
        {
            var o = objs[i];
            if (o.Type == ObjType.Spinner) continue;
            if (o.Type == ObjType.Circle ? st[i].Done : st[i].HeadDone) continue;
            return i;
        }
        return null;
    }

    // Notelock like stable: only the earliest unhit circle/slider head can take the click.
    // stable's click rules (from its hit policy):
    // - the click goes to the earliest visible, unhit circle/slider head under the cursor;
    // - notelock: any earlier object not fully judged that ends more than 3ms before it blocks the click (shake);
    // - clicks 400ms or more away are ignored; otherwise |offset| is judged with strict < against the
    //   whole-ms windows, so a click between the 50 window and 400ms is a miss.
    void HandlePress()
    {
        int i = -1;
        for (int k = firstActive; k < objs.Count && objs[k].Time - map.Preempt <= time; k++)
        {
            var c = objs[k];
            if (c.Type == ObjType.Spinner || (c.Type == ObjType.Circle ? st[k].Done : st[k].HeadDone)) continue;
            if (Vector2.Distance(cursor, PosOf(c)) <= map.Radius) { i = k; break; }
        }
        if (i < 0) return;
        var o = objs[i];
        var s = st[i];
        // stable: a click on a circle whose previous object is stacked (stack index above 0) and still unjudged does
        // nothing at all (no notelock shake either)
        if (o.Type == ObjType.Circle && i > 0 && objs[i - 1].Stack > 0 && !st[i - 1].Done && time >= objs[i - 1].Time - map.Preempt) return;
        for (int k = firstActive; k < i; k++)
            if (!st[k].Done && objs[k].EndTime + 3 < o.Time) { if (VerifyLog) Console.Error.WriteLine($"  notelock: press at {time:0} on #{i} blocked by #{k}"); return; } // notelock
        double err = time - o.Time;
        double abs = Math.Abs(err);
        if (abs >= 400) return;
        int result = abs < map.Hit300 ? 300 : abs < map.Hit100 ? 100 : abs < map.Hit50 ? 50 : 0;
        if (result > 0)
        {
            hitErrors.Add(err / rate); // shown in real milliseconds
            MeterHit(err, result);
        }

        if (o.Type == ObjType.Circle)
        {
            if (result > 0) PlayHitsound(o.HitSound, o.NormalSet, o.AdditionSet, o.SampleIndex, o.Volume, o.Time, o.SampleFile, pan: Pan(PosOf(o)));
            Final(i, result, PosOf(o));
        }
        else if (result == 0)
        {
            s.HeadDone = true; s.TrackButton = pressButton; // clicked too early/late: the head is missed
            if (VerifyLog) Console.Error.WriteLine($"  slider head #{i} at {o.Time} clicked outside the windows at {time:0}");
            BreakCombo();
            health.Apply(HpEvent.SliderBreak); ColourMeterHit(0);
        }
        else
        {
            s.HeadDone = true; s.HeadHit = true; s.HeadTime = time; s.HeadResult = result; s.TrackButton = pressButton;
            s.CheckpointsHit++;
            score += 30;
            combo++;
            maxCombo = Math.Max(maxCombo, combo);
            V2Hit(30);
            health.Apply(HpEvent.SliderPoint);
            PlayEdge(o, 0);
        }
    }

    // Every object's final judgement goes through here: counts, score, combo, HP, SD/PF and combo-end bonus.
    void Final(int i, int result, Vector2 popupPos)
    {
        if (!OldModeRules && result is 100 or 50 && mods.HasFlag(Mods.Perfect)) result = 0; // stable: Perfect makes a 100 or 50 a miss
        var o = objs[i];
        var s = st[i];
        s.TrackedAtEnd = s.Tracking;
        s.Done = true; s.Result = result; s.JudgeTime = time; s.Tracking = false;
        if (VerifyLog && result != 300) Console.Error.WriteLine($"  {result} on {o.Type} #{i} at {o.Time} (judged at {time:0}){(o.Type == ObjType.Slider ? $" head {(s.HeadHit ? "hit" : "missed")}, {s.CheckpointsHit}/{1 + o.Checkpoints.Count} points" : "")}");
        Count(result);
        if (result > 0)
        {
            ScoreHit(result);
            storyboard?.Fire(time, "HitObjectHit");
            if (o.Type != ObjType.Slider) { combo++; maxCombo = Math.Max(maxCombo, combo); } // slider combo comes from its checkpoints
            V2Hit(result);
            if (o.Type != ObjType.Spinner && Config.GetBool("HitLighting", true)) lightings.Add((popupPos, ColourOf(o), time));
            health.Apply(result switch { 300 => HpEvent.Hit300, 100 => HpEvent.Hit100, _ => HpEvent.Hit50 });
            ColourMeterHit(result);
            if (result != 300 && mods.HasFlag(Mods.Perfect)) Fail();
        }
        else
        {
            if (o.Type != ObjType.Slider) BreakCombo(); // a fully missed slider already broke combo at its head
            UpdateV2();
            health.Apply(HpEvent.Miss);
            ColourMeterHit(0);
            if (mods.HasFlag(Mods.SuddenDeath)) Fail();
        }
        comboWorst = Math.Min(comboWorst, result);
        string sprite = result switch { 300 => "hit300", 100 => "hit100", 50 => "hit50", _ => "hit0" };
        if (i == objs.Count - 1 || objs[i + 1].StartsCombo) // the next object starts a combo (spinners included)
        {
            health.Add(health.ComboEnd(comboWorst, lastHit: result > 0));
            // stable's combo-end sprites: the geki (hit300g) after an all-300 combo, the katu ones after 100s and 300s only
            if (comboWorst == 300) sprite = "hit300g";
            else if (comboWorst == 100 && result >= 100) sprite = result == 300 ? "hit300k" : "hit100k";
            if (comboWorst == 300) nGeki++;
            else if (comboWorst == 100) nKatu++;
            comboWorst = 300;
        }
        popups.Add((popupPos, time, result, sprite, o.Type == ObjType.Spinner));
        // stable's ranking graph: a point per judgement, HP against what a perfect play would have here (capped at 1).
        if (i < health.SimHp.Length && health.SimHp[i] > 0) perfGraph.Add(new((float)time, (float)Math.Min(1, health.Value / health.SimHp[i])));
    }

    void UpdateObjects(bool held, double dtMs)
    {
        for (int i = firstActive; i < objs.Count && objs[i].Time - map.Preempt <= time; i++)
        {
            var o = objs[i];
            var s = st[i];
            if (s.Done)
            {
                if (s.HeadMissPending && time > o.Time + map.Hit50)
                {
                    s.HeadMissPending = false;
                    if (VerifyLog) Console.Error.WriteLine($"  slider head #{i} at {o.Time} (slider already judged) expired unclicked");
                    BreakCombo();
                    health.Apply(HpEvent.SliderBreak); ColourMeterHit(0);
                }
                continue;
            }
            // stable, per object in order: its update (slider ticks, end), then its miss check (an expired circle or
            // slider head). A slider end judged in the update its head expires still counts, then the head breaks combo.
            if (o.Type == ObjType.Slider) UpdateSlider(i, held);
            else if (o.Type == ObjType.Spinner) UpdateSpinner(i, dtMs);
            MissExpired(i);
        }
    }

    // stable (osu! slider update): a slider remembers the button it's tracked with. With one button
    // down that's re-taken each update while the history differs from "both, then back to this one", so
    // alternating keeps tracking. But a slider started by pressing a key while the other was held is tracked
    // with the new key, and releasing it (other still held) drops tracking like stable does.
    bool ButtonHolds(ObjState s)
    {
        if (btnCur == 0) { s.TrackButton = 0; return false; }
        bool changed = btnPrev != 3 || btnPrev2 != btnCur;
        if (s.TrackButton == 0 || btnCur != 3 && changed)
        {
            s.TrackButton = (btnNew & 1) != 0 ? 1 : (btnNew & 2) != 0 ? 2 : btnCur;
            return true;
        }
        return (btnCur & s.TrackButton) != 0 || changed;
    }

    void UpdateSlider(int i, bool held)
    {
        var o = objs[i];
        var s = st[i];
        if (time < o.Time) return;
        // stable: integer time, its segmented ball, follow radius 2.4x once tracking. A checkpoint counts only if
        // tracking began by its time, and at most one checkpoint is judged per update.
        var ball = o.StableBallAt((int)Math.Floor(time)) + map.StackOffset(o);
        float followR = s.Tracking ? map.Radius * 2.4f : map.Radius;
        bool was = s.Tracking;
        bool holds = auto || relax ? held : ButtonHolds(s);
        s.Tracking = holds && Vector2.DistanceSquared(cursor, ball) < followR * followR;
        if (s.Tracking && !was) s.TrackingSince = Math.Floor(time);
        else if (!s.Tracking && was) s.FollowLostAt = time;
        if (VerifyLog && s.Tracking != was) Console.Error.WriteLine($"  tracking {(s.Tracking ? "gained" : "lost")} on slider #{i} at {time:0} (cursor {Vector2.Distance(cursor, ball) / map.Radius:0.00}r, held={held})");

        if (s.NextCheckpoint < o.Checkpoints.Count && o.Checkpoints[s.NextCheckpoint].Time <= time)
        {
            var cp = o.Checkpoints[s.NextCheckpoint++];
            if (s.Tracking && s.TrackingSince <= cp.Time)
            {
                s.CheckpointsHit++;
                // stable scores a checkpoint by position, not by what it is: with i = how many checkpoints (in list
                // order) have passed, the last is an end, every (count / slides)th a repeat, the rest ticks. Spans
                // with different tick counts make it call some ticks repeats and some repeats ticks (10 vs 30 points).
                int passed = 0, n = o.Checkpoints.Count;
                while (passed < n && o.Checkpoints[passed].Time <= time) passed++;
                bool tickScore = passed != n && passed % Math.Max(1, n / o.Slides) != 0;
                score += tickScore ? 10 : 30;
                combo++;
                maxCombo = Math.Max(maxCombo, combo);
                V2Hit(tickScore ? 10 : 30);
                health.Apply(tickScore ? HpEvent.SliderTick : HpEvent.SliderPoint);
                if (passed != n && !VirtualClock && PointPops) pointPops.Add((BallOf(o, time), time, tickScore)); // (not the end)
                if (cp.Kind == CheckpointKind.Tick)
                {
                    var tp = map.PointAt(cp.Time + 2); // stable: the timing point 2ms on, at its own volume
                    samples.Play(o.NormalSet > 0 ? o.NormalSet : tp.SampleSet, "slidertick", tp.SampleIndex, tp.Volume, SamplePitch, Pan(BallOf(o, cp.Time)));
                }
                else PlayEdge(o, cp.Kind == CheckpointKind.End ? o.Slides : cp.Span + 1);
            }
            else
            {
                if (VerifyLog) Console.Error.WriteLine($"  checkpoint miss: {cp.Kind} at {cp.Time} on slider {o.Time}-{o.EndTime} (cursor {Vector2.Distance(cursor, BallOf(o, time)) / map.Radius:0.00}r from ball, held={inputKeys != 0})");
                if (cp.Kind != CheckpointKind.End) BreakCombo(); // a missed slider end costs HP but keeps combo
                health.Apply(HpEvent.SliderBreak); ColourMeterHit(0);
            }
        }

        if (time >= o.EndTime && s.NextCheckpoint >= o.Checkpoints.Count)
        {
            MissUnclickedHead(i);
            double frac = s.CheckpointsHit / (double)(1 + o.Checkpoints.Count);
            int head = s.HeadHit ? s.HeadResult : 0;
            int result = !ScoreV2 ? (frac >= 1 ? 300 : frac >= 0.5 ? 100 : frac > 0 ? 50 : 0)
                : frac >= 1 && head == 300 ? 300 : frac >= 0.5 && head >= 100 ? 100 : frac > 0 ? 50 : 0; // Score V2: the head's timing caps it
            Final(i, result, o.EndPos + map.StackOffset(o));
        }
    }

    // ---- stable's spinner (momentum model) ----
    // Spins are counted in half-turns. The cursor's spin speed is sampled on a 60Hz tick (or per frame for
    // stable replays, which were recorded on that tick) and becomes a target velocity; the spinner's own
    // velocity accelerates toward it with a capped acceleration and is limited to 0.05 rad/ms.
    // Simulated in fixed 1ms map-time steps so it's frame-rate independent and our replays stay exact.
    const double TickMs = 1000.0 / 60;
    bool StableReplay => watching is { FromStable: true };
    bool SpinHeld => auto || relax || inputKeys != 0;

    int SpinsRequired(HitObject o) => (int)((float)(o.EndTime - o.Time) / 1000f * map.SpinsPerSecond);

    IEnumerable<(HitObject, ObjState)> ActiveSpinners(double t)
    {
        for (int i = firstActive; i < objs.Count && objs[i].Time <= t; i++)
            if (objs[i].Type == ObjType.Spinner && !st[i].Done) yield return (objs[i], st[i]);
    }

    // Advance active spinners to map time t using the input state as it stood before t.
    void AdvanceSpinners(double t)
    {
        for (int i = firstActive; i < objs.Count && objs[i].Time <= t; i++)
        {
            var o = objs[i];
            var s = st[i];
            if (o.Type != ObjType.Spinner || s.Done) continue;
            if (double.IsNaN(s.SimTime)) { s.SimTime = Math.Floor(o.Time); s.NextTick = o.Time; s.SmoothElapsed = TickMs; } // stable starts its average at a 60Hz frame
            double maxAccel = 8e-5 + Math.Max(0, (5000 - (o.EndTime - o.Time)) / 1000 / 2000);
            double end = Math.Min(t, o.EndTime);
            while (s.SimTime + 1 <= end)
            {
                s.SimTime += 1;
                if (!StableReplay && s.SimTime >= s.NextTick) { s.NextTick += TickMs * rate; SpinnerInput(o, s, s.SimTime); } // 60Hz of real time
                if (s.SimTime > o.Time && s.SimTime < o.EndTime) StepSpinner(o, s, maxAccel);
            }
        }
    }

    // Input sample: cursor angle change -> target angular velocity (rad per real ms).
    void SpinnerInput(HitObject o, ObjState s, double t)
    {
        double elapsed = double.IsNaN(s.LastInputTime) ? TickMs : t - s.LastInputTime;
        s.LastInputTime = t;
        if (elapsed < 0) return;
        var v = (auto ? AutoCursorAt(t) : cursor) - SpinCentre; // Auto: its spin path at the tick's own time
        double ang = Math.Atan2(v.Y, v.X);
        if (!s.HasAngle) { s.LastAngle = ang; s.HasAngle = true; }
        double d = ang - s.LastAngle;
        if (d < -Math.PI) d += 2 * Math.PI;
        else if (d > Math.PI) d -= 2 * Math.PI;
        double k = Math.Pow(0.999, elapsed);
        s.SmoothElapsed = k * s.SmoothElapsed + (1 - k) * elapsed;
        if (d == 0) s.Target = s.ZeroCount++ < 1 ? s.Target / 3 : 0;
        else
        {
            s.ZeroCount = 0;
            if (!SpinHeld || t < o.Time || t > o.EndTime) d = 0;
            if (Math.Abs(d) >= Math.PI) s.Target = 0;
            else if (s.SmoothElapsed / rate > 17.333332697550457) s.Target = elapsed > 0 ? d / (elapsed / rate) : 0;
            else s.Target = d / TickMs;
        }
        s.LastAngle = ang;
    }

    void StepSpinner(HitObject o, ObjState s, double maxAccel)
    {
        if (mods.HasFlag(Mods.SpunOut) || autopilot) s.Velocity = 0.03;
        else
        {
            double a = maxAccel / rate; // acceleration is per real ms; one step is 1ms of map time
            s.Velocity += s.Target > s.Velocity ? Math.Min(s.Target - s.Velocity, a) : Math.Max(s.Target - s.Velocity, -a);
        }
        s.Velocity = Math.Clamp(s.Velocity, -0.05, 0.05);
        s.Rotation += Math.Min(1, Math.Abs(s.Velocity / Math.PI)); // half-turns
        s.Angle += s.Velocity;
        // stable's RPM readout: eased 0.9 a 60Hz tick toward the current speed (rad/ms -> turns/min).
        double kr = Math.Pow(0.9, 1 / (1000 / 60.0));
        s.Rpm = s.Rpm * kr + (1 - kr) * Math.Abs(s.Velocity) * 1000 / (2 * Math.PI) * 60;
        spinRpms.Add((int)s.Rpm);
        int req = SpinsRequired(o);
        // stable fades "SPIN!" out over 300ms once a quarter of the required count is spun (all of it for replays
        // recorded before 2019-05-10); a quarter of 0 fades it right at the start.
        int spinText = watching is { Date.Year: > 1 } r && r.Date < new DateTime(2019, 5, 10) ? req : Math.Max(0, req / 4);
        if (double.IsNaN(s.SpinTextFadeAt) && s.SpinsCounted >= spinText) s.SpinTextFadeAt = s.SimTime;
        if ((int)s.Rotation <= s.SpinsCounted) return;
        s.SpinsCounted++;
        if (double.IsNaN(s.SpinTextFadeAt) && s.SpinsCounted >= spinText) s.SpinTextFadeAt = s.SimTime;
        if (s.SpinsCounted == req) s.ClearAt = s.SimTime;
        if (s.SpinsCounted > req + 3 && (s.SpinsCounted - (req + 3)) % 2 == 0)
        {
            s.BonusAt = s.SimTime;
            s.Bonus = 1000 * (s.SpinsCounted - (req + 3)) / 2;
            score += 1100;
            V2Bonus(500);
            health.Apply(HpEvent.SpinnerBonus);
            if (!cinema) skin.Sound("spinnerbonus")?.Play(BeatmapSamples.EffectVolume * PointVolume / 100f);
        }
        else if (s.SpinsCounted > 1 && s.SpinsCounted % 2 == 0)
        {
            score += 100;
            V2Bonus(100);
            health.Apply(HpEvent.SpinnerSpin);
        }
        else if (s.SpinsCounted > 1) health.Apply(HpEvent.SpinnerSpin);
    }

    void UpdateSpinner(int i, double dtMs)
    {
        var o = objs[i];
        var s = st[i];
        if (time < o.EndTime) return;
        int req = SpinsRequired(o);
        int spins = s.SpinsCounted;
        // stable's grading (current version): miss below a quarter, 300 above the requirement, 100 within 2.
        int result = req == 0 ? 300 : spins < Math.Max(0, req / 4) ? 0 : spins > req ? 300 : spins > req - 2 ? 100 : 50;
        if (result > 0) PlayHitsound(o.HitSound, o.NormalSet, o.AdditionSet, o.SampleIndex, o.Volume, o.EndTime, ahead: 0);
        Final(i, result, new Vector2(256, 192));
    }

    // stable: value + (int)(max(0, combo - 1) * ((value / 25) * (difficulty multiplier * mod multiplier))), integer value / 25
    void ScoreHit(int value) => score += value + (int)(Math.Max(0, combo - 1) * ((double)(value / 25) * (diffMul * modMul)));

    void BreakCombo()
    {
        if (VerifyLog) Console.Error.WriteLine($"  combo break at {time:0} after {combo}");
        if (combo > 20 && !mods.HasFlag(Mods.Relax) && !mods.HasFlag(Mods.Autopilot)) skin.Sound("combobreak")?.Play(BeatmapSamples.EffectVolume); // stable: over 20, not with Relax / Autopilot
        combo = 0;
        comboBroke = true;
        if (mods.HasFlag(Mods.SuddenDeath)) Fail();
    }

    void Count(int result)
    {
        switch (result)
        {
            case 300: n300++; break;
            case 100: n100++; break;
            case 50: n50++; break;
            default: nMiss++; break;
        }
    }

    // stable's Nightcore beat over the music, counted in half beats from the current red timing point: every 4 bars a
    // kick and a finish (none at a point's start when it omits its first bar line), else a kick on half beat 0 and a
    // clap on 2 of each meter-long group, and a hat on the others only when the map's slider tick rate is even.
    int ncHalfBeat = -1;
    void NightcoreBeat()
    {
        if (!mods.HasFlag(Mods.Nightcore)) return;
        var tp = map.UninheritedAt(time + 5);
        if (time < tp.Time) return;
        int n = (int)((time - tp.Time) * 2 / tp.BeatLength), inBar = n % tp.Meter;
        if (n == ncHalfBeat) return;
        ncHalfBeat = n;
        float vol = BeatmapSamples.EffectVolume;
        if (n % (8 * tp.Meter) == 0)
        {
            skin.Sound("nightcore-kick")?.Play(vol);
            if (n > 0 || !tp.OmitFirstBarLine) skin.Sound("nightcore-finish")?.Play(vol);
        }
        else if (inBar % 4 == 0) skin.Sound("nightcore-kick")?.Play(vol);
        else if (inBar % 4 == 2) skin.Sound("nightcore-clap")?.Play(vol);
        else if (Math.Clamp(map.SliderTickRate, 0.5, 8) % 2 == 0) skin.Sound("nightcore-hat")?.Play(vol);
    }

    // stable pans hitsounds by where they happen: (x / 512 - 0.5) x 0.8 (a slider's by its ball; spinners sit centred)
    static float Pan(Vector2 p) => (p.X / 512f - 0.5f) * 0.8f;

    // stable: hitsounds follow DT / HT (speed and pitch) only on maps that ask for it
    float SamplePitch => map.SamplesMatchPlaybackRate ? (float)rate : 1;

    // stable's slider edges: the edge's sound and sets (the head's normal set of 0 falls back to the slider's own before
    // v14), at the timing point's sample index and volume (the slider's own hitsample volume and index don't count)
    void PlayEdge(HitObject o, int edge)
    {
        int sound = edge < o.EdgeSounds.Length ? o.EdgeSounds[edge] : o.HitSound;
        var (normal, addition) = edge < o.EdgeSets.Length ? o.EdgeSets[edge] : (o.NormalSet, o.AdditionSet);
        if (edge == 0 && normal == 0 && map.FormatVersion < 14) normal = o.NormalSet;
        double at = o.Time + edge * o.SpanDuration;
        PlayHitsound(sound, normal, addition, 0, 0, at, pan: Pan(BallOf(o, at)));
    }

    // A custom sample file found in the map's folder plays instead of the whole set (stable: none of the normal,
    // whistle, finish or clap); storyboard triggers still see the set. Otherwise stable's mix: at least 8% volume, the
    // normal at 0.8 of it, whistle and clap at 0.85, finish at full.
    void PlayHitsound(int bits, int normalSet, int additionSet, int index, int volume, double at, string file = "", double ahead = 5, float pan = 0)
    {
        var tp = map.PointAt(at + ahead); // stable samples the timing point slightly ahead (a spinner's end: right at it)
        int set = normalSet > 0 ? normalSet : tp.SampleSet;
        int add = additionSet > 0 ? additionSet : set;
        int idx = index > 0 ? index : tp.SampleIndex;
        int vol = volume > 0 ? volume : tp.Volume;
        storyboard?.FireHitSound(time, set, add, bits, idx);
        if (samples.File(file) is { } custom) { custom.Play(BeatmapSamples.EffectVolume * vol / 100f, SamplePitch, pan); return; }
        vol = Math.Max(8, vol);
        // the normal sound: with no additions, when the map asks for it, or with the skin's LayeredHitSounds (on by default)
        if (bits == 0 || (bits & 1) != 0 || skin.LayeredHitSounds) samples.Play(set, "hitnormal", idx, (int)(vol * 0.8), SamplePitch, pan);
        if ((bits & 2) != 0) samples.Play(add, "hitwhistle", idx, (int)(vol * 0.85), SamplePitch, pan);
        if ((bits & 4) != 0) samples.Play(add, "hitfinish", idx, vol, SamplePitch, pan);
        if ((bits & 8) != 0) samples.Play(add, "hitclap", idx, (int)(vol * 0.85), SamplePitch, pan);
    }

    // ---------------------------------------------------------------- drawing

    // Loaded on first need: at start, or when the dim is lowered from 100% mid-play (Visual Settings).
    bool bgTried;

    // The map's background video (stable's Video option, on by default; not while watching the result screen etc.).
    Video? video;

    void LoadBackground()
    {
        bgTried = true;
        var videoPath = Path.Combine(map.Folder, map.VideoFile);
        if (map.VideoFile.Length > 0 && !VirtualClock && Config.GetBool("Video", true) && File.Exists(videoPath)) video = new Video(videoPath);
        var bgPath = Path.Combine(map.Folder, map.Background);
        // A storyboard that draws the background itself replaces it, like stable.
        if (map.Background.Length == 0 || !File.Exists(bgPath) || storyboard?.UsesFile(map.Background) == true) return;
        bg = LoadTexture(bgPath);
        if (bg.Id != 0) SetTextureFilter(bg, TextureFilter.Bilinear);
    }

    public override void Draw()
    {
        int w = Game.W, h = Game.H;
        if (bg.Id == 0 && !bgTried) LoadBackground(); // even at 100% dim: it fades in from the previous screen
        // stable: once the video has started it replaces the background image, fitted inside the screen
        // the decoder keeps following while the toggle is off, so turning it back on shows the right frame at once
        if (video != null && time >= map.VideoOffset && !video.Failed && video.Frame(time - map.VideoOffset) is { } vt && Toggles.Video)
        {
            float s = Math.Min(w / (float)vt.Width, h / (float)vt.Height);
            DrawTexturePro(vt, new Rectangle(0, 0, vt.Width, vt.Height),
                new Rectangle((w - vt.Width * s) / 2, (h - vt.Height * s) / 2, vt.Width * s, vt.Height * s),
                Vector2.Zero, 0, ColorAlpha(Color.White, 1 - bgDim / 100f));
        }
        else if (bg.Id != 0)
        {
            float s = Math.Max(w / (float)bg.Width, h / (float)bg.Height);
            float dim = 1 - bgDim / 100f;
            DrawTexturePro(bg, new Rectangle(0, 0, bg.Width, bg.Height),
                new Rectangle((w - bg.Width * s) / 2, (h - bg.Height * s) / 2, bg.Width * s, bg.Height * s),
                Vector2.Zero, 0, ColorAlpha(Color.White, dim));
        }

        visible.Clear();
        for (int i = firstActive; mode == 0 && i < objs.Count && objs[i].Time - map.Preempt <= time; i++) visible.Add(i);
        if (bodyMeshes.Count > 0 || bodyCache.Count > 0) SweepBodyMeshes();

        float sbDim = 1 - bgDim / 100f;
        bool showStoryboard = storyboard != null && Toggles.Storyboard && Config.GetBool("ShowStoryboard", true);
        if (showStoryboard) storyboard!.Draw(time, w, h, sbDim, overlay: false);
        if (!cinema) { if (mode == 1) TaikoDraw(w, h); else if (mode == 2) CatchDraw(w, h); else if (mode == 3) ManiaDraw(w, h); else DrawPlayfield(w, h); }
        if (showStoryboard) storyboard!.Draw(time, w, h, sbDim, overlay: true);
        if (!cinema)
        {
            DrawBreakOverlays(w, h);
            if (mods.HasFlag(Mods.Flashlight) && !finished) DrawFlashlight(w, h);
            DrawHud(w, h);
        }
        DrawOverlays(w, h);
        if (back.DialogOpen) back.HostDrawDialog();
    }

    void DrawPlayfield(int w, int h)
    {
        UpdateComboBursts();
        DrawComboBursts(w, h);
        DrawLightings();
        DrawFollowPoints();
        // stable's depths: approach circles at the bottom (a later object's ring goes under earlier objects and under
        // judgements, measured), then particles and particle-style judgements, then spinners (each under its own
        // judgement), then the hit objects (earlier ones on top), then misses and plain judgements, then the flashes.
        for (int k = visible.Count - 1; k >= 0; k--)
        {
            if (Falling) PushFall(visible[k]);
            DrawApproach(visible[k]);
            if (Falling) Rlgl.PopMatrix();
        }
        DrawPopups(0);
        DrawPopups(1);
        for (int k = visible.Count - 1; k >= 0; k--)
        {
            if (objs[visible[k]].Type != ObjType.Spinner) continue;
            if (Falling) PushFall(visible[k]);
            DrawSpinner(visible[k]);
            if (Falling) Rlgl.PopMatrix();
        }
        DrawPopups(2);

        // Earlier objects on top: draw later ones first.
        for (int k = visible.Count - 1; k >= 0; k--)
        {
            int i = visible[k];
            if (Falling) PushFall(i);
            switch (objs[i].Type)
            {
                case ObjType.Circle: DrawHitCircle(i); break;
                case ObjType.Slider: DrawSlider(i); break;
            }
            if (Falling) Rlgl.PopMatrix();
        }
        if (!Falling)
        {
            UpdateKiaiFlashes();
            DrawKiaiFlashes();
        }
        DrawPopups(3);
        DrawPopups(4);
        DrawPointPops(); // stable: depth 1, over everything on the playfield
    }

    void DrawOverlays(int w, int h)
    {
        DrawEpilepsyWarning(w, h);
        if (retryHold > 0) DrawRectangle(0, 0, w, h, ColorAlpha(Color.Black, (float)(retryHold / 100)));
        if (paused && resumePending) DrawResume();
        else if (paused) DrawPauseMenu(fail: false);
        if (failed) DrawPauseMenu(fail: true);
        if (LiveFail) // stable: bold 16 with a shadow at (10, 10), fading in with the menu
        {
            float u = h / 480f, a = (float)Math.Clamp((Game.NowMs - menuShownAt) / 300, 0, 1) * MenuAlpha;
            string[] hint = ["Press F1 to view replay", "Press F2 to save replay"];
            for (int i = 0; i < hint.Length; i++)
            {
                Game.TextBold(hint[i], 12 * u + 1, (10 + 16 * i) * u + 1, 16.6f * u, ColorAlpha(Color.Black, 0.5f * a));
                Game.TextBold(hint[i], 12 * u, (10 + 16 * i) * u, 16.6f * u, ColorAlpha(Color.White, a)); // measured against stable
            }
        }
        if (!double.IsNaN(LeftSongSelectAt)) back.DrawLeaving(Game.NowMs - LeftSongSelectAt);
        DrawVisualSettings(w, h);
        if (cinema) return;
        UpdateBreakStars(auto && playback == null ? autoKeys : inputKeys);
        DrawBreakStars();
        if (mode != 0 && !paused && !failed) return; // (the other modes play without the cursor)
        var cursorAt = auto || autopilot || playback != null ? ToScreen(cursor) : Input.GetMousePosition();
        UpdateSmoke(smokeHeld && !paused && !failed, cursorAt);
        DrawSmoke();
        SongSelect.CursorPlayScale = Config.GetBool("AutomaticCursorSizing", false) ? 1 - 0.7f * (float)(map.CS - 4) / 5 : 1;
        SongSelect.CursorKeys = auto && playback == null ? autoKeys : inputKeys;
        SongSelect.DrawCursorAt(cursorAt);
        SongSelect.CursorPlayScale = 1;
        SongSelect.CursorKeys = null;
    }

    // ---- stable's cursor smoke: while the smoke key is held (in replays its frame bit 16), every 60Hz tick lays
    // cursor-smoke sprites from the last one towards the cursor, a quarter of the sprite's width (at scale 1) apart, each
    // added on at half size, alpha 0.6 fading out over 4s, at a random angle turning up to 0.25 rad more over 500ms
    // (Out). Letting go (or closing a loop: over 50 in the stroke and back within 10px of its start) ends the stroke:
    // its sprites fade from full alpha over 8s (In), 2ms apart.
    public const byte SmokeBit = 16;
    KeyboardKey keySmoke;
    bool smokeHeld;
    sealed class SmokePuff { public Vector2 Pos; public double Born, Release = double.NaN; public float Rot0, Rot1; }
    readonly List<SmokePuff> smoke = [], smokeStroke = [];
    Vector2? smokeLast;
    double smokeTick = double.NaN;

    void ReleaseSmoke()
    {
        double now = Game.NowMs;
        for (int i = 0; i < smokeStroke.Count; i++) smokeStroke[i].Release = now + 2 * i;
        smokeStroke.Clear();
    }

    void UpdateSmoke(bool held, Vector2 at)
    {
        double now = Game.NowMs;
        if (double.IsNaN(smokeTick)) smokeTick = now;
        var tex = skin.Tex("cursor-smoke");
        for (; now - smokeTick >= 1000 / 60.0; smokeTick += 1000 / 60.0)
        {
            if (!held || tex is null) { ReleaseSmoke(); smokeLast = null; continue; }
            float gap = Math.Max(2, tex.Value.Width * 0.625f * Game.H / 480f / 4);
            if (smokeLast is not { } last || last != at)
            {
                float dist = smokeLast is { } l ? Vector2.Distance(l, at) : 0;
                int n = Math.Max(1, (int)(dist / gap));
                var from = smokeLast is { } p ? p + Vector2.Normalize(at - p) * Math.Min(dist, gap) : at;
                for (int i = 0; i < n; i++)
                {
                    float r = Random.Shared.NextSingle() * 10;
                    var puff = new SmokePuff { Pos = Vector2.Lerp(from, at, (float)i / n), Born = now, Rot0 = r, Rot1 = r + Random.Shared.NextSingle() * 0.5f - 0.25f };
                    smoke.Add(puff);
                    smokeStroke.Add(puff);
                    smokeLast = puff.Pos;
                }
            }
            if (smokeStroke.Count > 50 && Vector2.Distance(smokeLast!.Value, smokeStroke[0].Pos) < 10) ReleaseSmoke();
        }
        smoke.RemoveAll(s => double.IsNaN(s.Release) ? now - s.Born > 4000 : now - s.Release > 8000);
    }

    void DrawSmoke()
    {
        if (smoke.Count == 0 || skin.Tex("cursor-smoke") is not { } tex) return;
        double now = Game.NowMs;
        float sd = Game.H / 768f;
        BeginBlendMode(BlendMode.Additive);
        foreach (var s in smoke)
        {
            float a = double.IsNaN(s.Release) ? 0.6f * (1 - (float)((now - s.Born) / 4000))
                : now < s.Release ? 1 : 1 - (float)Easing.Apply(2, (now - s.Release) / 8000);
            if (a <= 0) continue;
            float rot = s.Rot0 + (s.Rot1 - s.Rot0) * (float)Easing.Apply(1, (now - s.Born) / 500);
            tex.Draw(s.Pos, sd * 0.5f, ColorAlpha(Color.White, a * hudFade), rot * 180 / MathF.PI);
        }
        EndBlendMode();
    }

    // stable's star2 effects, in 640x480 units moved by a little physics step every 60Hz tick (+gravity to the speed,
    // speed x 1/60 to the position); each fades out, scales from 1 and turns between two random angles over a random
    // 300-1000ms (scale and turn eased Out). Cursor stars (StarBreakAdditive, added on): every third tick of a break, and
    // every tick of sliders and spinners under Relax/Autopilot or in kiai with a key held; they leave with 4x the cursor's
    // last-tick movement (held keys kick break stars sideways), gravity 8, scale to 0..2, linear fade. Kiai fountain
    // (white): when kiai starts, 50 pairs from the bottom corners 10ms apart in song time, thrown up and inwards, gravity
    // 12.8, scale to 1..2.8, fade eased Out too; both stars of a pair share their life, scale and turn.
    sealed class Star
    {
        public Vector2 Pos, Vel;
        public double Born, Life; // real ms for cursor stars, song ms for fountain stars
        public float Scale, Rot0, Rot1, Gravity = 8;
        public bool Fountain;
    }
    readonly List<Star> stars = [];
    double starTickAt = double.NaN;
    int starTicks;
    Vector2 starCursorWas;
    bool kiaiWas;

    double StarAge(Star s) => s.Fountain ? time - s.Born : Game.NowMs - s.Born;

    void UpdateBreakStars(byte keys)
    {
        double now = Game.NowMs;
        if (double.IsNaN(starTickAt)) starTickAt = now;
        float u = Game.H / 480f;
        var at = ToScreen(cursor) / u;
        var rnd = Random.Shared;
        bool kiai = KiaiAt(time);
        if (kiai && !kiaiWas && !paused && !failed)
            for (int i = 0; i < 50; i++)
            {
                double life = rnd.Next(300, 1000);
                float scale = 1 + rnd.NextSingle() * 1.8f, rot0 = rnd.NextSingle() * 4 - 2, rot1 = rnd.NextSingle() * 4 - 2;
                foreach (bool right in (bool[])[false, true])
                    stars.Add(new Star
                    {
                        Fountain = true, Born = time + i * 10, Life = life, Gravity = 8 * 1.6f,
                        Pos = new Vector2(right ? Game.W / u - 30 : 30, 490),
                        Vel = new Vector2(rnd.Next(300, 450) * (right ? -1 : 1), rnd.Next(-800, -700)),
                        Scale = scale, Rot0 = rot0, Rot1 = rot1,
                    });
            }
        kiaiWas = kiai;
        for (; now - starTickAt >= 1000 / 60.0; starTickAt += 1000 / 60.0)
        {
            var moved = at - starCursorWas;
            starCursorWas = at;
            foreach (var s in stars)
            {
                if (StarAge(s) < 0) continue; // a fountain star waits for its start
                s.Vel.Y += s.Gravity;
                s.Pos += s.Vel * (1 / 60f);
            }
            if (paused || failed) continue;
            bool rest = Resting(time), spray = !rest && (relax || autopilot || keys != 0 && kiai) && SliderOrSpinnerActive();
            if (!spray && (!rest || starTicks++ <= 1)) continue;
            if (!spray) starTicks = 0;
            var vel = moved * 4;
            bool left = !spray && (keys & 5) != 0, rightKey = !spray && (keys & 10) != 0;
            if (left && rightKey) vel += new Vector2(rnd.Next(-460, 460), rnd.Next(-160, 160));
            else if (left) vel += new Vector2(rnd.Next(-460, 0), rnd.Next(-40, 40));
            else if (rightKey) vel += new Vector2(rnd.Next(0, 460), rnd.Next(-40, 40));
            stars.Add(new Star { Pos = at, Vel = vel, Born = starTickAt + 1000 / 60.0, Life = rnd.Next(300, 1000), Scale = rnd.NextSingle() * 2,
                Rot0 = rnd.NextSingle() * 4 - 2, Rot1 = rnd.NextSingle() * 4 - 2 });
        }
        stars.RemoveAll(s => StarAge(s) >= s.Life);
    }

    // stable's kiai pulse: on every beat in kiai each circle / slider head on screen and not yet hit gets an added-on
    // copy of its body in its combo colour brightened (by max(25, 300 - R - G - B)), fading 23% -> 0 (measured) over 3/4 of a beat
    // (cut at the object's end).
    readonly List<(int Obj, double At, double Len)> kiaiFlashes = [];
    long kiaiBeatSeen = long.MinValue;

    void UpdateKiaiFlashes()
    {
        var tp = map.UninheritedAt(time);
        if (tp.BeatLength <= 0) return;
        long beat = (long)Math.Floor((time - tp.Time) / tp.BeatLength);
        long key = beat ^ ((long)tp.Time << 20);
        if (key == kiaiBeatSeen) return;
        kiaiBeatSeen = key;
        if (!KiaiAt(time) || paused) return;
        double len = tp.BeatLength * 0.75;
        for (int i = firstActive; i < objs.Count && objs[i].Time - map.Preempt <= time; i++)
        {
            var o = objs[i];
            if (o.Type == ObjType.Spinner || (o.Type == ObjType.Circle ? st[i].Done : st[i].HeadDone)) continue;
            double l = Math.Min(len, o.EndTime - time);
            if (l >= 0) kiaiFlashes.Add((i, time, l));
        }
    }

    void DrawKiaiFlashes()
    {
        kiaiFlashes.RemoveAll(f => time - f.At > f.Len);
        if (kiaiFlashes.Count == 0) return;
        BeginBlendMode(BlendMode.Additive);
        foreach (var (i, at, len) in kiaiFlashes)
        {
            var o = objs[i];
            double age = time - at;
            var p = ToScreen(PosOf(o));
            var c = ColourOf(o);
            int add = Math.Max(25, 300 - c.R - c.G - c.B);
            var bright = new Color(Math.Min(255, c.R + add), Math.Min(255, c.G + add), Math.Min(255, c.B + add), 255);
            if (age > len || (o.Type == ObjType.Circle ? st[i].Done : st[i].HeadDone)) continue;
            float fa = 0.23f * (float)(1 - age / Math.Max(1, len)) * FadeInAlpha(o);
            float cs = CircleScale;
            var body = (o.Type == ObjType.Slider ? skin.Tex("sliderstartcircle") : null) ?? skin.Tex("hitcircle");
            body?.Draw(p, cs, ColorAlpha(bright, fa));
        }
        EndBlendMode();
    }

    // stable's combo bursts (the Combo bursts option): at combo 30, 60 and every 50 from 100 (not during a spinner), the
    // (taiko's at 50 and every 100: its mascot cheers and, with the option, a flower pops up by the drum instead;
    // catch's images are comboburst-fruits (skins from 2.3); mania's come every 100 past the last one's hundred)
    // skin's next comboburst image (a random one with ComboBurstRandom) slides in at the bottom of a random side over
    // 700ms (Out; flipped on the right) and fades out from 200 to 1200ms (In). Its texels are 0.625 units.
    Sprite[]? burstFrames;
    int burstIndex, burstLastCombo, burstSeenCombo = -1;
    readonly List<(Sprite Tex, bool Right, double At)> bursts = [];

    // stable's combo burst sounds: with the skin's CustomComboBurstSounds, "comboburst-N" when the combo reaches the
    // list's Nth entry; otherwise at each burst milestone the skin's "comboburst-0", "-1" ... in turn (back to -0 after
    // the last), or "comboburst" when it has none. The images follow the Combo bursts option.
    int burstSound;
    void PlayBurstSound()
    {
        if (skin.Sound($"comboburst-{burstSound}") is { } s) { s.Play(BeatmapSamples.EffectVolume); burstSound++; }
        else if (burstSound > 0) { skin.Sound("comboburst-0")?.Play(BeatmapSamples.EffectVolume); burstSound = 1; }
        else skin.Sound("comboburst")?.Play(BeatmapSamples.EffectVolume);
    }

    void UpdateComboBursts()
    {
        if (combo == burstSeenCombo) return;
        burstSeenCombo = combo;
        if (combo == 0) { burstLastCombo = 0; mBurstBase = 0; }
        if (combo > 0 && skin.ComboSoundBursts.BinarySearch(combo) is int custom and >= 0) skin.Sound($"comboburst-{custom}")?.Play(BeatmapSamples.EffectVolume);
        bool milestone = mode switch
        {
            1 => combo == 50 || combo > 99 && combo % 100 == 0,
            3 => combo - mBurstBase >= 100,
            _ => combo is 30 or 60 || combo > 99 && combo % 50 == 0,
        };
        if (!milestone || burstLastCombo >= combo || SpinnerActive()) return;
        burstLastCombo = combo;
        if (skin.ComboSoundBursts.Count == 0) PlayBurstSound();
        // stable: unless the dim is at 100%, a burst also lifts the background toward its lightest dim (80% of the way
        // with burst images on, 40% without; it eases back) and flashes the play effects (Bloom)
        if (Config.GetInt("DimLevel", 80) != 100)
        {
            bgDim -= MathF.Ceiling((bgDim - LightestDim) * (Config.GetBool("ComboBurst", false) ? 0.8f : 0.4f));
            bloomFlash = 1;
        }
        if (mode == 1) { TaikoBurst(); return; }
        if (mode == 3) { ManiaBurst(); return; }
        if (!Config.GetBool("ComboBurst", false)) return;
        if (burstFrames == null)
        {
            List<Sprite> Frames(string prefix)
            {
                var list = new List<Sprite>();
                while (skin.Tex($"{prefix}-{list.Count}") is { } f) list.Add(f);
                if (list.Count == 0 && skin.Tex(prefix) is { } one) list.Add(one);
                return list;
            }
            var frames = mode == 2 && skin.Version >= 2.3 ? Frames("comboburst-fruits") : new List<Sprite>();
            burstFrames = (frames.Count > 0 ? frames : Frames("comboburst")).ToArray();
        }
        if (burstFrames.Length == 0) return;
        if (skin.ComboBurstRandom) burstIndex = Random.Shared.Next(burstFrames.Length);
        bursts.Add((burstFrames[burstIndex], Random.Shared.NextDouble() > 0.5, Game.NowMs));
        burstIndex = (burstIndex + 1) % burstFrames.Length;
    }

    bool SpinnerActive()
    {
        for (int i = firstActive; i < objs.Count && objs[i].Time <= time; i++)
            if (objs[i].Type == ObjType.Spinner && time <= objs[i].EndTime) return true;
        return false;
    }

    void DrawComboBursts(int w, int h)
    {
        bursts.RemoveAll(b => Game.NowMs - b.At > 1200);
        float sd = h / 768f;
        foreach (var (tex, right, at) in bursts)
        {
            double age = Game.NowMs - at;
            float slide = (float)Easing.Apply(1, Math.Clamp(age / 700, 0, 1)), bw = tex.Width * sd, bh = tex.Height * sd;
            float a = 1 - (float)Easing.Apply(2, Math.Clamp((age - 200) / 1000, 0, 1));
            float x = right ? w - bw * slide : -bw + bw * slide; // left edge
            DrawTexturePro(tex.Tex, new Rectangle(0, 0, right ? -tex.Tex.Width : tex.Tex.Width, tex.Tex.Height),
                new Rectangle(x, h - bh, bw, bh), Vector2.Zero, 0, ColorAlpha(Color.White, a * playfieldAlpha));
        }
    }

    bool KiaiAt(double t)
    {
        bool kiai = false;
        foreach (var tp in map.TimingPoints) { if (tp.Time > t) break; kiai = tp.Kiai; }
        return kiai;
    }

    bool SliderOrSpinnerActive()
    {
        for (int i = firstActive; i < objs.Count && objs[i].Time <= time; i++)
            if (objs[i].Type != ObjType.Circle && time <= objs[i].EndTime) return true;
        return false;
    }

    void DrawBreakStars()
    {
        if (stars.Count == 0 || skin.Tex("star2") is not { } tex) return;
        float u = Game.H / 480f, sd = Game.H / 768f;
        foreach (bool fountain in (bool[])[true, false])
        {
            if (!fountain) BeginBlendMode(BlendMode.Additive);
            foreach (var s in stars)
            {
                double age = StarAge(s);
                if (s.Fountain != fountain || age < 0) continue;
                float f = (float)Math.Clamp(age / s.Life, 0, 1), e = (float)Easing.Apply(1, f);
                var tint = fountain ? Color.White : skin.StarBreakAdditive;
                tex.Draw(s.Pos * u, sd * (1 + (s.Scale - 1) * e), ColorAlpha(tint, (1 - (fountain ? e : f)) * (fountain ? playfieldAlpha : failAlpha)), (s.Rot0 + (s.Rot1 - s.Rot0) * e) * 180 / MathF.PI);
            }
            if (!fountain) EndBlendMode();
        }
    }

    float CircleScale => map.Radius / 64f * scale;
    Color ColourOf(HitObject o) => colours[o.ColourIndex % colours.Length];

    float FadeInAlpha(HitObject o)
    {
        double appear = o.Time - map.Preempt;
        if (!mods.HasFlag(Mods.Hidden)) return (float)Math.Clamp((time - appear) / map.FadeIn, 0, 1);
        // stable HD: fade in over 40% of the approach, then out over the next 30%.
        double fadeIn = Math.Clamp((time - appear) / (map.Preempt * 0.4), 0, 1);
        double fadeOut = Math.Clamp(1 - (time - appear - map.Preempt * 0.4) / (map.Preempt * 0.3), 0, 1);
        return (float)Math.Min(fadeIn, fadeOut);
    }

    float SliderBodyAlpha(HitObject o)
    {
        double appear = o.Time - map.Preempt;
        if (!mods.HasFlag(Mods.Hidden))
        {
            float a = (float)Math.Clamp((time - appear) / map.FadeIn, 0, 1) * playfieldAlpha;
            return time > o.EndTime ? a * (float)Math.Max(0, 1 - (time - o.EndTime) / 240) : a;
        }
        // HD: body fades out over the rest of the slider instead of staying solid.
        double outStart = appear + map.Preempt * 0.4;
        double fadeIn = Math.Clamp((time - appear) / (map.Preempt * 0.4), 0, 1);
        double fadeOut = Math.Clamp(1 - (time - outStart) / Math.Max(1, o.EndTime - outStart), 0, 1);
        return (float)Math.Min(fadeIn, fadeOut) * playfieldAlpha;
    }

    // stable draws objects that can't be hit yet (over 400ms ahead) at 195/255 brightness, brightening over the next
    // 100ms (measured); combo numbers stay white.
    float Lit(HitObject o) => 0.765f + 0.235f * (float)Math.Clamp((time - (o.Time - 400)) / 100, 0, 1);

    static Color Shade(Color c, float f) => new((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f), c.A);

    void DrawCircleSprite(Vector2 osuPos, Color colour, float alpha, float expand, int number, bool sliderHead, float lit = 1)
    {
        var p = ToScreen(osuPos);
        float cs = CircleScale * expand;
        var body = (sliderHead ? skin.Tex("sliderstartcircle") : null) ?? skin.Tex("hitcircle");
        // stable takes the overlay named after the body it drew (sliderstartcircle -> sliderstartcircleoverlay, or none)
        var overlay = sliderHead && skin.Tex("sliderstartcircle") != null ? skin.Tex("sliderstartcircleoverlay") : skin.Tex("hitcircleoverlay");
        float hitAge = expand > 1.001f ? (1 - alpha) * 240 : -1; // hits: alpha is 1 - age/240 (HitAnim)
        alpha *= playfieldAlpha;
        if (body is { } b) b.Draw(p, cs, ColorAlpha(Shade(colour, lit), alpha));
        else
        {
            DrawCircleV(p, map.Radius * scale * expand, ColorAlpha(Color.White, alpha));
            DrawCircleV(p, map.Radius * scale * expand * 0.88f, ColorAlpha(colour, alpha));
        }
        // the overlay goes over the number unless the beatmap or skin (HitCircleOverlayAboveNumber: 0) puts it under
        bool overlayAbove = map.OverlayPosition == 0 ? skin.OverlayAboveNumber : map.OverlayPosition == 2;
        if (!overlayAbove) overlay?.Draw(p, cs, ColorAlpha(Shade(Color.White, lit), alpha));
        // stable on a hit: the number fades out over 60ms (v2+ skins) or grows 0.8 -> 1.12 with the circle (older).
        if (number > 0 && hitAge < 0) DrawNumber(number, p, CircleScale * 0.8f, alpha);
        else if (number > 0 && skin.Version > 1) DrawNumber(number, p, CircleScale * 0.8f, playfieldAlpha * Math.Max(0, 1 - hitAge / 60));
        else if (number > 0) DrawNumber(number, p, CircleScale * (0.8f + 0.32f * (float)Easing.Apply(1, hitAge / 240)), alpha);
        if (overlayAbove) overlay?.Draw(p, cs, ColorAlpha(Shade(Color.White, lit), alpha));
    }

    readonly NumberText comboNumbers = new(v => v.ToString());

    void DrawNumber(int n, Vector2 centre, float s, float alpha)
    {
        string digits = comboNumbers.Of(n); // cached text and glyphs: this runs for every visible circle every frame
        float total = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            if (skin.Glyph(skin.HitCirclePrefix, digits[i]) is not { } sp)
            {
                float size = map.Radius * scale;
                Game.Text(digits, centre.X - Game.TextWidth(digits, size) / 2, centre.Y - size / 2, size, ColorAlpha(Color.White, alpha));
                return;
            }
            total += sp.Width * s - (i > 0 ? skin.HitCircleOverlap * s : 0);
        }
        float x = centre.X - total / 2;
        for (int i = 0; i < digits.Length; i++)
        {
            var sp = skin.Glyph(skin.HitCirclePrefix, digits[i])!.Value;
            if (i > 0) x -= skin.HitCircleOverlap * s;
            sp.Draw(new Vector2(x + sp.Width * s / 2, centre.Y), s, ColorAlpha(Color.White, alpha));
            x += sp.Width * s;
        }
    }

    // Returns (alpha, expand) for a circle/head given its judgement state; alpha 0 = gone.
    (float Alpha, float Expand) HitAnim(HitObject o, bool done, bool hit, double judgeTime)
    {
        if (!done) return (FadeInAlpha(o), 1);
        double t = time - judgeTime;
        if (hit && mods.HasFlag(Mods.Hidden)) return (FadeInAlpha(o), 1); // HD: no burst, the Hidden fade just carries on (read from stable)
        if (hit) return ((float)Math.Max(0, 1 - t / 240), 1 + 0.4f * (float)Easing.Apply(1, t / 240)); // stable: fade linear, grow Out
        return ((float)Math.Max(0, 1 - t / 100) * FadeInAlpha(o), 1);
    }

    void DrawHitCircle(int i)
    {
        var o = objs[i];
        var s = st[i];
        var (alpha, expand) = HitAnim(o, s.Done, s.Result > 0, s.JudgeTime);
        if (alpha > 0) DrawCircleSprite(PosOf(o), ColourOf(o), alpha, expand, o.ComboNumber, false, Lit(o));
    }

    void DrawSlider(int i)
    {
        var o = objs[i];
        var s = st[i];
        float alpha = SliderBodyAlpha(o);
        var colour = ColourOf(o);
        var track = skin.SliderTrackOverride ?? colour; // stable shades the combo colour itself (see SliderGradient)
        float lit = Lit(o);
        // Snaking: the body grows from the head over the first third of the approach (stable option).
        float snake = snaking ? (float)Math.Clamp((time - (o.Time - map.Preempt)) / (map.Preempt / 3), 0, 1) : 1;
        int count = Math.Max(1, (int)Math.Ceiling(bodies[i].Length * snake));
        if (alpha > 0)
        {
            // Rendered into a texture (the body is translucent and self-overlapping), then faded as a whole.
            // rlgl would apply a pushed fall transform inside the texture pass too, so it is lifted for it.
            // Only the body's own box is cleared and composited (a full-screen clear and blend per slider was most of a
            // dense map's frame); the body can't reach outside its path's bounds plus the radius.
            var tex = sliderRt.Texture;
            float pad = map.Radius * scale + 2;
            var (lo, hi) = bodyBounds[i];
            Vector2 a = ToScreen(lo) - new Vector2(pad), b = ToScreen(hi) + new Vector2(pad);
            int x0 = Math.Max(0, (int)MathF.Floor(a.X)), y0 = Math.Max(0, (int)MathF.Floor(a.Y));
            int x1 = Math.Min(tex.Width, (int)MathF.Ceiling(b.X)), y1 = Math.Min(tex.Height, (int)MathF.Ceiling(b.Y));
            var tint = ColorAlpha(Shade(Color.White, lit), alpha);
            // (render textures are stored bottom-up: the used top-left part is the last rows, drawn flipped)
            if (count >= bodies[i].Length && bodyCache.TryGetValue(i, out var cached))
                DrawTextureRec(cached.Rt.Texture, new Rectangle(0, cached.Rt.Texture.Height - cached.H, cached.W, -cached.H), new Vector2(x0, y0), tint);
            else if (x1 > x0 && y1 > y0)
            {
                if (Falling) Rlgl.PopMatrix();
                if (count >= bodies[i].Length)
                {
                    // a finished body doesn't change: drawn once into a texture of its own (its box, from a pool) and
                    // reused from now on; drawn directly unless it snaked in (then its mesh is there already)
                    int bw = x1 - x0, bh = y1 - y0;
                    var rt = TakeBodyTexture(bw, bh);
                    BeginTextureMode(rt);
                    ClearBackground(Color.Blank);
                    Rlgl.PushMatrix();
                    Rlgl.Translatef(-x0, -y0, 0);
                    var gradient = SliderGradient(skin.SliderBorder, track);
                    if (bodyMeshes.ContainsKey(i)) DrawBody(i, count, gradient);
                    else DrawBodyOnce(bodies[i], gradient);
                    Rlgl.PopMatrix();
                    Game.EndTexture();
                    bodyCache[i] = (rt, bw, bh);
                    if (Falling) PushFall(i);
                    DrawTextureRec(rt.Texture, new Rectangle(0, rt.Texture.Height - bh, bw, -bh), new Vector2(x0, y0), tint);
                }
                else
                {
                    BeginTextureMode(sliderRt);
                    BeginScissorMode(x0, y0, x1 - x0, y1 - y0);
                    ClearBackground(Color.Blank);
                    EndScissorMode();
                    DrawBody(i, count, SliderGradient(skin.SliderBorder, track));
                    Game.EndTexture();
                    if (Falling) PushFall(i);
                    // (a render texture is stored bottom-up: rows counted from its bottom, drawn flipped)
                    DrawTextureRec(tex, new Rectangle(x0, tex.Height - y1, x1 - x0, -(y1 - y0)), new Vector2(x0, y0), tint);
                }
            }
        }

        var off = map.StackOffset(o);
        int span = time < o.Time ? 0 : Math.Min(o.Slides - 1, (int)((time - o.Time) / o.SpanDuration));
        bool active = time >= o.Time && time <= o.EndTime;

        // ticks still ahead in the current span
        for (int c = s.NextCheckpoint; c < o.Checkpoints.Count; c++)
        {
            var cp = o.Checkpoints[c];
            if (cp.Span != span || cp.Kind != CheckpointKind.Tick) continue;
            // stable: a tick pops in (fade + 0.5 -> 1.2, then -> 1 Out) on the first span halfway between the slider's start and
            // its own time, less 2/3 of the preempt (150ms steps), halfway from its span's start on repeats (200ms in, settling over 200ms); HD fades
            // it out over the second before it.
            double pop, popIn, settle;
            if (span == 0) { pop = (int)((cp.Time - o.Time) / 2) + o.Time - (int)(map.Preempt * 2 / 3); popIn = 150; settle = 150; } // stable: 2/3 of the preempt (when snaking completes)
            else
            {
                double spanFrom = o.Time + span * o.SpanDuration;
                pop = spanFrom + (cp.Time - spanFrom) / 2 - 200; popIn = 150; settle = 200;
            }
            float tA = (float)Math.Clamp((time - pop) / (span == 0 ? 150 : 200), 0, 1);
            if (mods.HasFlag(Mods.Hidden)) tA *= (float)Math.Clamp((cp.Time - time) / Math.Max(1, cp.Time - Math.Max(pop + popIn, cp.Time - 1000)), 0, 1);
            float tS = time < pop + popIn ? 0.5f + 0.7f * (float)Math.Clamp((time - pop) / popIn, 0, 1)
                : 1.2f - 0.2f * (float)Easing.Apply(1, (time - pop - popIn) / settle);
            if (tA <= 0) continue;
            // stable draws no tick within a circle radius of the slider's start or end (it still counts)
            var tickAt = o.PositionAtTime(cp.Time);
            float r2 = map.Radius * 1.00041f * (map.Radius * 1.00041f);
            if (Vector2.DistanceSquared(tickAt, o.Pos) < r2 || Vector2.DistanceSquared(tickAt, o.EndPos) < r2) continue;
            var p = ToScreen(tickAt + off);
            var tc = ColorAlpha(Color.White, tA * playfieldAlpha);
            if (skin.Tex("sliderscorepoint") is { } sp) sp.Draw(p, scale * tS, tc);
            else DrawCircleV(p, 3 * scale * tS, tc);
        }

        // end circle + reverse arrows
        int remaining = o.Slides - 1 - span;
        Vector2 spanEnd = span % 2 == 0 ? o.PointAt(o.Length) : o.PointAt(0);
        Vector2 spanStart = span % 2 == 0 ? o.PointAt(0) : o.PointAt(o.Length);
        if (snake >= 1)
        {
            // end circles at every end still to come (both, while a repeat remains): sliderendcircle (+ overlay), else
            // a plain hit circle like stable's default skin
            bool own = skin.Tex("sliderendcircle") != null;
            float tailAlpha = mods.HasFlag(Mods.Hidden) ? FadeInAlpha(o) * playfieldAlpha : alpha; // HD: end circles fade like hit circles (stable)
            void Tail(Vector2 at)
            {
                var tail = ToScreen(at + off);
                (own ? skin.Tex("sliderendcircle") : skin.Tex("hitcircle"))?.Draw(tail, CircleScale, ColorAlpha(Shade(colour, lit), tailAlpha));
                (own ? skin.Tex("sliderendcircleoverlay") : skin.Tex("hitcircleoverlay"))?.Draw(tail, CircleScale, ColorAlpha(Shade(Color.White, lit), tailAlpha));
            }
            Tail(spanEnd);
            if (remaining >= 1) Tail(spanStart);
            if (remaining >= 1) DrawReverse(o, span % 2 == 0, spanEnd + off, alpha);
            if (remaining >= 2) DrawReverse(o, span % 2 == 1, spanStart + off, alpha);
        }

        // head
        var (headAlpha, headExpand) = HitAnim(o, s.HeadDone, s.HeadHit, s.HeadTime);
        if (headAlpha > 0) DrawCircleSprite(PosOf(o), colour, headAlpha, headExpand, o.ComboNumber, true, lit);

        if (active)
        {
            // stable's ball: the sliderb frames (sliderb0, 1 ...), turned to face the way it moves (on return spans the
            // way it went out, the frames running backwards and, with SliderBallFlip, mirrored), a frame per 60Hz tick
            // (slower under 150px/s), upside down when a curved path sets off leftwards. Its own default ball comes in the skin's SliderBall
            // colour with sliderb-nd (near black) behind and sliderb-spec added on top; combo-coloured with the option,
            // which a skin's own ball only follows with AllowSliderBallTint.
            var ballOsu = BallOf(o, time);
            var ball = ToScreen(ballOsu);
            var ahead = BallOf(o, Math.Min(o.EndTime, time + 1)) - ballOsu;
            if (ahead == Vector2.Zero) ahead = ballOsu - BallOf(o, time - 1);
            float rot = MathF.Atan2(ahead.Y, ahead.X) * 180 / MathF.PI + (span % 2 == 1 ? 180 : 0);
            var frames = skin.Animation("sliderb", false);
            int s0 = skin.Source("sliderb0"), s1 = skin.Source("sliderb");
            bool builtIn = (s0 > 0 && s0 >= s1 ? s0 : s1) == 1;
            bool comboTint = (builtIn || skin.AllowSliderBallTint) && Config.GetBool("ComboColourSliderBall", true);
            var tint = ColorAlpha(comboTint ? colour : builtIn ? skin.SliderBall : Color.White, playfieldAlpha);
            if (builtIn) skin.Tex("sliderb-nd")?.Draw(ball, CircleScale, ColorAlpha(new Color(5, 5, 5, 255), playfieldAlpha));
            double into = Math.Max(0, time - o.Time);
            double inSpan = into - span * o.SpanDuration;
            double frameMs = Math.Max(150 / Math.Max(1, o.StableVelocity), 1) * 1000 / 60;
            int fi = frames.Length == 0 ? 0 : (int)((span % 2 == 0 ? inSpan : o.SpanDuration - inSpan) / frameMs) % frames.Length;
            bool flipY = o.Path.Length > 3 && MathF.Abs(MathF.Atan2(o.Path[1].Y - o.Path[0].Y, o.Path[1].X - o.Path[0].X)) > MathF.PI / 2;
            if (frames.Length > 0) frames[fi].Draw(ball, CircleScale, tint, rot, skin.SliderBallFlip && span % 2 == 1, flipY);
            else DrawCircleV(ball, map.Radius * scale * 0.8f, tint);
            if (builtIn && skin.Tex("sliderb-spec") is { } spec)
            {
                BeginBlendMode(BlendMode.Additive);
                spec.Draw(ball, CircleScale, ColorAlpha(Color.White, playfieldAlpha));
                EndBlendMode();
            }
        }
        DrawFollowCircle(o, s);
    }

    // stable's follow circle: on grabbing, fade in over 60ms and grow 0.5 -> 1 over 180ms (Out); each
    // checkpoint but the last pulses 1.1 -> 1 (less when they're under 200ms apart); losing it fades out growing
    // 1 -> 2 over the 100ms before the next checkpoint; held to the end it fades (In) shrinking to 0.8 over 200ms.
    void DrawFollowCircle(HitObject o, ObjState s)
    {
        double t = time, end = o.EndTime;
        float Seg(double a, double b) => (float)Math.Clamp((t - a) / Math.Max(1, b - a), 0, 1);
        float alpha, sc;
        if (s.Tracking && t < end)
        {
            double t0 = Math.Max(s.TrackingSince, o.Time);
            alpha = Seg(t0, Math.Min(end, t0 + 60));
            sc = 0.5f + 0.5f * (float)Easing.Apply(1, Seg(t0, Math.Min(end, t0 + 180)));
            var cps = o.Checkpoints;
            double gap = cps.Count > 1 ? Math.Min(200, cps[1].Time - cps[0].Time) : 200;
            float settle = 1.1f - (float)(gap / 200 * 0.1);
            for (int k = cps.Count - 2; k >= 0; k--)
                if (cps[k].Time <= t && cps[k].Time >= t0)
                {
                    if (t <= cps[k].Time + gap) sc = 1.1f + (settle - 1.1f) * Seg(cps[k].Time, Math.Min(end, cps[k].Time + gap));
                    else sc = settle;
                    break;
                }
        }
        else if (s.Done && s.TrackedAtEnd)
        {
            if (t > end + 200) return;
            float f = Seg(end, end + 200);
            alpha = 1 - f * f;
            sc = 1 - 0.2f * (float)Easing.Apply(1, f);
        }
        else if (!double.IsNaN(s.FollowLostAt))
        {
            double next = end;
            foreach (var cp in o.Checkpoints) if (cp.Time > s.FollowLostAt) { next = cp.Time; break; }
            if (t >= next) return;
            float f = Seg(next - 100, next);
            alpha = 1 - f;
            sc = 1 + f;
        }
        else return;
        if (alpha <= 0) return;
        var at = ToScreen(BallOf(o, Math.Min(t, end)));
        var white = ColorAlpha(Color.White, alpha * playfieldAlpha);
        if (skin.AnimFrame("sliderfollowcircle", t - o.Time) is { } fc) fc.Draw(at, CircleScale * sc, white);
        else DrawRing(at, map.Radius * scale * 2.3f * sc, map.Radius * scale * 2.4f * sc, 0, 360, 48, white);
    }

    void DrawReverse(HitObject o, bool atEnd, Vector2 osuPos, float alpha)
    {
        double d = atEnd ? o.Length : 0;
        double toward = atEnd ? Math.Max(0, o.Length - 5) : Math.Min(o.Length, 5);
        var dir = o.PointAt(toward) - o.PointAt(d);
        float rot = MathF.Atan2(dir.Y, dir.X) * 180 / MathF.PI;
        var p = ToScreen(osuPos);
        // stable: the default skin's arrow turns black on light combo colours (R + G + B over 600)
        var c = ColourOf(o);
        var tint = !skin.Own("reversearrow") && c.R + c.G + c.B > 600 ? Color.Black : Color.White;
        if (skin.Tex("reversearrow") is { } ra) ra.Draw(p, CircleScale, ColorAlpha(tint, alpha), rot);
        else Game.Text("<", p.X - 10, p.Y - 20, 40 * scale / 2.25f, ColorAlpha(Color.White, alpha));
    }

    // stable's slider body: a 128x1 gradient across the radius, outside in: a soft shadow,
    // the border colour up to 0.1875, then the track colour darkened by 0.1 shading to lightened by 0.5 at the
    // centre, all at alpha 180. Each path point is a depth-tested cone (centre nearest), so overlaps take the
    // part closest to any centre line.
    Texture2D sliderGradient;
    (Color, Color) sliderGradientKey;

    static Color Lighten(Color c, float amount)
    {
        amount *= 0.5f;
        byte L(byte v) => (byte)Math.Min(255f, v * (1 + 0.5f * amount) + 255 * amount);
        return new Color(L(c.R), L(c.G), L(c.B), c.A);
    }

    static Color Darken(Color c, float amount) =>
        new((byte)Math.Min(255f, c.R / (1 + amount)), (byte)Math.Min(255f, c.G / (1 + amount)), (byte)Math.Min(255f, c.B / (1 + amount)), c.A);

    // Color isn't IEquatable, so tuple/Equals comparisons box it (per slider, per frame).
    static bool Same(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;

    Texture2D SliderGradient(Color border, Color track)
    {
        if (sliderGradient.Id != 0 && Same(sliderGradientKey.Item1, border) && Same(sliderGradientKey.Item2, track)) return sliderGradient;
        if (sliderGradient.Id != 0) UnloadTexture(sliderGradient);
        var body = new Color(track.R, track.G, track.B, (byte)180);
        Color inner = Lighten(body, 0.5f), outer = Darken(body, 0.1f), shadow = new(0, 0, 0, 64);
        float aa = Math.Clamp(0.5f / (map.Radius * scale), 3 / 256f, 1 / 16f);
        (float At, Color C)[] stops = [(0, Color.Blank), (5 / 64f - aa, shadow), (5 / 64f + aa, border), (0.1875f - aa, border), (0.1875f + aa, outer), (1, inner)];
        var img = GenImageColor(128, 1, Color.Blank);
        for (int x = 0; x < 128; x++)
        {
            float t = (x + 0.5f) / 128;
            int k = 1;
            while (k < stops.Length - 1 && stops[k].At < t) k++;
            var (a, ca) = stops[k - 1];
            var (b, cb) = stops[k];
            ImageDrawPixel(ref img, x, 0, ColorLerp(ca, cb, Math.Clamp((t - a) / Math.Max(1e-6f, b - a), 0, 1)));
        }
        sliderGradient = LoadTextureFromImage(img);
        UnloadImage(img);
        SetTextureFilter(sliderGradient, TextureFilter.Bilinear);
        SetTextureWrap(sliderGradient, TextureWrap.Clamp);
        sliderGradientKey = (border, track);
        return sliderGradient;
    }

    // Like stable: each path segment is a tent-shaped quad pair (centre line nearest, gradient straight across the
    // width) and each joint a cone filling the turn, so edges are smooth lines rather than overlapping polygons.
    // A body's triangles (screen space): per path point the tent quads from the previous point, then a 64-segment cone;
    // the centre line sits near the viewer (depth -0.01), the edge far (-0.99), and the depth test keeps the nearest, so
    // overlaps show the gradient's centre. Built once per slider and kept on the GPU while it's up (rebuilding ~10k
    // vertices per body every frame was most of a dense map's CPU time); snaking draws the first part (Ends[k] = vertices
    // through point k).
    readonly Dictionary<int, (Mesh Mesh, int[] Ends)> bodyMeshes = [];
    readonly Dictionary<int, (RenderTexture2D Rt, int W, int H)> bodyCache = []; // finished bodies, drawn once (see DrawSlider)
    // Their textures come from a pool and go back to it: creating one took ~2ms each time a slider appeared. Sizes are
    // rounded up to 128 so they get reused; a few are made while the play loads.
    readonly List<RenderTexture2D> bodyTexturePool = [];
    RenderTexture2D TakeBodyTexture(int w, int h)
    {
        int best = -1;
        for (int k = 0; k < bodyTexturePool.Count; k++)
        {
            var t = bodyTexturePool[k].Texture;
            if (t.Width >= w && t.Height >= h && (best < 0 || t.Width * t.Height < bodyTexturePool[best].Texture.Width * bodyTexturePool[best].Texture.Height)) best = k;
        }
        if (best < 0) return LoadRenderTexture((w + 127) / 128 * 128, (h + 127) / 128 * 128);
        var rt = bodyTexturePool[best];
        bodyTexturePool.RemoveAt(best);
        return rt;
    }

    // a unit circle in the body cones' 64 steps (the same floats as computing them each time)
    static readonly Vector2[] Circle64 = Enumerable.Range(0, 65).Select(j => new Vector2(MathF.Cos(j * MathF.Tau / 64), MathF.Sin(j * MathF.Tau / 64))).ToArray();

    // a body drawn directly (immediate mode), for one that's drawn only once: cheaper than building its mesh
    void DrawBodyOnce(Vector2[] pts, Texture2D gradient)
    {
        float r = map.Radius * scale;
        Rlgl.DrawRenderBatchActive();
        Rlgl.EnableDepthTest();
        Rlgl.DisableColorBlend(); // write the gradient's own alpha; the texture is composited afterwards
        static void Centre(Vector2 p) { Rlgl.TexCoord2f(1, 0); Rlgl.Vertex3f(p.X, p.Y, -0.01f); }
        static void Edge(Vector2 p) { Rlgl.TexCoord2f(0, 0); Rlgl.Vertex3f(p.X, p.Y, -0.99f); }
        void Begin(int vertices)
        {
            Rlgl.CheckRenderBatchLimit(vertices);
            Rlgl.Begin(DrawMode.Triangles);
            Rlgl.SetTexture(gradient.Id); // after Begin: a mode change resets the batch texture
            Rlgl.Color4ub(255, 255, 255, 255);
        }
        Vector2 prev = default;
        for (int k = 0; k < pts.Length; k++)
        {
            var c = ToScreen(pts[k]);
            if (k > 0 && Vector2.DistanceSquared(prev, c) > 1e-6f)
            {
                var n = Vector2.Normalize(new Vector2(prev.Y - c.Y, c.X - prev.X)) * r;
                Begin(12);
                for (int sgn = 1; sgn >= -1; sgn -= 2)
                {
                    var side = n * sgn;
                    Centre(prev); Edge(prev + side); Edge(c + side);
                    Centre(prev); Edge(c + side); Centre(c);
                }
                Rlgl.End();
            }
            Begin(64 * 3);
            for (int j = 0; j < 64; j++) { Centre(c); Edge(c + Circle64[j + 1] * r); Edge(c + Circle64[j] * r); }
            Rlgl.End();
            prev = c;
        }
        Rlgl.SetTexture(0);
        Rlgl.DrawRenderBatchActive();
        Rlgl.EnableColorBlend();
        Rlgl.DisableDepthTest();
    }
    static Material bodyMaterial;
    static bool bodyMaterialReady;

    (Mesh Mesh, int[] Ends) BodyMesh(int i)
    {
        if (bodyMeshes.TryGetValue(i, out var built)) return built;
        const int segments = 64;
        var pts = bodies[i];
        float r = map.Radius * scale;
        var pos = new List<Vector3>(pts.Length * (segments * 3 + 12));
        var uv = new List<Vector2>(pos.Capacity);
        var ends = new int[pts.Length];
        void Centre(Vector2 p) { pos.Add(new Vector3(p, -0.01f)); uv.Add(new Vector2(1, 0)); }
        void Edge(Vector2 p) { pos.Add(new Vector3(p, -0.99f)); uv.Add(new Vector2(0, 0)); }
        Vector2 prev = default;
        for (int k = 0; k < pts.Length; k++)
        {
            var c = ToScreen(pts[k]);
            if (k > 0 && Vector2.DistanceSquared(prev, c) > 1e-6f)
            {
                var n = Vector2.Normalize(new Vector2(prev.Y - c.Y, c.X - prev.X)) * r;
                for (int sgn = 1; sgn >= -1; sgn -= 2)
                {
                    var side = n * sgn;
                    Centre(prev); Edge(prev + side); Edge(c + side);
                    Centre(prev); Edge(c + side); Centre(c);
                }
            }
            for (int j = 0; j < segments; j++) { Centre(c); Edge(c + Circle64[j + 1] * r); Edge(c + Circle64[j] * r); }
            ends[k] = pos.Count;
            prev = c;
        }
        var mesh = new Mesh(pos.Count, pos.Count / 3);
        mesh.AllocVertices();
        mesh.AllocTexCoords();
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pos).CopyTo(mesh.VerticesAs<Vector3>());
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(uv).CopyTo(mesh.TexCoordsAs<Vector2>());
        UploadMesh(ref mesh, false);
        if (!bodyMaterialReady) { bodyMaterial = LoadMaterialDefault(); bodyMaterialReady = true; }
        return bodyMeshes[i] = (mesh, ends);
    }

    void DrawBody(int i, int count, Texture2D gradient)
    {
        var (mesh, ends) = BodyMesh(i);
        mesh.VertexCount = ends[Math.Clamp(count, 1, ends.Length) - 1];
        mesh.TriangleCount = mesh.VertexCount / 3;
        Rlgl.DrawRenderBatchActive();
        Rlgl.EnableDepthTest();
        Rlgl.DisableColorBlend(); // write the gradient's own alpha; the texture is composited afterwards
        SetMaterialTexture(ref bodyMaterial, MaterialMapIndex.Albedo, gradient);
        DrawMesh(mesh, bodyMaterial, Matrix4x4.Identity);
        Rlgl.EnableColorBlend();
        Rlgl.DisableDepthTest();
    }

    // once a frame: bodies of sliders that are done go
    readonly List<int> bodyMeshesDone = [];
    void SweepBodyMeshes()
    {
        foreach (var k in bodyMeshes.Keys) if (k < firstActive) bodyMeshesDone.Add(k);
        foreach (var k in bodyMeshesDone) { UnloadMesh(bodyMeshes[k].Mesh); bodyMeshes.Remove(k); }
        bodyMeshesDone.Clear();
        foreach (var k in bodyCache.Keys) if (k < firstActive) bodyMeshesDone.Add(k);
        foreach (var k in bodyMeshesDone) { bodyTexturePool.Add(bodyCache[k].Rt); bodyCache.Remove(k); }
        bodyMeshesDone.Clear();
    }

    // Follow points: dots along the gap between consecutive objects of the same combo, appearing in sequence.
    // stable's hit lighting (the Hit lighting option): every circle hit and slider end leaves the skin's lighting in the
    // object's combo colour, added on at hit-circle scale, growing 0.8 -> 1.2 (Out) over 600ms, fading in over 200ms and
    // out over 1000ms from 400ms (read from a running stable).
    readonly List<(Vector2 Pos, Color Colour, double At)> lightings = [];

    void DrawLightings()
    {
        lightings.RemoveAll(l => time - l.At >= 1400 || time < l.At);
        if (lightings.Count == 0 || skin.Tex("lighting") is not { } tex) return;
        BeginBlendMode(BlendMode.Additive);
        foreach (var (pos, colour, at) in lightings)
        {
            double t = time - at;
            float a = (float)Math.Min(t / 200, Math.Clamp(1 - (t - 400) / 1000, 0, 1)) * playfieldAlpha;
            float sc = 0.8f + 0.4f * (float)Easing.Apply(1, t / 600);
            tex.Draw(ToScreen(pos), CircleScale * sc, ColorAlpha(colour, a));
        }
        EndBlendMode();
    }

    // stable's slider point popups: only skins of version 1.0 or below (with their own sliderpoint10 / sliderpoint30)
    // show them, at the slider ball for each tick (10) and repeat (30), not the end: rising 10 units over 300ms (Out),
    // then fading over 60ms.
    readonly List<(Vector2 Pos, double At, bool Ten)> pointPops = [];
    bool PointPops => skin.Version <= 1 && (skin.Own("sliderpoint10") || skin.Own("sliderpoint30"));

    void DrawPointPops()
    {
        for (int i = pointPops.Count - 1; i >= 0; i--)
        {
            var (pos, at, ten) = pointPops[i];
            double t = time - at;
            if (t >= 360 || t < 0) { pointPops.RemoveAt(i); continue; }
            if (skin.Tex(ten ? "sliderpoint10" : "sliderpoint30") is not { } tex) continue;
            float a = (1 - (float)Math.Clamp((t - 300) / 60, 0, 1)) * playfieldAlpha;
            tex.Draw(ToScreen(pos - new Vector2(0, 10 * (float)Easing.Apply(1, t / 300))), scale / 2.25f, ColorAlpha(Color.White, a)); // (judgements' field scale)
        }
    }

    void DrawFollowPoints()
    {
        // stable: each point fades in over FadeIn from 800ms (x preempt/450 when that is under 450) before its time,
        // sliding into place and shrinking 1.5 -> 1 (Out), then fades out over FadeIn from its time.
        double lead = 800 * Math.Min(1, map.Preempt / 450), fade = map.FadeIn;
        while (fpFirst < followPoints.Length && followPoints[fpFirst].Time + fade < time) fpFirst++;
        for (int k = fpFirst; k < followPoints.Length && followPoints[k].Time - lead <= time; k++)
        {
            var fp = followPoints[k];
            double age = time - (fp.Time - lead);
            float t = (float)Math.Clamp(age / fade, 0, 1), e = 1 - (1 - t) * (1 - t);
            float a = t * (float)Math.Clamp(1 - (time - fp.Time) / fade, 0, 1) * playfieldAlpha;
            if (a <= 0) continue;
            Sprite? sprite = skin.Frame("followpoint", Math.Min(fpFrames - 1, (int)(age / fpFrameMs)));
            var p = ToScreen(Vector2.Lerp(fp.From, fp.Pos, e));
            float sc = 1.5f - 0.5f * e;
            if (sprite is { } sp) sp.Draw(p, Game.H / 768f * sc, ColorAlpha(Color.White, a), fp.Rotation);
            else DrawCircleV(p, 2.5f * scale * sc, ColorAlpha(Color.White, a * 0.8f));
        }
    }

    void DrawSpinner(int i)
    {
        // stable's old-style spinner, in 640x480 units around the screen's middle column: background
        // (tinted SpinnerBackground) and circle at base + 219, the metre's lit bars from the top (10 steps of 43.25,
        // the next one blinking in by chance unless SpinnerNoBlink), approach circle 1.86 -> 0.1 (not with HD),
        // "SPIN!" at base + 335, "CLEAR" at base + 115 when done, bonus number at base + 299, RPM label/number at the bottom sliding up.
        var o = objs[i];
        var s = st[i];
        float fadeIn = (float)Math.Max(1, map.FadeIn);
        float alpha = (float)Math.Clamp((time - (o.Time - fadeIn)) / fadeIn, 0, 1);
        if (s.Done) alpha *= (float)Math.Max(0, 1 - (time - s.JudgeTime) / 240);
        alpha *= playfieldAlpha;
        if (alpha <= 0) return;

        int w = Game.W, h = Game.H;
        float u = h / 480f, sd = h / 768f, cx = w / 2f;
        Vector2 U(float x, float y) => new(cx + x * u, y * u);
        // stable lays the spinner out from a base of 45, less 16 on widescreen: centred on the playfield (248).
        const float Base = 29;
        var c = U(0, Base + 219);
        var white = ColorAlpha(Color.White, alpha);
        // stable greys the spinning parts when the spinner spins itself (SpunOut, Autopilot)
        var spinTint = mods.HasFlag(Mods.SpunOut) || autopilot ? ColorAlpha(Xna.Gray, alpha) : white;
        if (skin.SpinnerFadePlayfield) DrawRectangle(0, 0, w, h, ColorAlpha(Color.Black, alpha)); // old skins: the playfield fades to black
        int req = SpinsRequired(o);
        float rotDeg = (float)(s.Angle * 180 / Math.PI);
        double progress = Math.Clamp((time - o.Time) / Math.Max(1, o.EndTime - o.Time), 0, 1);
        // stable: v2+ skins without their own spinner-background (and circle) use the newer spinner.
        bool newStyle = skin.Version > 1 && !skin.Own("spinner-background") && !skin.Own("spinner-circle");
        if (newStyle)
        {
            // Glow (additive, blue, as bright as the progress), bottom (a third of the top's turn), top (half the
            // spin when middle2 exists), middle2 (the spin), middle (reddening as time runs out); all grow 0.8 -> 1
            // with progress, which freezes once cleared; bonuses flash the glow white for 200ms.
            float spun = (float)Math.Min(1, s.Rotation / Math.Max(1, req));
            if (!double.IsNaN(s.ClearAt) && time >= s.ClearAt) spun = 1;
            float grow = sd * (0.8f + 0.2f * spun);
            bool middle2 = skin.Tex("spinner-middle2") != null;
            float top = rotDeg * (middle2 ? 0.5f : 1);
            var glowColour = new Color(3, 151, 255, 255);
            if (!double.IsNaN(s.BonusAt) && time - s.BonusAt < 200)
                glowColour = ColorLerp(Color.White, glowColour, (float)((time - s.BonusAt) / 200));
            if (skin.Tex("spinner-glow") is { } glow)
            {
                BeginBlendMode(BlendMode.Additive);
                glow.Draw(c, grow, ColorAlpha(glowColour, spun * alpha));
                EndBlendMode();
            }
            skin.Tex("spinner-bottom")?.Draw(c, grow, spinTint, top / 3);
            skin.Tex("spinner-top")?.Draw(c, grow, spinTint, top);
            skin.Tex("spinner-middle2")?.Draw(c, grow, white, rotDeg);
            skin.Tex("spinner-middle")?.Draw(c, grow, ColorAlpha(ColorLerp(Color.White, Xna.Red, (float)progress), alpha));
        }
        else
        {
            skin.Tex("spinner-background")?.Draw(c, sd, ColorAlpha(skin.SpinnerBackground, alpha));
            if (skin.Tex("spinner-metre") is { } m)
            {
                float f = Math.Min(99, (float)(s.Rotation / Math.Max(1, req) * 100));
                int n = (int)f / 10;
                if (skin.SpinnerNoBlink || Random.Shared.NextSingle() < f % 10 / 10) n++;
                float k = 1 / m.Scale; // texels per 1x pixel
                DrawTexturePro(m.Tex, new Rectangle(0, 69.2f * (10 - n) * k, m.Tex.Width, 69.2f * n * k),
                    new Rectangle(cx - 320 * u, (Base + 43.25f * (10 - n)) * u, m.Width * sd, 69.2f * n * sd), Vector2.Zero, 0, white);
            }
            if (skin.Tex("spinner-circle") is { } sc) sc.Draw(c, sd, spinTint, rotDeg);
            else DrawRing(c, 180 * sd, 190 * sd, rotDeg, rotDeg + 300, 64, white);
        }

        // The approach circle only shows when the skin brings its own disc (stable leaves the default's out).
        if (time >= o.Time && !s.Done && !mods.HasFlag(Mods.Hidden) && skin.Own(newStyle ? "spinner-top" : "spinner-circle"))
        {
            float a = 1.86f - 1.76f * (float)progress;
            if (skin.Tex("spinner-approachcircle") is { } ac) ac.Draw(c, sd * a, white);
            else DrawRing(c, 190 * sd * a, 196 * sd * a, 0, 360, 64, white);
        }

        double clearAt = s.SpinsCounted >= req ? (double.IsNaN(s.ClearAt) ? o.Time : s.ClearAt) : double.NaN;
        if (skin.Tex("spinner-spin") is { } spin)
        {
            float a = (float)Math.Clamp((time - (o.Time - fadeIn / 2)) / (fadeIn / 2), 0, 1);
            double outFrom = o.EndTime - Math.Min(400, o.EndTime - o.Time);
            if (time > outFrom) a *= (float)Math.Clamp(1 - (time - outFrom) / Math.Max(1, o.EndTime - outFrom), 0, 1);
            if (!double.IsNaN(clearAt)) a *= (float)Math.Clamp(1 - (time - clearAt) / 100, 0, 1);
            if (!double.IsNaN(s.SpinTextFadeAt)) a *= (float)Math.Clamp(1 - (time - s.SpinTextFadeAt) / 300, 0, 1);
            if (a > 0) spin.Draw(U(0, Base + 335), sd, ColorAlpha(Color.White, a * playfieldAlpha));
        }
        if (!double.IsNaN(clearAt) && skin.Tex("spinner-clear") is { } clear)
        {
            double t = time - clearAt;
            float a = (float)Easing.Apply(1, Math.Clamp(t / 400, 0, 1));
            if (time > o.EndTime - 50) a *= (float)Math.Clamp((o.EndTime - time) / 50, 0, 1);
            float sc2 = t < 240 ? 2 - 1.2f * (float)Easing.Apply(1, t / 240) : 0.8f + 0.2f * (float)Math.Clamp((t - 240) / 160, 0, 1);
            if (a > 0 && !s.Done) clear.Draw(U(0, Base + 115), sd * sc2, ColorAlpha(Color.White, a * playfieldAlpha));
        }
        if (!double.IsNaN(s.BonusAt) && time - s.BonusAt < 800)
        {
            float t = (float)Easing.Apply(1, (time - s.BonusAt) / 800), sc3 = sd * (2 - 0.72f * t);
            string b = bonusText.Of(s.Bonus);
            float gh = (skin.Glyph(skin.ScorePrefix, '0')?.Height ?? 30) * sc3;
            skin.DrawFont(skin.ScorePrefix, skin.ScoreOverlap, b, cx, (Base + 299) * u - gh / 2, sc3, 0.5f, ColorAlpha(Color.White, (1 - t) * alpha));
        }
        if (skin.Tex("spinner-rpm") is { } rpm)
        {
            float slide = 50 * (1 - (float)Easing.Apply(1, Math.Clamp((time - (o.Time - fadeIn)) / fadeIn, 0, 1)));
            var at = U(-87, 445 + slide);
            rpm.Draw(new Vector2(at.X + rpm.Width * sd / 2, at.Y + rpm.Height * sd / 2), sd, white);
            skin.DrawFont(skin.ScorePrefix, skin.ScoreOverlap, ((int)s.Rpm).ToString(), cx + 80 * u, (448 + slide) * u, 0.9f * sd, 1, white);
        }
    }

    void DrawApproach(int i)
    {
        var o = objs[i];
        var s = st[i];
        if (o.Type == ObjType.Spinner) return;
        if (o.Type == ObjType.Circle ? s.Done : s.HeadDone) return;
        if (time > o.Time) return;
        if (mods.HasFlag(Mods.Hidden) && (i != 0 || !Config.GetBool("HiddenShowFirstApproach", true))) return; // HD: only the first object keeps its approach circle (an option)
        double t = (o.Time - time) / map.Preempt; // 1 -> 0
        float alpha = Math.Min(0.9f, FadeInAlpha(o) * 0.9f / Math.Max(0.001f, (float)Math.Min(1, map.FadeIn * 2 / map.Preempt)));
        alpha = Math.Clamp(alpha, 0, 0.9f) * playfieldAlpha;
        float approachScale = 1 + 3 * (float)t;
        var p = ToScreen(PosOf(o));
        if (skin.Tex("approachcircle") is { } ac) ac.Draw(p, CircleScale * approachScale, ColorAlpha(ColourOf(o), alpha));
        else
        {
            float r = map.Radius * scale * approachScale;
            DrawRing(p, r - 3, r, 0, 360, 64, ColorAlpha(ColourOf(o), alpha));
        }
    }

    // Visible circle around the cursor that shrinks at 100 and 200 combo, like stable.
    // stable's flashlight: the flashlightv2 mask (osu!gameplay.dll) drawn black at the cursor, sized in 640x480
    // units: 3.2 under 100 combo, 2.6 under 200, 2.0 after; 8 (wide open) in breaks. It eases 0.1 per 60Hz
    // frame, so it opens from 1 at the start. Holding a slider dims the whole screen to 80% black.
    float flScale = 1;

    void UpdateFlashlight(double dtMs)
    {
        // (catch's is wider: 5.2, 4.6, 4.0; mania's a fixed band)
        float target = InBreak(time) ? 8 : mode == 2 ? (combo < 100 ? 5.2f : combo < 200 ? 4.6f : 4f) : combo < 100 ? 3.2f : combo < 200 ? 2.6f : 2f;
        float step = (float)(0.1 * dtMs / (1000 / 60.0));
        flScale = flScale > target ? Math.Max(flScale - step, target) : Math.Min(flScale + step, target);
    }

    void DrawFlashlight(int w, int h)
    {
        if (mode != 0) { DrawModeFlashlight(w, h); return; }
        bool sliding = false;
        for (int i = firstActive; i < objs.Count && objs[i].Time <= time; i++)
            if (objs[i].Type == ObjType.Slider && st[i].Tracking && time <= objs[i].EndTime) sliding = true;
        if (sliding) DrawRectangle(0, 0, w, h, ColorAlpha(Color.Black, 0.8f));

        var c = ToScreen(cursor);
        c = new Vector2(Math.Clamp(c.X, 0, w), Math.Clamp(c.Y, 0, h));
        if (skin.Tex("flashlightv2") is { } fl)
        {
            fl.Draw(c, flScale * h / 480f * (fl.Scale == 1 ? 1 : 2), Color.Black);
            return;
        }
        // No mask available (osu!gameplay.dll missing): a plain circle of the same size.
        float r = 55 * flScale * h / 480f, far = MathF.Sqrt(w * w + h * h) * 2;
        DrawRing(c, r, far, 0, 360, 96, Color.Black);
    }

    // stable's flashlight in the other modes: taiko's stays at (208, 208), catch's follows the catcher, both sized like
    // osu!'s (catch's wider); mania's is its own flashlightv2_mania band, left-centred at y 220 at 1.4
    void DrawModeFlashlight(int w, int h)
    {
        float u = h / 480f;
        if (mode == 3)
        {
            if ((skin.DefaultTex("flashlightv2_mania") ?? skin.DefaultTex("flashlight2")) is not { } band) return;
            float bs = 1.4f * u * (band.Scale == 1 ? 1 : 2), bw = band.Width * bs, bh = band.Height * bs;
            DrawTexturePro(band.Tex, new Rectangle(0, 0, band.Tex.Width, band.Tex.Height), new Rectangle(0, 220 * u - bh / 2, bw, bh), Vector2.Zero, 0, Color.White);
            if (bw < w) DrawRectangle((int)bw, 0, w - (int)bw, h, Color.Black); // (past the band's right end)
            return;
        }
        var c = mode == 1 ? new Vector2(208 * u, 208 * u) : new Vector2((w - 512 * u) / 2 + cX * u, (72 + 340) * u);
        if (skin.Tex("flashlightv2") is { } fl) { fl.Draw(c, flScale * u * (fl.Scale == 1 ? 1 : 2), Color.Black); return; }
        float r = 55 * flScale * u, far = MathF.Sqrt(w * w + h * h) * 2;
        DrawRing(c, r, far, 0, 360, 96, Color.Black);
    }

    // a judgement's name without its last letter (hit100k -> hit100), kept so drawing doesn't slice it every frame
    readonly Dictionary<string, string> popupBase = [];
    string PopupBase(string name) => popupBase.TryGetValue(name, out var b) ? b : popupBase[name] = name[..^1];

    // stable's judgement depths, as layers: 0 the particles, at the very bottom; 1 a hit that has particles, under
    // every hit object; 2 a spinner's own judgement, just over its spinner; 3 misses and hits without particles, over
    // the hit objects; 4 the particle hits' additive flashes, over everything. A hit only has particles when the
    // judgement's own source (beatmap, skin or default) has particleNNN.
    void DrawPopups(int layer)
    {
        // stable's judgement animations: misses fade in over 120ms shrinking 2 -> 1, tilt a random
        // +-0.15 rad (doubling by 1100ms) and, on v2+ skins, sink 45 units; hits bounce 0.6 -> 1.1 -> 0.9 -> 1, or with
        // particles grow 0.9 -> 1.05 with an additive flash and 150 particles flying up to 32 units
        // (each 400-1200ms, eased out, fading). Everything fades out over 500-1100ms.
        if (layer == 0)
        {
            int kept = 0; // RemoveAll without a per-frame closure
            for (int i = 0; i < popups.Count; i++) if (time - popups[i].Time <= 1200) popups[kept++] = popups[i];
            popups.RemoveRange(kept, popups.Count - kept);
        }
        float baseScale = scale / 2.25f * 1.2f, litScale = baseScale * 1.2f; // particle-style hits measured 1.2x larger at 1080p
        foreach (var (pos, t, result, name, spin) in popups)
        {
            double age = time - t;
            float Seg(double a, double b) => (float)Math.Clamp((age - a) / (b - a), 0, 1);
            float alpha = Math.Min(1, 1 - Seg(500, 1100)) * playfieldAlpha;
            var p = ToScreen(pos);
            // Per-popup randomness (stable from frame to frame) from a seeded hash, so nothing is allocated per frame.
            uint seed = (uint)HashCode.Combine(t, pos.X, pos.Y);
            double Rand() { seed = seed * 1664525 + 1013904223; return (seed >> 8) / 16777216.0; }
            // the skin's animation (hit300-0, -1 ...) plays once at 60fps and holds its last frame; animated
            // judgements skip the bounce/shrink, a miss's tilt and sinking (the particle flash still bounces)
            string anim = skin.Animation(name, true).Length > 0 ? name : PopupBase(name);
            var frames = skin.Animation(anim, true);
            var sp = frames.Length == 0 ? (Sprite?)null : frames[Math.Clamp((int)(age / (1000 / 60.0)), 0, frames.Length - 1)];
            bool single = frames.Length == 1;
            var particle = result > 0 && sp != null ? skin.TexFrom(result switch { 300 => "particle300", 100 => "particle100", _ => "particle50" }, skin.AnimationSource(anim, true)) : null;
            float Bounce() => age < 120 ? 0.6f + 0.5f * Seg(0, 96) : age < 144 ? 1.1f - 0.2f * Seg(120, 144) : 0.9f + 0.1f * Seg(120, 168);
            if (layer == 0)
            {
                if (particle is not { } pt || age < 0) continue;
                BeginBlendMode(BlendMode.Additive);
                for (int k = 0; k < 150; k++)
                {
                    double life = 400 + (int)(Rand() * 800), ang = Rand() * Math.Tau, dist = Rand() * 32;
                    double f = Math.Clamp(age / life, 0, 1);
                    if (f >= 1) continue;
                    float d = (float)(dist * Easing.Apply(13, f)) * scale; // OutQuint
                    pt.Draw(p + new Vector2(MathF.Cos((float)ang), MathF.Sin((float)ang)) * d, baseScale, ColorAlpha(Color.White, (float)(1 - f) * playfieldAlpha));
                }
                EndBlendMode();
                continue;
            }
            if (layer == 4)
            {
                if (particle == null || spin || sp is not { } fl) continue;
                float flash = age < 40 ? 0.5f * (float)Easing.Apply(1, Seg(-16, 40)) : 0.5f * (1 - Seg(40, 340));
                BeginBlendMode(BlendMode.Additive);
                fl.Draw(p, litScale * Bounce(), ColorAlpha(Color.White, flash * playfieldAlpha));
                EndBlendMode();
                continue;
            }
            if (layer != (spin ? 2 : particle != null ? 1 : 3)) continue;
            if (sp is { } s)
            {
                if (!single) s.Draw(p, particle != null ? litScale : baseScale, ColorAlpha(Color.White, alpha * Seg(0, particle != null ? 80 : 120)));
                else if (result == 0)
                {
                    float r = (float)(Rand() * 0.3 - 0.15);
                    float rot = age < 120 ? r * Seg(0, 120) : r + r * (float)Easing.Apply(2, Seg(120, 1100));
                    float drop = skin.Version > 1 ? (-5 + 45 * (float)Easing.Apply(2, Seg(0, 1100))) * scale : 0;
                    s.Draw(p + new Vector2(0, drop), baseScale * (2 - Seg(0, 120)), ColorAlpha(Color.White, alpha * Seg(0, 120)), rot * 180 / MathF.PI);
                }
                else if (particle != null) s.Draw(p, litScale * (0.9f + 0.15f * Seg(0, 1100)), ColorAlpha(Color.White, alpha * Seg(0, 80)));
                else s.Draw(p, baseScale * Bounce(), ColorAlpha(Color.White, alpha * Seg(0, 120)));
            }
            else if (result != 300)
            {
                string txt = result == 0 ? "X" : result.ToString();
                var col = result switch { 100 => Color.Green, 50 => Color.Orange, _ => Color.Red };
                Game.Text(txt, p.X - Game.TextWidth(txt, 40) / 2, p.Y - 20, 40, ColorAlpha(col, alpha));
            }
        }
    }

    internal double Acc => Accuracy; // (--auto-check in the other modes)
    double Accuracy => AccuracyFor(mode, n300, n100, n50, nMiss, nGeki, nKatu, ScoreV2 && !OldModeRules); // (an older own replay keeps the accuracy it was saved with)

    // stable's accuracy per mode (percent)
    internal static double AccuracyFor(int mode, int n300, int n100, int n50, int nMiss, int nGeki, int nKatu, bool v2 = false)
    {
        int total = n300 + n100 + n50 + nMiss;
        if (mode == 1) return total == 0 ? 100 : (n300 + 0.5 * n100) / total * 100; // taiko: a good is half
        if (mode == 2) return total + nKatu == 0 ? 100 : (double)(n300 + n100 + n50) / (total + nKatu) * 100; // catch: anything caught (katu: tiny droplets missed)
        if (mode == 3) // mania: MAX (geki) and 300 full, 200 (katu) two thirds, 100 a third, 50 a sixth; Score V2: MAX 305 and 300 300 of 305
        {
            int all = total + nGeki + nKatu;
            if (v2) return all == 0 ? 100 : (50.0 * n50 + 100.0 * n100 + 200.0 * nKatu + 300.0 * n300 + 305.0 * nGeki) / (305.0 * all) * 100;
            return all == 0 ? 100 : (50.0 * n50 + 100.0 * n100 + 200.0 * nKatu + 300.0 * (n300 + nGeki)) / (300.0 * all) * 100;
        }
        return total == 0 ? 100 : (300.0 * n300 + 100.0 * n100 + 50.0 * n50) / (300.0 * total) * 100;
    }

    (double Ur, double Mean) HitStats()
    {
        if (hitErrors.Count == 0) return (0, 0);
        double mean = hitErrors.Average();
        double variance = hitErrors.Sum(e => (e - mean) * (e - mean)) / hitErrors.Count;
        return (Math.Sqrt(variance) * 10, mean);
    }

    // stable's health bar: the shown value fills from empty at 0.02/ms once the song runs (the marker
    // pulsing), then eases a quarter of the gap per 60Hz frame up and a sixth down. Each HP gain pops the marker
    // 1.2 -> 0.8 over 150ms, above 180 HP with an expanding flash. New-style (marker) skins darken bar and marker
    // white -> black below 100 HP and black -> red below 40; old ones swap ki/kidanger/kidanger2 there.
    double hpShown, hpLast = double.NaN, hpPulseAt = double.MinValue, hpFlashAt = double.MinValue;
    bool hpFilling = true;

    // stable's intro fill: 0.02 per ms, but always full by the first object (rate 200 / (first + lead-in) when it comes
    // within 10s); it starts at -lead-in, or 10s before a late first object.
    double HpFillRate => objs[0].Time * 0.02 < 200 ? 200.0 / Math.Max(1, objs[0].Time + map.AudioLeadIn) : 0.02;
    double HpFillStart => map.AudioLeadIn > 0 ? -map.AudioLeadIn : objs[0].Time * 0.02 >= 200 && objs[0].Time > 10000 ? objs[0].Time - 10000 : 0;

    void UpdateHealthShown()
    {
        double now = Game.NowMs, dt = GetFrameTime() * 1000, actual = health.Value;
        if (!double.IsNaN(hpLast) && actual > hpLast + 1e-9)
        {
            hpPulseAt = now;
            if (actual > 180) hpFlashAt = now;
        }
        hpLast = actual;
        if (hpShown < actual)
        {
            if (hpFilling && recovering) hpShown = Math.Min(200, hpShown + 0.08 * dt);
            else if (hpFilling)
            {
                if (time > HpFillStart)
                {
                    hpShown = Math.Min(200, hpShown + HpFillRate * dt);
                    if (now - hpPulseAt >= 150) hpPulseAt = now;
                }
            }
            else hpShown = Math.Min(200, hpShown + Math.Abs(actual - hpShown) / 4 * dt / (1000 / 60.0));
        }
        else if (hpShown > actual)
        {
            hpFilling = false;
            hpShown = Math.Max(0, hpShown - Math.Abs(hpShown - actual) / 6 * dt / (1000 / 60.0));
        }
    }

    void DrawHealthBar(int w, float s)
    {
        UpdateHealthShown();
        float frac = (float)(hpShown / Health.Max);
        // stable's osu!mania: the stage's own bar, standing up its right edge (the scorebar turned a quarter
        // anticlockwise at 0.7, its back 1 unit past the stage, the fill 8 (6.6 new-style) past and 2 (5.2) above the
        // bottom), plain white, no marker
        if (mode == 3 && mLook is { } ml && skin.AnimFrame("scorebar-colour", Game.NowMs) is { } mc)
        {
            float u = Game.H / 480f, right = (ml.Start + ml.Width) * u, sc = 0.7f * s, ma = hudFade;
            if (skin.Tex("scorebar-bg") is { } mb)
                DrawTexturePro(mb.Tex, new Rectangle(0, 0, mb.Tex.Width, mb.Tex.Height), new Rectangle(right + u, Game.H, mb.Width * sc, mb.Height * sc), Vector2.Zero, -90, ColorAlpha(Color.White, ma));
            bool newStyle = skin.Tex("scorebar-marker") != null;
            float fx = right + (newStyle ? 6.6f : 8) * u, fy = (newStyle ? 474.8f : 478) * u;
            DrawTexturePro(mc.Tex, new Rectangle(0, 0, mc.Tex.Width * frac, mc.Tex.Height), new Rectangle(fx, fy, mc.Width * sc * frac, mc.Height * sc), Vector2.Zero, -90, ColorAlpha(Color.White, ma));
            return;
        }
        if (skin.AnimFrame("scorebar-colour", Game.NowMs) is { } colour)
        {
            float bq = (float)Math.Clamp((Game.NowMs - hudBreakAt) / 500, 0, 1);
            float ba = hudBreak ? 1 - bq : bq, by = -20 * Game.H / 480f * (hudBreak ? bq : 1 - bq);
            float markerBreak = hudBreak ? 1 + 0.6f * bq : 1.6f - 0.6f * bq;
            ba *= hudFade;
            if (ba <= 0) return;
            skin.Tex("scorebar-bg")?.Draw(new Vector2(skin.Tex("scorebar-bg")!.Value.Width * s / 2, skin.Tex("scorebar-bg")!.Value.Height * s / 2 + by), s, ColorAlpha(Color.White, ba));
            // New-style skins (with scorebar-marker) place the fill differently from old ones.
            bool newStyle = skin.Tex("scorebar-marker") != null;
            var tint = Color.White;
            bool additive = newStyle, coloured = newStyle && (!hpFilling || HpFillStart > 0); // stable leaves the bar white while it fills
            if (coloured && hpShown < 40) { tint = ColorLerp(Color.Black, Xna.Red, (float)((40 - hpShown) / 40)); additive = false; }
            else if (coloured && hpShown < 100) { tint = ColorLerp(Color.White, Color.Black, (float)((100 - hpShown) / 100)); additive = false; }
            if (Game.NowMs - hpRecoverAt < 400) tint = ColorLerp(Xna.Red, Color.White, (float)((Game.NowMs - hpRecoverAt) / 400)); // (an Easy life used)
            var at = (newStyle ? new Vector2(12f, 12.5f) * s : new Vector2(5f, 16f) * s) + new Vector2(0, by);
            tint = ColorAlpha(tint, ba);
            var tex = colour.Tex;
            float fw = colour.Width * s * frac, fh = colour.Height * s;
            DrawTexturePro(tex, new Rectangle(0, 0, tex.Width * frac, tex.Height), new Rectangle(at.X, at.Y, fw, fh), Vector2.Zero, 0, tint);
            var tip = new Vector2(at.X + fw, at.Y + fh / 2);
            var marker = newStyle ? skin.Tex("scorebar-marker")
                : hpShown < 40 ? skin.Tex("scorebar-kidanger2") ?? skin.Tex("scorebar-ki")
                : hpShown < 100 ? skin.Tex("scorebar-kidanger") ?? skin.Tex("scorebar-ki")
                : skin.Tex("scorebar-ki");
            if (marker is not { } mk || newStyle && coloured && hpShown < 40) return; // stable hides the new-style marker below 40 HP (once the bar is coloured)
            double now = Game.NowMs;
            float pulse = now - hpPulseAt < 150 ? 1.2f - 0.4f * (float)((now - hpPulseAt) / 150) : 1;
            if (additive) BeginBlendMode(BlendMode.Additive);
            mk.Draw(tip, s * pulse * markerBreak, tint);
            double f = (now - hpFlashAt) / 120;
            if (f < 1) // the flash grows 1 -> 2 (additive, from half alpha) or 1 -> 1.6, eased out
            {
                float e = (float)Easing.Apply(1, f);
                mk.Draw(tip, s * (additive ? 1 + e : 1 + 0.6f * e), ColorAlpha(tint, tint.A / 255f * (additive ? 0.5f : 1) * (1 - e)));
            }
            if (additive) EndBlendMode();
            return;
        }
        float barW = w * 0.35f;
        DrawRectangleRec(new Rectangle(20, 16, barW, 14), new Color(0, 0, 0, 160));
        var hpColour = frac > 0.5f ? Color.White : frac > 0.25f ? new Color(255, 200, 80, 255) : new Color(255, 70, 70, 255);
        DrawRectangleRec(new Rectangle(22, 18, (barW - 4) * frac, 10), hpColour);
    }

    // stable's grade rules.
    string Grade() => GradeFor(mode, mods, n300, n100, n50, nMiss, nGeki, nKatu, OldModeRules);

    internal static string GradeFor(int mode, Mods mods, int n300, int n100, int n50, int nMiss, int nGeki, int nKatu, bool oldRules = false)
    {
        int total = n300 + n100 + n50 + nMiss;
        double Accuracy = AccuracyFor(mode, n300, n100, n50, nMiss, nGeki, nKatu, mods.HasFlag(Mods.ScoreV2) && !oldRules);
        if (mode == 3) // mania: by accuracy
        {
            double acc = Accuracy / 100;
            bool silv = (mods & (Mods.Hidden | Mods.Flashlight | Mods.FadeIn)) != 0; // (stable: Fade In too)
            return acc >= 1 ? silv ? "XH" : "X" : acc > 0.95 ? silv ? "SH" : "S" : acc > 0.9 ? "A" : acc > 0.8 ? "B" : acc > 0.7 ? "C" : "D";
        }
        if (mode == 2) // catch: by accuracy
        {
            double acc = Accuracy / 100;
            bool silv = (mods & (Mods.Hidden | Mods.Flashlight | Mods.FadeIn)) != 0; // (stable: Fade In too)
            return acc >= 1 ? silv ? "XH" : "X" : acc > 0.98 ? silv ? "SH" : "S" : acc > 0.94 ? "A" : acc > 0.9 ? "B" : acc > 0.85 ? "C" : "D";
        }
        if (total == 0) return "D";
        double r300 = n300 / (double)total, r50 = n50 / (double)total;
        bool silver = (mods & (Mods.Hidden | Mods.Flashlight | Mods.FadeIn)) != 0; // (stable: Fade In too)
        if (n300 == total) return silver ? "XH" : "X";
        if (r300 > 0.9 && r50 < 0.01 && nMiss == 0) return silver ? "SH" : "S";
        if (r300 > 0.8 && nMiss == 0 || r300 > 0.9) return "A";
        if (r300 > 0.7 && nMiss == 0 || r300 > 0.8) return "B";
        return r300 > 0.6 ? "C" : "D";
    }

    // ---- stable's pause/fail menu: buttons centred at -100/10/120 (640x480 units), grow to 1.1x on hover over
    // 120ms, flash on click. Keys still work: Esc continue, R retry, Q back (fail: Enter/Esc back).
    double menuShownAt;
    int pauseLoop; // BASS channel of the looping pause music while paused
    readonly float[] menuHover = new float[3];
    int menuHovered = -1;
    (string Name, double At) menuFlash = ("", double.MinValue);

    string[] MenuButtons => failed ? ["pause-retry", "pause-back"] : ["pause-continue", "pause-retry", "pause-back"];

    static float MenuY(string name) => name switch { "pause-continue" => -100, "pause-retry" => 10, _ => 120 };

    Rectangle MenuRect(string name)
    {
        int w = Game.W, h = Game.H;
        if (skin.Tex(name) is not { } sp) return default;
        float sw = sp.Width * h / 768f, sh = sp.Height * h / 768f;
        return new Rectangle(w / 2f - sw / 2, h / 2f + MenuY(name) * h / 480f - sh / 2, sw, sh);
    }

    // Returns the button clicked this frame (mouse or either game key), or null.
    // stable's keyboard pause / fail menu: Up / Down pick a button (the first press: Up the last one, Down the first
    // there is), Enter / Space / Skip press it. Arrows (the skin's arrow-pause, else play-warningarrow in blue) flank it
    // 170 units either side at 0.8: sliding in from 170 further out over 300ms (In) as they fade in, then swaying 45 out
    // and back (300ms Out, 300ms In) for as long as they show; they glide to a new pick over 100ms (Out).
    int menuSel = -1; // stable's numbering: 0 continue, 1 retry, 2 back
    double menuSelAt, menuSelMovedAt = double.MinValue, menuSelFor = double.NaN;
    float menuSelFromY;

    string? UpdateMenuKeys()
    {
        if (menuShownAt != menuSelFor) { menuSel = -1; menuSelFor = menuShownAt; }
        int d = Input.IsKeyPressed(KeyboardKey.Down) ? 1 : Input.IsKeyPressed(KeyboardKey.Up) ? -1 : 0;
        if (d != 0)
        {
            if (menuSel < 0) { menuSel = d < 0 ? 2 : failed ? 1 : 0; menuSelAt = Game.NowMs; menuSelMovedAt = double.MinValue; }
            else
            {
                menuSelFromY = MenuArrowY();
                menuSelMovedAt = Game.NowMs;
                menuSel = failed ? (menuSel + d + 1) % 2 + 1 : (menuSel + d + 3) % 3;
                (skin.Sound("pause-hover") ?? skin.Sound("menuclick"))?.Play(BeatmapSamples.EffectVolume);
            }
        }
        bool press = Input.IsKeyPressed(KeyboardKey.Enter) || Input.IsKeyPressed(KeyboardKey.Space) || Input.IsKeyPressed(Config.Key("keySkip", KeyboardKey.Space));
        if (menuSel < 0 || !press) return null;
        string name = MenuSelName;
        skin.Sound(name + "-click")?.Play(BeatmapSamples.EffectVolume);
        menuFlash = (name, Game.NowMs);
        return name;
    }

    string MenuSelName => menuSel switch { 0 => "pause-continue", 1 => "pause-retry", _ => "pause-back" };

    float MenuArrowY()
    {
        int h = Game.H;
        float to = h / 2f + MenuY(MenuSelName) * h / 480f;
        double t = (Game.NowMs - menuSelMovedAt) / 100;
        return t >= 1 ? to : menuSelFromY + (to - menuSelFromY) * (float)Easing.Apply(1, t);
    }

    void DrawMenuArrows(float fade)
    {
        if (menuSel < 0) return;
        var own = skin.Tex("arrow-pause");
        if ((own ?? skin.Tex("play-warningarrow")) is not { } arrow) return;
        int w = Game.W, h = Game.H;
        float u = h / 480f;
        double t = Game.NowMs - menuSelAt;
        float dx; // units outwards
        if (t < 300) dx = 170 * (1 - (float)Easing.Apply(2, t / 300));
        else
        {
            double p = (t - 300) % 600;
            dx = p < 300 ? 45 * (float)Easing.Apply(1, p / 300) : 45 * (1 - (float)Easing.Apply(2, (p - 300) / 300));
        }
        var tint = ColorAlpha(own != null ? Color.White : new Color(0, 114, 255, 255), (float)Math.Min(1, t / 300) * fade);
        float y = MenuArrowY(), sc = 0.8f * h / 768f;
        arrow.Draw(new Vector2(w / 2f - (170 + dx) * u, y), sc, tint);
        arrow.Draw(new Vector2(w / 2f + (170 + dx) * u, y), sc, tint, flipX: true);
    }

    string? UpdateMenu(double dtMs)
    {
        var mouse = Input.GetMousePosition();
        var names = MenuButtons;
        bool click = Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsKeyPressed(key1) || Input.IsKeyPressed(key2);
        int hovered = -1;
        for (int i = 0; i < names.Length; i++)
        {
            bool over = CheckCollisionPointRec(mouse, MenuRect(names[i]));
            if (over) hovered = i;
            menuHover[i] = Math.Clamp(menuHover[i] + (float)(over ? dtMs : -dtMs) / 120, 0, 1);
        }
        if (hovered != menuHovered && hovered >= 0) (skin.Sound("pause-hover") ?? skin.Sound("menuclick"))?.Play(BeatmapSamples.EffectVolume);
        menuHovered = hovered;
        if (!click || hovered < 0) return null;
        string name = names[hovered];
        skin.Sound(name + "-click")?.Play(BeatmapSamples.EffectVolume);
        menuFlash = (name, Game.NowMs);
        return name;
    }

    void DrawPauseMenu(bool fail)
    {
        int w = Game.W, h = Game.H;
        float s = h / 768f;
        // the Visual Settings panel fades the whole menu (darkening included) out as it opens
        float fade = (float)Math.Clamp((Game.NowMs - menuShownAt) / 300, 0, 1) * MenuAlpha;
        DrawRectangle(0, 0, w, h, ColorAlpha(Color.Black, (skin.Version > 1 ? 0.8f : 0.95f) * fade));
        var centre = new Vector2(w / 2f, h / 2f);
        if (skin.Tex(fail ? "fail-background" : "pause-overlay") is { } bg)
            bg.Draw(centre, fail ? h / bg.Height : s, ColorAlpha(Color.White, fade));
        var names = MenuButtons;
        for (int i = 0; i < names.Length; i++)
        {
            if (skin.Tex(names[i]) is not { } sp) continue;
            float e = 1 - (1 - menuHover[i]) * (1 - menuHover[i]); // ease out
            var pos = centre + new Vector2(0, MenuY(names[i]) * h / 480f);
            sp.Draw(pos, s * (1 + 0.1f * e), ColorAlpha(Color.White, fade));
            float flash = (float)((Game.NowMs - menuFlash.At) / 400);
            if (menuFlash.Name == names[i] && flash < 1)
            {
                BeginBlendMode(BlendMode.Additive);
                sp.Draw(pos, s * (1 + 0.1f * e) * (1 + 0.01f * flash), ColorAlpha(Color.White, 1 - flash));
                EndBlendMode();
            }
        }
        DrawMenuArrows(fade);
    }

    // ---- skip button: the skin's play-skip, bottom-right, 60% alpha brightening on hover; fades in over 400ms.
    readonly CachedText<int> bonusText = new(v => v.ToString());
    double skipShownAt = double.NaN;
    int skipFrames = -1;

    Sprite? SkipSprite()
    {
        if (skipFrames < 0) skipFrames = skin.FrameCount("play-skip");
        if (skipFrames == 0) return null;
        double frameMs = skin.AnimationFramerate > 0 ? 1000 / skin.AnimationFramerate : 1000.0 / skipFrames;
        return skin.Frame("play-skip", (int)(Game.NowMs / frameMs) % skipFrames);
    }

    Rectangle? SkipRect()
    {
        if (SkipSprite() is not { } sp) return null;
        int w = Game.W, h = Game.H;
        float sw = sp.Width * h / 768f, sh = sp.Height * h / 768f;
        return new Rectangle(w - sw, h - sh, sw, sh);
    }

    void DrawSkip()
    {
        if (!CanSkip || finished || paused || failed) { skipShownAt = double.NaN; return; }
        if (double.IsNaN(skipShownAt)) skipShownAt = Game.NowMs;
        if (SkipSprite() is not { } sp || SkipRect() is not { } r)
        {
            Game.Text("Press Space to skip", Game.W - 360, Game.H - 120, 36, Color.White);
            return;
        }
        float a = (float)Math.Clamp((Game.NowMs - skipShownAt) / (outroSkip ? 300 : 400), 0, 1); // stable: the outro skip fades in faster
        bool over = playback == null && CheckCollisionPointRec(Input.GetMousePosition(), r);
        sp.Draw(new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2), Game.H / 768f, ColorAlpha(Color.White, a * (over ? 1 : 0.6f)));
    }
}
