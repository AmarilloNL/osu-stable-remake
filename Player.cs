using System.Diagnostics;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace StableRemake;

sealed class ObjState
{
    public bool Done;
    public int Result = -1;
    public double JudgeTime;
    public bool HeadDone, HeadHit;
    public double HeadTime;
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
    public double Rpm, ClearAt = double.NaN, BonusAt = double.NaN;
    public int Bonus;
}

sealed partial class Player : Screen
{
    readonly Beatmap map;
    readonly SongSelect back;
    readonly List<HitObject> objs;
    readonly ObjState[] st;
    readonly Vector2[][] bodies;
    readonly bool snaking = Config.GetBool("SnakingSliders", true);
    readonly (Vector2 Pos, float Rotation, double Time)[] followPoints;
    readonly int fpFrames;
    readonly double fpFrameMs;
    int fpFirst;
    readonly BeatmapSamples samples;
    readonly Skin skin = Game.Skin;
    Color[] colours; // per-map Skin toggle can swap these (VisualSettings.cs)

    // Clock: a Stopwatch that is continuously (and gently) pulled towards the audio position.
    readonly Stopwatch sw = new();
    double clockOrigin, time, clockFloor = double.NegativeInfinity;
    readonly Track? music;
    double driftSum, driftMax;
    long driftCount, snaps;
    bool musicStarted, musicEnded, paused, finished;
    readonly Storyboard? storyboard;
    bool wasInBreak;
    Texture2D bg;
    RenderTexture2D sliderRt;

    readonly KeyboardKey key1, key2;
    readonly bool mouseButtons;
    Vector2 cursor;
    float scale;
    Vector2 origin;
    int firstActive;

    long score;
    int combo, maxCombo, n300, n100, n50, nMiss, nGeki, nKatu;
    internal readonly List<Vector2> hpGraph = []; // (time, hp 0..1) for the results screen
    readonly double diffMul;
    readonly List<double> hitErrors = [];
    readonly List<(Vector2 Pos, double Time, int Result)> popups = [];
    readonly List<int> visible = [];
    double offsetShownUntil = double.MinValue;

    static double Offset => Config.GetDouble("RemakeOffset", 0);

    readonly Mods mods;
    readonly bool auto, relax, autopilot;
    readonly double rate, modMul;
    readonly Health health;
    bool failed;
    int comboWorst = 300;
    double lastTime = double.NaN;
    int autoNext;
    double autoReleaseAt;

    // Replays: recording during live play, or playback when watching.
    readonly ReplayFrame[]? playback;
    readonly ReplayInfo? watching;
    readonly List<ReplayFrame> recording = [];
    ReplayInfo? savedReplay;
    int pbIndex;
    byte inputKeys;
    // stable's left/right button states (K1/M1 = left 1, K2/M2 = right 2) at the last three changes, and the
    // buttons newly down this update; slider tracking depends on them (ButtonHolds).
    int btnCur, btnPrev, btnPrev2, btnNew;
    int pressButton = 1; // the button (1 left, 2 right) of the press being handled; a slider head takes it (stable)
    Vector2 pbCursor = new(256, 192);
    Vector2 lastRecorded = new(float.NaN);

    public Player(Beatmap map, SongSelect back, Mods mods, ReplayFrame[]? playback = null, ReplayInfo? watching = null)
    {
        if (!VirtualClock) Beatmap.PlayfieldHeight = GetScreenHeight(); // headless runs keep stable's configured height
        this.playback = playback;
        this.watching = watching;
        this.map = map;
        this.back = back;
        this.mods = mods;
        auto = mods.HasFlag(Mods.Autoplay);
        relax = mods.HasFlag(Mods.Relax);
        autopilot = mods.HasFlag(Mods.Autopilot);
        rate = mods.Rate();
        modMul = mods.ScoreMultiplier();
        health = new Health(map, mods.HasFlag(Mods.HalfTime));
        objs = map.Objects;
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

        var fps = new List<(Vector2, float, double)>();
        for (int i = 1; i < objs.Count; i++)
        {
            HitObject a = objs[i - 1], b = objs[i];
            if (b.ComboNumber == 1 || a.Type == ObjType.Spinner || b.Type == ObjType.Spinner) continue;
            Vector2 from = a.EndPos + map.StackOffset(a), to = PosOf(b);
            float dist = Vector2.Distance(from, to);
            float rot = MathF.Atan2(to.Y - from.Y, to.X - from.X) * 180 / MathF.PI;
            for (float d = 48; d < dist - 32; d += 32)
                fps.Add((Vector2.Lerp(from, to, d / dist), rot, a.EndTime + (b.Time - a.EndTime) * d / dist));
        }
        followPoints = fps.ToArray();
        while (skin.Frame("followpoint", fpFrames) != null) fpFrames++;
        fpFrameMs = skin.AnimationFramerate > 0 ? 1000 / skin.AnimationFramerate : 1000.0 / Math.Max(1, fpFrames);

        key1 =Config.Key("keyOsuLeft", KeyboardKey.Z);
        key2 = Config.Key("keyOsuRight", KeyboardKey.X);
        mouseButtons = !Config.GetBool("MouseDisableButtons", false);

        diffMul = map.DifficultyMultiplier;

        music = Track.Load(Path.Combine(map.Folder, map.AudioFilename), rate, mods.HasFlag(Mods.Nightcore), tempo: playback != null);
        if (music != null)
            music.Volume = Config.MusicVolume;

        float dimAlpha = 1 - LightestDim / 100f; // the storyboard can show through at the lightest dim reached
        if (!VirtualClock)
            try { storyboard = Storyboard.Load(map, textures: dimAlpha > 0); }
            catch (IOException e) { Console.Error.WriteLine($"storyboard not loaded: {e.Message}"); }
        if (dimAlpha > 0) LoadBackground();
        sliderRt = LoadRenderTexture(GetScreenWidth(), GetScreenHeight());
        if (!VirtualClock) ApplyToggles();
        SetupCountdown();

        clockOrigin = Math.Min(-map.AudioLeadIn, objs[0].Time - map.Preempt - 1000);
        playStart = clockOrigin;
        sw.Start();
    }

    // Background dim in percent, eased like stable (1/15 of the way per 60Hz tick) from the previous screen's
    // (none) to DimLevel, and 30 points lighter in the intro, breaks and outro unless IHateHavingFun is set.
    float bgDim;
    static float LightestDim => Math.Max(0, Config.GetInt("DimLevel", 80) - (Config.GetBool("IHateHavingFun", false) ? 0 : 30));

    void UpdateDim(double dtMs)
    {
        bool rest = time < objs[0].Time - map.Preempt || time > objs[^1].EndTime + map.Hit50 || InBreak(time);
        float target = rest ? LightestDim : Config.GetInt("DimLevel", 80);
        bgDim += (target - bgDim) * (1 - (float)Math.Pow(14 / 15.0, dtMs / (1000 / 60.0)));
    }

    // Map time: real time scaled by the mod rate (DT 1.5x, HT 0.75x), like stable, and the replay speed.
    double Raw => clockOrigin + (VirtualClock ? virtualElapsed : sw.Elapsed.TotalMilliseconds) * rate * speed;

    // Headless verification (--verify-osr): time advances only by the dt passed to Update, no audio.
    public static bool VirtualClock;
    static readonly bool VerifyLog = Environment.GetEnvironmentVariable("VERIFY_LOG") == "1"; // per-judgement log for --verify-osr
    double virtualElapsed;
    public (int N300, int N100, int N50, int NMiss, int MaxCombo, bool Done, double Mean) Result =>
        (n300, n100, n50, nMiss, maxCombo, finished || failed, HitStats().Mean);
    Vector2 ToScreen(Vector2 p) => origin + p * scale;
    Vector2 PosOf(HitObject o) => o.Pos + map.StackOffset(o);
    Vector2 BallOf(HitObject o, double t) => o.PositionAtTime(t) + map.StackOffset(o);
    bool CanSkip => objs[0].Time - time > 6000;
    int MapMaxCombo => objs.Sum(o => o.Type == ObjType.Slider ? 1 + o.Checkpoints.Count : 1);

    public void Dispose()
    {
        music?.Dispose();
        SoundPool.Stop(pauseLoop);
        if (bg.Id != 0) UnloadTexture(bg);
        storyboard?.Unload();
        UnloadRenderTexture(sliderRt);
        if (sliderGradient.Id != 0) UnloadTexture(sliderGradient);
        samples.Unload();
    }

    void Leave(Screen next)
    {
        Dispose();
        Game.Screen = next;
    }

    void Retry() => Leave(new Player(map, back, mods, playback, watching));

    void Fail()
    {
        if (failed || auto || finished) return;
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
    float failFreq, playfieldAlpha = 1, fallDown = 1, fallUp = 1;
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
            playfieldAlpha = Math.Max(0, playfieldAlpha - 0.007f);
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
            var tint = skin.Version > 1 ? Color.Red : Color.White; // stable reds the new-style arrow for v2+ skins
            for (int c = 0; c < 4; c++)
            {
                bool right = c >= 2;
                var at = new Vector2(right ? w - 80 * u : 80 * u, c % 2 == 0 ? 100 * u : h - 100 * u);
                arrow.Draw(at, sd, tint, flipX: right);
            }
        }
    }

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

    void Drain()
    {
        double delta = double.IsNaN(lastTime) ? 0 : time - lastTime;
        lastTime = time;
        if (delta <= 0 || time < objs[0].Time - map.Preempt || time > objs[^1].EndTime || InBreak(time)) return;
        health.Add(-health.DropRate * delta * (InSpinner(time) ? 0.25 : 1)); // stable: a quarter of the drain during spinners
        if (health.Value <= 0 && !mods.HasFlag(Mods.NoFail)) Fail();
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
    internal void ApplyMusicVolume() { if (music != null) music.Volume = Config.MusicVolume; }

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
        else { sw.Start(); if (musicStarted) music?.Play(); SoundPool.Stop(pauseLoop); pauseLoop = 0; }
    }

    // ---- stable's resume: live play outside breaks only continues once you click the orange ring left where your
    // cursor was when you paused (so the cursor can't jump), with a centred hint. Auto/replays/relax resume at once.
    bool resumePending;
    Vector2 pauseCursor;
    double resumeShownAt;
    static readonly Color Orange = new(255, 165, 0, 255);

    void Continue()
    {
        if (playback == null && !auto && !relax && !autopilot && !InBreak(time))
        {
            resumePending = true;
            resumeShownAt = Game.NowMs;
        }
        else SetPaused(false);
    }

    void UpdateResume()
    {
        float u = GetScreenHeight() / 480f;
        bool over = Vector2.Distance(Input.GetMousePosition(), ToScreen(pauseCursor)) <= 12 * u; // 1.2 x 20 units / 2
        if (over && (Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsKeyPressed(key1) || Input.IsKeyPressed(key2)))
        {
            resumePending = false;
            SetPaused(false);
        }
    }

    void DrawResume()
    {
        int w = GetScreenWidth(), h = GetScreenHeight();
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
    }

    void Skip()
    {
        double target = objs[0].Time - 2500;
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
        int w = GetScreenWidth(), h = GetScreenHeight();
        scale = h / 480f;
        origin = new Vector2((w - 512 * scale) / 2, (h - 384 * scale) / 2 + 8 * scale);

        // The results screen takes over (and disposes us later), so the outro keeps playing like stable.
        if (finished) { Game.Screen = new Results(this); return; }
        bool visualClick = UpdateVisualSettings(dtMs);
        if (failed)
        {
            if (double.IsPositiveInfinity(menuShownAt)) { UpdateFailAnim(dtMs); return; }
            string? f = visualClick ? null : UpdateMenu(dtMs);
            if (f == "pause-back" || Input.IsKeyPressed(KeyboardKey.Enter) || Input.IsKeyPressed(KeyboardKey.Escape)) Leave(back);
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
        if (!VirtualClock) { UpdateMapWarning(); UpdateDim(dtMs); }
        if (playback != null && TestSpeedAt.Count > 0 && Game.NowMs / 1000 >= TestSpeedAt[0]) { TestSpeedAt.RemoveAt(0); CycleSpeed(); }
        if (playback != null && (Input.IsKeyPressed(KeyboardKey.Escape) || UpdateReplayOverlay())) { Leave(back); return; }
        if (Input.IsKeyPressed(KeyboardKey.Escape))
        {
            if (!paused) SetPaused(true);
            else if (resumePending) resumePending = false; // back to the pause menu
            else Continue();
        }
        else if (paused && resumePending) UpdateResume();
        else if (paused)
        {
            string? c = visualClick ? null : UpdateMenu(dtMs);
            if (c == "pause-continue") { Continue(); return; } // not into gameplay input this frame
            else if (c == "pause-retry" || Input.IsKeyPressed(KeyboardKey.R)) Retry();
            else if (c == "pause-back" || Input.IsKeyPressed(KeyboardKey.Q)) Leave(back);
        }
        if (paused) return;
        if (Input.IsKeyPressed(KeyboardKey.Grave)) { Retry(); return; }

        int offsetStep = Input.IsKeyDown(KeyboardKey.LeftShift) ? 1 : 5;
        if (Input.IsKeyPressed(KeyboardKey.Equal) || Input.IsKeyPressed(KeyboardKey.KpAdd)) ChangeOffset(offsetStep);
        if (Input.IsKeyPressed(KeyboardKey.Minus) || Input.IsKeyPressed(KeyboardKey.KpSubtract)) ChangeOffset(-offsetStep);
        if (CanSkip && (Input.IsKeyPressed(KeyboardKey.Space) || Input.IsMouseButtonPressed(MouseButton.Left) && SkipRect() is { } sr && CheckCollisionPointRec(Input.GetMousePosition(), sr)))
        {
            skin.Sound("menuhit")?.Play(BeatmapSamples.EffectVolume);
            Skip();
        }

        if (VirtualClock) virtualElapsed += dtMs;
        UpdateClock(dtMs);
        // Replays are judged in 1ms steps of map time whatever the frame rate (like --verify-osr), so watching at
        // 60fps gives the same result as at 1000fps. Seeks (skip) jump straight through; nothing is judged there.
        // Only whole milliseconds are judged: a frame shorter than 1ms judges nothing and a longer one judges every
        // ms it covered, so the number of updates (sliders judge one checkpoint per update) never depends on fps.
        // Auto too (stable's is a replay): a stutter would otherwise press late (100s) and drop slider ticks.
        if ((playback != null || auto) && !double.IsNaN(simTime) && time - simTime < 2000)
        {
            double target = time;
            for (double t = Math.Floor(simTime) + 1; t <= Math.Floor(target) && !finished; t++)
            {
                time = t;
                Step(1);
                while (sameTimePending && !finished) { sameTimePending = false; Step(0); }
                simTime = t;
            }
            time = target;
        }
        else
        {
            if (!finished) Step(dtMs);
            simTime = time;
        }
    }

    double simTime = double.NaN;
    bool sameTimePending; // PlaybackInput stopped before a replay frame with the same time as the last one

    // One step of game logic at the current time.
    void Step(double dtMs)
    {
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
        Drain();
        UpdateBreakSection();
        UpdateCountdown();
        if (playback == null) AdvanceSpinners(time); // live/auto: up to now on the previous frame's input
        cursor = auto || autopilot ? AutoCursor() : playback != null ? pbCursor : (Input.GetMousePosition() - origin) / scale;

        // Replay presses happened at earlier (recorded) times within this frame: apply them before expiring
        // anything at the current time, or a press that hit live would find its object already missed.
        if (playback != null) PlaybackInput();
        MissExpired();

        bool held;
        if (auto)
        {
            // Press every circle/slider head exactly when its time is reached, cursor on it.
            for (; autoNext < objs.Count && objs[autoNext].Time <= time; autoNext++)
            {
                var o = objs[autoNext];
                if (o.Type == ObjType.Spinner) continue;
                cursor = PosOf(o);
                HandlePress();
                autoReleaseAt = time + 50;
            }
            cursor = AutoCursor();
            held = time < autoReleaseAt;
            for (int i = firstActive; i < objs.Count && objs[i].Time <= time; i++)
                if (objs[i].Type != ObjType.Circle && time <= objs[i].EndTime) held = true;
        }
        else
        {
            if (playback == null) LiveInput();
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
        if (time >= objs[0].Time && time <= objs[^1].EndTime && (hpGraph.Count == 0 || time - hpGraph[^1].X >= (VirtualClock ? 10 : 250))) // verify-osr compares it with stable's life bar
            hpGraph.Add(new((float)time, (float)(health.Value / Health.Max)));

        while (firstActive < objs.Count && st[firstActive].Done && time > Math.Max(st[firstActive].JudgeTime, objs[firstActive].EndTime) + 1000)
            firstActive++;
        if (firstActive >= objs.Count && (musicEnded || music == null || time > objs[^1].EndTime + 3000))
        {
            finished = true;
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
        if (!autopilot) cursor = mouse;

        // Record only changes: frames then follow the tablet/mouse report rate, not the render rate.
        bool changed = keys != inputKeys || mouse != lastRecorded;
        if (changed)
        {
            recording.Add(new ReplayFrame(time, mouse.X, mouse.Y, keys));
            lastRecorded = mouse;
        }
        int newly = keys & ~inputKeys;
        inputKeys = keys;
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
        var pos = AutoCursor();
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
            int newly = f.Keys & ~inputKeys;
            inputKeys = f.Keys;
            if (StableReplay) foreach (var (so, ss) in ActiveSpinners(f.Time)) SpinnerInput(so, ss, f.Time);
            // stable runs an update per replay frame: frames sharing a time are judged one at a time, so stop here
            // and let the stepping loop run another (zero-length) update at this time for the next one.
            bool split = pbIndex < playback.Length && playback[pbIndex].Time == f.Time;
            if (newly == 0 || relax) { if (split) { sameTimePending = true; break; } continue; }
            time = f.Time;
            MissExpired();
            for (int b = 0; b < 4; b++)
                if ((newly >> b & 1) != 0) { pressButton = (b & 1) == 0 ? 1 : 2; HandlePress(); }
            time = now;
            if (split) { sameTimePending = true; break; }
        }
        AdvanceSpinners(now);
        // Stable replays store the cursor at ~60Hz, but stable judged slider tracking live at a much higher
        // rate. Interpolating between frames approximates the cursor it actually saw. (Our own replays record
        // every change, so they're played back as-is to stay exact.)
        if (watching is { FromStable: true } && !autopilot && pbIndex > 0 && pbIndex < playback.Length)
        {
            var a = playback[pbIndex - 1];
            var b2 = playback[pbIndex];
            if (b2.Time > a.Time)
            {
                pbCursor = Vector2.Lerp(new Vector2(a.X, a.Y), new Vector2(b2.X, b2.Y), (float)((now - a.Time) / (b2.Time - a.Time)));
                cursor = pbCursor;
            }
        }
    }

    ReplayInfo CurrentInfo() => new()
    {
        BeatmapMd5 = Replays.Md5Of(map.File), Artist = map.Artist, Title = map.Title, Version = map.Version,
        Mods = mods, Score = score, MaxCombo = maxCombo, N300 = n300, N100 = n100, N50 = n50, NMiss = nMiss,
        Accuracy = Accuracy, Date = DateTime.Now,
    };

    void SaveReplay()
    {
        if (auto || playback != null || recording.Count == 0) return;
        try
        {
            savedReplay = Replays.Save(CurrentInfo(), recording);
            back.AddScore(savedReplay);
        }
        catch (IOException e) { Console.Error.WriteLine($"replay not saved: {e.Message}"); }
    }

    // stable's fail screen keys for a live play: F1 watches the attempt, F2 saves its replay.
    bool LiveFail => failed && playback == null && !auto && recording.Count > 0;

    void FailReplayKeys()
    {
        if (!LiveFail) return;
        if (Input.IsKeyPressed(KeyboardKey.F1)) Leave(new Player(map, back, mods, recording.ToArray(), CurrentInfo()));
        else if (Input.IsKeyPressed(KeyboardKey.F2) && !failSaved)
            try { Notification.Show($"Saved replay to {Replays.Save(CurrentInfo(), recording, "Failed").Path}", 3000); failSaved = true; }
            catch (IOException e) { Notification.Show($"Replay not saved: {e.Message}", 3000); }
    }
    bool failSaved;

    Vector2 AutoCursor()
    {
        var centre = new Vector2(256, 192);
        Vector2 prevPos = centre;
        double prevEnd = objs[0].Time - map.Preempt;
        for (int i = Math.Max(0, firstActive - 1); i < objs.Count; i++)
        {
            var o = objs[i];
            if (o.EndTime < time) { prevPos = o.Type == ObjType.Spinner ? centre : o.EndPos + map.StackOffset(o); prevEnd = o.EndTime; continue; }
            if (time >= o.Time)
                return o.Type switch
                {
                    ObjType.Slider => BallOf(o, time),
                    ObjType.Spinner => centre + 50 * new Vector2(MathF.Cos((float)time * 0.05f), MathF.Sin((float)time * 0.05f)),
                    _ => PosOf(o),
                };
            var target = o.Type == ObjType.Spinner ? centre : PosOf(o);
            float f = (float)Math.Clamp((time - prevEnd) / Math.Max(1, o.Time - prevEnd), 0, 1);
            f = f * f * (3 - 2 * f); // smoothstep
            return Vector2.Lerp(prevPos, target, f);
        }
        return prevPos;
    }

    void ChangeOffset(int delta)
    {
        Config.SetOwn("RemakeOffset", (Offset + delta).ToString(System.Globalization.CultureInfo.InvariantCulture));
        offsetShownUntil = Game.NowMs + 1500;
    }

    void MissExpired()
    {
        for (int i = firstActive; i < objs.Count && objs[i].Time - map.Preempt <= time; i++)
        {
            var o = objs[i];
            var s = st[i];
            if (time <= o.Time + map.Hit50) continue;
            if (o.Type == ObjType.Circle && !s.Done) Final(i, 0, PosOf(o));
            else if (o.Type == ObjType.Slider && !s.HeadDone)
            {
                s.HeadDone = true;
                if (VerifyLog) Console.Error.WriteLine($"  slider head #{i} at {o.Time} expired unclicked (cursor {Vector2.Distance(cursor, PosOf(o)) / map.Radius:0.00}r)");
                BreakCombo();
                health.Apply(HpEvent.SliderBreak);
            }
        }
    }

    // A slider shorter than its head's 50 window reaches its end before the head expires: an unhit head is a
    // missed head (combo break) all the same.
    void MissUnclickedHead(int i)
    {
        var s = st[i];
        if (s.HeadDone) return;
        s.HeadDone = true;
        if (VerifyLog) Console.Error.WriteLine($"  slider head #{i} at {objs[i].Time} unclicked when the slider ended");
        BreakCombo();
        health.Apply(HpEvent.SliderBreak);
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
            if (result > 0) PlayHitsound(o.HitSound, o.NormalSet, o.AdditionSet, o.SampleIndex, o.Volume, o.Time);
            Final(i, result, PosOf(o));
        }
        else if (result == 0)
        {
            s.HeadDone = true; s.TrackButton = pressButton; // clicked too early/late: the head is missed
            if (VerifyLog) Console.Error.WriteLine($"  slider head #{i} at {o.Time} clicked outside the windows at {time:0}");
            BreakCombo();
            health.Apply(HpEvent.SliderBreak);
        }
        else
        {
            s.HeadDone = true; s.HeadHit = true; s.HeadTime = time; s.TrackButton = pressButton;
            s.CheckpointsHit++;
            score += 30;
            combo++;
            maxCombo = Math.Max(maxCombo, combo);
            health.Apply(HpEvent.SliderPoint);
            PlayEdge(o, 0);
        }
    }

    // Every object's final judgement goes through here: counts, score, combo, HP, SD/PF and combo-end bonus.
    void Final(int i, int result, Vector2 popupPos)
    {
        var o = objs[i];
        var s = st[i];
        s.TrackedAtEnd = s.Tracking;
        s.Done = true; s.Result = result; s.JudgeTime = time; s.Tracking = false;
        if (VerifyLog && result != 300) Console.Error.WriteLine($"  {result} on {o.Type} #{i} at {o.Time} (judged at {time:0}){(o.Type == ObjType.Slider ? $" head {(s.HeadHit ? "hit" : "missed")}, {s.CheckpointsHit}/{1 + o.Checkpoints.Count} points" : "")}");
        Count(result);
        popups.Add((popupPos, time, result));
        if (result > 0)
        {
            ScoreHit(result);
            storyboard?.Fire(time, "HitObjectHit");
            if (o.Type != ObjType.Slider) { combo++; maxCombo = Math.Max(maxCombo, combo); } // slider combo comes from its checkpoints
            health.Apply(result switch { 300 => HpEvent.Hit300, 100 => HpEvent.Hit100, _ => HpEvent.Hit50 });
            if (result != 300 && mods.HasFlag(Mods.Perfect)) Fail();
        }
        else
        {
            if (o.Type != ObjType.Slider) BreakCombo(); // a fully missed slider already broke combo at its head
            health.Apply(HpEvent.Miss);
            if (mods.HasFlag(Mods.SuddenDeath)) Fail();
        }
        comboWorst = Math.Min(comboWorst, result);
        if (i == objs.Count - 1 || objs[i + 1].StartsCombo) // the next object starts a combo (spinners included)
        {
            health.Add(health.ComboEnd(comboWorst, lastHit: result > 0));
            if (comboWorst == 300) nGeki++;
            else if (comboWorst == 100) nKatu++;
            comboWorst = 300;
        }
    }

    void UpdateObjects(bool held, double dtMs)
    {
        for (int i = firstActive; i < objs.Count && objs[i].Time - map.Preempt <= time; i++)
        {
            var o = objs[i];
            var s = st[i];
            if (s.Done) continue;
            if (o.Type == ObjType.Slider) UpdateSlider(i, held);
            else if (o.Type == ObjType.Spinner) UpdateSpinner(i, dtMs);
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
                score += cp.Kind == CheckpointKind.Tick ? 10 : 30;
                combo++;
                maxCombo = Math.Max(maxCombo, combo);
                health.Apply(cp.Kind == CheckpointKind.Tick ? HpEvent.SliderTick : HpEvent.SliderPoint);
                if (cp.Kind == CheckpointKind.Tick)
                {
                    var tp = map.PointAt(cp.Time);
                    samples.Play(o.NormalSet > 0 ? o.NormalSet : tp.SampleSet, "slidertick", tp.SampleIndex, o.Volume > 0 ? o.Volume : tp.Volume);
                }
                else PlayEdge(o, cp.Kind == CheckpointKind.End ? o.Slides : cp.Span + 1);
            }
            else
            {
                if (VerifyLog) Console.Error.WriteLine($"  checkpoint miss: {cp.Kind} at {cp.Time} on slider {o.Time}-{o.EndTime} (cursor {Vector2.Distance(cursor, BallOf(o, time)) / map.Radius:0.00}r from ball, held={inputKeys != 0})");
                if (cp.Kind != CheckpointKind.End) BreakCombo(); // a missed slider end costs HP but keeps combo
                health.Apply(HpEvent.SliderBreak);
            }
        }

        if (time >= o.EndTime && s.NextCheckpoint >= o.Checkpoints.Count)
        {
            MissUnclickedHead(i);
            double frac = s.CheckpointsHit / (double)(1 + o.Checkpoints.Count);
            int result = frac >= 1 ? 300 : frac >= 0.5 ? 100 : frac > 0 ? 50 : 0;
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
            if (double.IsNaN(s.SimTime)) { s.SimTime = Math.Floor(o.Time); s.NextTick = o.Time; }
            double maxAccel = 8e-5 + Math.Max(0, (5000 - (o.EndTime - o.Time)) / 1000 / 2000);
            double end = Math.Min(t, o.EndTime);
            while (s.SimTime + 1 <= end)
            {
                s.SimTime += 1;
                if (!StableReplay && s.SimTime >= s.NextTick) { s.NextTick += TickMs; SpinnerInput(o, s, s.SimTime); }
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
        var v = cursor - new Vector2(256, 192);
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
        else if (auto) s.Velocity = 0.05;
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
        if ((int)s.Rotation <= s.SpinsCounted) return;
        s.SpinsCounted++;
        int req = SpinsRequired(o);
        if (s.SpinsCounted == req) s.ClearAt = s.SimTime;
        if (s.SpinsCounted > req + 3 && (s.SpinsCounted - (req + 3)) % 2 == 0)
        {
            s.BonusAt = s.SimTime;
            s.Bonus = 1000 * (s.SpinsCounted - (req + 3)) / 2;
            score += 1100;
            health.Apply(HpEvent.SpinnerBonus);
            skin.Sound("spinnerbonus")?.Play(BeatmapSamples.EffectVolume);
        }
        else if (s.SpinsCounted > 1 && s.SpinsCounted % 2 == 0)
        {
            score += 100;
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
        if (result > 0) PlayHitsound(o.HitSound, o.NormalSet, o.AdditionSet, o.SampleIndex, o.Volume, o.EndTime);
        Final(i, result, new Vector2(256, 192));
    }

    void ScoreHit(int value) => score += value + (long)(value * Math.Max(0, combo - 1) * diffMul * modMul / 25);

    void BreakCombo()
    {
        if (combo >= 20) skin.Sound("combobreak")?.Play(BeatmapSamples.EffectVolume);
        combo = 0;
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

    void PlayEdge(HitObject o, int edge)
    {
        int sound = edge < o.EdgeSounds.Length ? o.EdgeSounds[edge] : o.HitSound;
        var (normal, addition) = edge < o.EdgeSets.Length ? o.EdgeSets[edge] : (o.NormalSet, o.AdditionSet);
        PlayHitsound(sound, normal, addition, o.SampleIndex, o.Volume, o.Time + edge * o.SpanDuration);
    }

    void PlayHitsound(int bits, int normalSet, int additionSet, int index, int volume, double at)
    {
        var tp = map.PointAt(at + 5); // stable samples the timing point slightly ahead
        int set = normalSet > 0 ? normalSet : tp.SampleSet;
        int add = additionSet > 0 ? additionSet : set;
        int idx = index > 0 ? index : tp.SampleIndex;
        int vol = volume > 0 ? volume : tp.Volume;
        samples.Play(set, "hitnormal", idx, vol);
        storyboard?.FireHitSound(time, set, add, bits, idx);
        if ((bits & 2) != 0) samples.Play(add, "hitwhistle", idx, vol);
        if ((bits & 4) != 0) samples.Play(add, "hitfinish", idx, vol);
        if ((bits & 8) != 0) samples.Play(add, "hitclap", idx, vol);
    }

    // ---------------------------------------------------------------- drawing

    // Loaded on first need: at start, or when the dim is lowered from 100% mid-play (Visual Settings).
    bool bgTried;

    void LoadBackground()
    {
        bgTried = true;
        var bgPath = Path.Combine(map.Folder, map.Background);
        // A storyboard that draws the background itself replaces it, like stable.
        if (map.Background.Length == 0 || !File.Exists(bgPath) || storyboard?.UsesFile(map.Background) == true) return;
        bg = LoadTexture(bgPath);
        if (bg.Id != 0) SetTextureFilter(bg, TextureFilter.Bilinear);
    }

    public override void Draw()
    {
        int w = GetScreenWidth(), h = GetScreenHeight();
        if (bg.Id == 0 && !bgTried) LoadBackground(); // even at 100% dim: it fades in from the previous screen
        if (bg.Id != 0)
        {
            float s = Math.Max(w / (float)bg.Width, h / (float)bg.Height);
            float dim = 1 - bgDim / 100f;
            DrawTexturePro(bg, new Rectangle(0, 0, bg.Width, bg.Height),
                new Rectangle((w - bg.Width * s) / 2, (h - bg.Height * s) / 2, bg.Width * s, bg.Height * s),
                Vector2.Zero, 0, ColorAlpha(Color.White, dim));
        }

        visible.Clear();
        for (int i = firstActive; i < objs.Count && objs[i].Time - map.Preempt <= time; i++) visible.Add(i);

        float sbDim = 1 - bgDim / 100f;
        bool showStoryboard = storyboard != null && Toggles.Storyboard;
        if (showStoryboard) storyboard!.Draw(time, w, h, sbDim, overlay: false);
        DrawFollowPoints();

        // Earlier objects on top: draw later ones first.
        for (int k = visible.Count - 1; k >= 0; k--)
        {
            int i = visible[k];
            if (Falling) PushFall(i);
            switch (objs[i].Type)
            {
                case ObjType.Circle: DrawHitCircle(i); break;
                case ObjType.Slider: DrawSlider(i); break;
                case ObjType.Spinner: DrawSpinner(i); break;
            }
            if (Falling) Rlgl.PopMatrix();
        }
        for (int k = visible.Count - 1; k >= 0; k--)
        {
            if (Falling) PushFall(visible[k]);
            DrawApproach(visible[k]);
            if (Falling) Rlgl.PopMatrix();
        }

        if (showStoryboard) storyboard!.Draw(time, w, h, sbDim, overlay: true);
        DrawPopups();
        DrawBreakOverlays(w, h);
        if (mods.HasFlag(Mods.Flashlight) && !finished) DrawFlashlight(w, h);
        DrawHud(w, h);
        if (paused && resumePending) DrawResume();
        else if (paused) DrawPauseMenu(fail: false);
        if (failed) DrawPauseMenu(fail: true);
        if (LiveFail) // stable: bold 16 with a shadow at (10, 10), fading in with the menu
        {
            float u = h / 480f, a = (float)Math.Clamp((Game.NowMs - menuShownAt) / 300, 0, 1);
            string[] hint = ["Press F1 to view replay", "Press F2 to save replay"];
            for (int i = 0; i < hint.Length; i++)
            {
                Game.TextBold(hint[i], 12 * u + 1, (10 + 16 * i) * u + 1, 16.6f * u, ColorAlpha(Color.Black, 0.5f * a));
                Game.TextBold(hint[i], 12 * u, (10 + 16 * i) * u, 16.6f * u, ColorAlpha(Color.White, a)); // measured against stable
            }
        }
        DrawVisualSettings(w, h);
        if (auto || autopilot || playback != null) SongSelect.DrawCursorAt(ToScreen(cursor));
        else SongSelect.DrawCursor();
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

    void DrawCircleSprite(Vector2 osuPos, Color colour, float alpha, float expand, int number, bool sliderHead)
    {
        var p = ToScreen(osuPos);
        float cs = CircleScale * expand;
        var body = (sliderHead ? skin.Tex("sliderstartcircle") : null) ?? skin.Tex("hitcircle");
        var overlay = (sliderHead && skin.Tex("sliderstartcircle") != null ? skin.Tex("sliderstartcircleoverlay") : null) ?? skin.Tex("hitcircleoverlay");
        float hitAge = expand > 1.001f ? (1 - alpha) * 240 : -1; // hits: alpha is 1 - age/240 (HitAnim)
        alpha *= playfieldAlpha;
        if (body is { } b) b.Draw(p, cs, ColorAlpha(colour, alpha));
        else
        {
            DrawCircleV(p, map.Radius * scale * expand, ColorAlpha(Color.White, alpha));
            DrawCircleV(p, map.Radius * scale * expand * 0.88f, ColorAlpha(colour, alpha));
        }
        // stable on a hit: the number fades out over 60ms (v2+ skins) or grows 0.8 -> 1.12 with the circle (older).
        if (number > 0 && hitAge < 0) DrawNumber(number, p, CircleScale * 0.8f, alpha);
        else if (number > 0 && skin.Version > 1) DrawNumber(number, p, CircleScale * 0.8f, playfieldAlpha * Math.Max(0, 1 - hitAge / 60));
        else if (number > 0) DrawNumber(number, p, CircleScale * (0.8f + 0.32f * (float)Easing.Apply(1, hitAge / 240)), alpha);
        overlay?.Draw(p, cs, ColorAlpha(Color.White, alpha));
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
        if (hit) return ((float)Math.Max(0, 1 - t / 240), 1 + 0.4f * (float)Easing.Apply(1, t / 240)); // stable: fade linear, grow Out
        return ((float)Math.Max(0, 1 - t / 100) * FadeInAlpha(o), 1);
    }

    void DrawHitCircle(int i)
    {
        var o = objs[i];
        var s = st[i];
        var (alpha, expand) = HitAnim(o, s.Done, s.Result > 0, s.JudgeTime);
        if (alpha > 0) DrawCircleSprite(PosOf(o), ColourOf(o), alpha, expand, o.ComboNumber, false);
    }

    void DrawSlider(int i)
    {
        var o = objs[i];
        var s = st[i];
        float alpha = SliderBodyAlpha(o);
        var colour = ColourOf(o);
        var track = skin.SliderTrackOverride ?? colour; // stable shades the combo colour itself (see SliderGradient)
        // Snaking: the body grows from the head over the first third of the approach (stable option).
        float snake = snaking ? (float)Math.Clamp((time - (o.Time - map.Preempt)) / (map.Preempt / 3), 0, 1) : 1;
        int count = Math.Max(1, (int)Math.Ceiling(bodies[i].Length * snake));
        if (alpha > 0)
        {
            // Rendered into a texture (the body is translucent and self-overlapping), then faded as a whole.
            // rlgl would apply a pushed fall transform inside the texture pass too, so it is lifted for it.
            if (Falling) Rlgl.PopMatrix();
            BeginTextureMode(sliderRt);
            ClearBackground(Color.Blank);
            DrawBody(bodies[i], count, SliderGradient(skin.SliderBorder, track));
            EndTextureMode();
            if (Falling) PushFall(i);
            var tex = sliderRt.Texture;
            DrawTextureRec(tex, new Rectangle(0, 0, tex.Width, -tex.Height), Vector2.Zero, ColorAlpha(Color.White, alpha));
        }

        var off = map.StackOffset(o);
        int span = time < o.Time ? 0 : Math.Min(o.Slides - 1, (int)((time - o.Time) / o.SpanDuration));
        bool active = time >= o.Time && time <= o.EndTime;

        // ticks still ahead in the current span
        for (int c = s.NextCheckpoint; c < o.Checkpoints.Count; c++)
        {
            var cp = o.Checkpoints[c];
            if (cp.Span != span || cp.Kind != CheckpointKind.Tick) continue;
            // stable: a tick pops in (fade + 0.5 -> 1.2, then -> 1 Out) halfway through its approach on the first
            // span (150ms steps), halfway from its span's start on repeats (200ms in, settling over 200ms); HD fades
            // it out over the second before it.
            double pop, popIn, settle;
            if (span == 0) { pop = (cp.Time - o.Time) / 2 + o.Time - map.Preempt; popIn = 150; settle = 150; }
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
            var p = ToScreen(o.PositionAtTime(cp.Time) + off);
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
            if (skin.Tex("sliderendcircle") is { } ec) ec.Draw(ToScreen(spanEnd + off), CircleScale, ColorAlpha(colour, alpha));
            if (remaining >= 1) DrawReverse(o, span % 2 == 0, spanEnd + off, alpha);
            if (remaining >= 2) DrawReverse(o, span % 2 == 1, spanStart + off, alpha);
        }

        // head
        var (headAlpha, headExpand) = HitAnim(o, s.HeadDone, s.HeadHit, s.HeadTime);
        if (headAlpha > 0) DrawCircleSprite(PosOf(o), colour, headAlpha, headExpand, o.ComboNumber, true);

        if (active)
        {
            var ball = ToScreen(BallOf(o, time));
            var tint = ColorAlpha(skin.AllowSliderBallTint ? colour : Color.White, playfieldAlpha);
            if ((skin.Tex("sliderb") ?? skin.Tex("sliderb0")) is { } sb) sb.Draw(ball, CircleScale, tint);
            else DrawCircleV(ball, map.Radius * scale * 0.8f, tint);
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
        if (skin.Tex("sliderfollowcircle") is { } fc) fc.Draw(at, CircleScale * sc, white);
        else DrawRing(at, map.Radius * scale * 2.3f * sc, map.Radius * scale * 2.4f * sc, 0, 360, 48, white);
    }

    void DrawReverse(HitObject o, bool atEnd, Vector2 osuPos, float alpha)
    {
        double d = atEnd ? o.Length : 0;
        double toward = atEnd ? Math.Max(0, o.Length - 5) : Math.Min(o.Length, 5);
        var dir = o.PointAt(toward) - o.PointAt(d);
        float rot = MathF.Atan2(dir.Y, dir.X) * 180 / MathF.PI;
        var p = ToScreen(osuPos);
        if (skin.Tex("reversearrow") is { } ra) ra.Draw(p, CircleScale, ColorAlpha(Color.White, alpha), rot);
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
    void DrawBody(Vector2[] pts, int count, Texture2D gradient)
    {
        const int segments = 64;
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
        for (int k = 0; k < count; k++)
        {
            var c = ToScreen(pts[k]);
            if (k > 0 && Vector2.DistanceSquared(prev, c) > 1e-6f)
            {
                var n = Vector2.Normalize(new Vector2(prev.Y - c.Y, c.X - prev.X)) * r;
                Begin(12);
                for (int sgn = 1; sgn >= -1; sgn -= 2) // both sides, without a per-segment array
                {
                    var side = n * sgn;
                    Centre(prev); Edge(prev + side); Edge(c + side);
                    Centre(prev); Edge(c + side); Centre(c);
                }
                Rlgl.End();
            }
            Begin(segments * 3);
            for (int j = 0; j < segments; j++)
            {
                float a0 = j * MathF.Tau / segments, a1 = (j + 1) * MathF.Tau / segments;
                Centre(c);
                Edge(c + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * r);
                Edge(c + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * r);
            }
            Rlgl.End();
            prev = c;
        }
        Rlgl.SetTexture(0);
        Rlgl.DrawRenderBatchActive();
        Rlgl.EnableColorBlend();
        Rlgl.DisableDepthTest();
    }

    // Follow points: dots along the gap between consecutive objects of the same combo, appearing in sequence.
    void DrawFollowPoints()
    {
        const double fadeIn = 400, lead = 800, fadeOut = 400;
        while (fpFirst < followPoints.Length && followPoints[fpFirst].Time + fadeOut < time) fpFirst++;
        for (int k = fpFirst; k < followPoints.Length && followPoints[k].Time - lead <= time; k++)
        {
            var fp = followPoints[k];
            double age = time - (fp.Time - lead);
            float a = (float)(Math.Clamp(age / fadeIn, 0, 1) * Math.Clamp(1 - (time - fp.Time) / fadeOut, 0, 1)) * playfieldAlpha;
            if (a <= 0) continue;
            Sprite? sprite = fpFrames > 0
                ? skin.Frame("followpoint", Math.Min(fpFrames - 1, (int)(age / fpFrameMs)))
                : skin.Tex("followpoint");
            var p = ToScreen(fp.Pos);
            if (sprite is { } sp) sp.Draw(p, GetScreenHeight() / 768f, ColorAlpha(Color.White, a), fp.Rotation);
            else DrawCircleV(p, 2.5f * scale, ColorAlpha(Color.White, a * 0.8f));
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

        int w = GetScreenWidth(), h = GetScreenHeight();
        float u = h / 480f, sd = h / 768f, cx = w / 2f;
        Vector2 U(float x, float y) => new(cx + x * u, y * u);
        // stable lays the spinner out from a base of 45, less 16 on widescreen: centred on the playfield (248).
        const float Base = 29;
        var c = U(0, Base + 219);
        var white = ColorAlpha(Color.White, alpha);
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
            skin.Tex("spinner-bottom")?.Draw(c, grow, white, top / 3);
            skin.Tex("spinner-top")?.Draw(c, grow, white, top);
            skin.Tex("spinner-middle2")?.Draw(c, grow, white, rotDeg);
            skin.Tex("spinner-middle")?.Draw(c, grow, ColorAlpha(ColorLerp(Color.White, Color.Red, (float)progress), alpha));
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
            if (skin.Tex("spinner-circle") is { } sc) sc.Draw(c, sd, white, rotDeg);
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
        if (mods.HasFlag(Mods.Hidden) && i != 0) return; // HD: only the first object keeps its approach circle
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
        float target = InBreak(time) ? 8 : combo < 100 ? 3.2f : combo < 200 ? 2.6f : 2f;
        float step = (float)(0.1 * dtMs / (1000 / 60.0));
        flScale = flScale > target ? Math.Max(flScale - step, target) : Math.Min(flScale + step, target);
    }

    void DrawFlashlight(int w, int h)
    {
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

    void DrawPopups()
    {
        // stable's judgement animations: misses fade in over 120ms shrinking 2 -> 1, tilt a random
        // +-0.15 rad (doubling by 1100ms) and, on v2+ skins, sink 45 units; hits bounce 0.6 -> 1.1 -> 0.9 -> 1, or with
        // the skin's particleNNN grow 0.9 -> 1.05 with an additive flash and 150 particles flying up to 70 units
        // (each 400-1200ms, eased out, fading). Everything fades out over 500-1100ms.
        int kept = 0; // RemoveAll without a per-frame closure
        for (int i = 0; i < popups.Count; i++) if (time - popups[i].Time <= 1200) popups[kept++] = popups[i];
        popups.RemoveRange(kept, popups.Count - kept);
        float baseScale = scale / 2.25f * 1.2f;
        foreach (var (pos, t, result) in popups)
        {
            double age = time - t;
            float Seg(double a, double b) => (float)Math.Clamp((age - a) / (b - a), 0, 1);
            float alpha = Math.Min(1, 1 - Seg(500, 1100)) * playfieldAlpha;
            var p = ToScreen(pos);
            // Per-popup randomness (stable from frame to frame) from a seeded hash, so nothing is allocated per frame.
            uint seed = (uint)HashCode.Combine(t, pos.X, pos.Y);
            double Rand() { seed = seed * 1664525 + 1013904223; return (seed >> 8) / 16777216.0; }
            string name = result switch { 300 => "hit300", 100 => "hit100", 50 => "hit50", _ => "hit0" };
            var particle = result switch { 300 => skin.Tex("particle300"), 100 => skin.Tex("particle100"), 50 => skin.Tex("particle50"), _ => null };
            float Bounce() => age < 120 ? 0.6f + 0.5f * Seg(0, 96) : age < 144 ? 1.1f - 0.2f * Seg(120, 144) : 0.9f + 0.1f * Seg(120, 168);
            if (particle is { } pt && age >= 0)
            {
                BeginBlendMode(BlendMode.Additive);
                for (int k = 0; k < 150; k++)
                {
                    double life = 400 + (int)(Rand() * 800), ang = Rand() * Math.Tau, dist = Rand() * 70;
                    double f = Math.Clamp(age / life, 0, 1);
                    if (f >= 1) continue;
                    float d = (float)(dist * Easing.Apply(13, f)) * scale; // OutQuint
                    pt.Draw(p + new Vector2(MathF.Cos((float)ang), MathF.Sin((float)ang)) * d, baseScale, ColorAlpha(Color.White, (float)(1 - f) * playfieldAlpha));
                }
                EndBlendMode();
            }
            if (skin.Tex(name) is { } sp)
            {
                if (result == 0)
                {
                    float r = (float)(Rand() * 0.3 - 0.15);
                    float rot = age < 120 ? r * Seg(0, 120) : r + r * (float)Easing.Apply(2, Seg(120, 1100));
                    float drop = skin.Version > 1 ? (-5 + 45 * (float)Easing.Apply(2, Seg(0, 1100))) * scale : 0;
                    sp.Draw(p + new Vector2(0, drop), baseScale * (2 - Seg(0, 120)), ColorAlpha(Color.White, alpha * Seg(0, 120)), rot * 180 / MathF.PI);
                }
                else if (particle != null)
                {
                    sp.Draw(p, baseScale * (0.9f + 0.15f * Seg(0, 1100)), ColorAlpha(Color.White, alpha * Seg(0, 80)));
                    float flash = age < 40 ? 0.5f * (float)Easing.Apply(1, Seg(-16, 40)) : 0.5f * (1 - Seg(40, 340));
                    BeginBlendMode(BlendMode.Additive);
                    sp.Draw(p, baseScale * Bounce(), ColorAlpha(Color.White, flash * playfieldAlpha));
                    EndBlendMode();
                }
                else sp.Draw(p, baseScale * Bounce(), ColorAlpha(Color.White, alpha * Seg(0, 120)));
            }
            else if (result != 300)
            {
                string txt = result == 0 ? "X" : result.ToString();
                var col = result switch { 100 => Color.Green, 50 => Color.Orange, _ => Color.Red };
                Game.Text(txt, p.X - Game.TextWidth(txt, 40) / 2, p.Y - 20, 40, ColorAlpha(col, alpha));
            }
        }
    }

    double Accuracy
    {
        get
        {
            int total = n300 + n100 + n50 + nMiss;
            return total == 0 ? 100 : (300.0 * n300 + 100.0 * n100 + 50.0 * n50) / (300.0 * total) * 100;
        }
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
            if (hpFilling)
            {
                if (time > -map.AudioLeadIn)
                {
                    hpShown = Math.Min(200, hpShown + 0.02 * dt);
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
        if (skin.Tex("scorebar-colour") is { } colour)
        {
            float bq = (float)Math.Clamp((Game.NowMs - hudBreakAt) / 500, 0, 1);
            float ba = hudBreak ? 1 - bq : bq, by = -20 * GetScreenHeight() / 480f * (hudBreak ? bq : 1 - bq);
            float markerBreak = hudBreak ? 1 + 0.6f * bq : 1.6f - 0.6f * bq;
            if (ba <= 0) return;
            skin.Tex("scorebar-bg")?.Draw(new Vector2(skin.Tex("scorebar-bg")!.Value.Width * s / 2, skin.Tex("scorebar-bg")!.Value.Height * s / 2 + by), s, ColorAlpha(Color.White, ba));
            // New-style skins (with scorebar-marker) place the fill differently from old ones.
            bool newStyle = skin.Tex("scorebar-marker") != null;
            var tint = Color.White;
            bool additive = newStyle;
            if (newStyle && hpShown < 40) { tint = ColorLerp(Color.Black, Color.Red, (float)((40 - hpShown) / 40)); additive = false; }
            else if (newStyle && hpShown < 100) { tint = ColorLerp(Color.White, Color.Black, (float)((100 - hpShown) / 100)); additive = false; }
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
            if (marker is not { } mk) return;
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
    string Grade()
    {
        int total = n300 + n100 + n50 + nMiss;
        if (total == 0) return "D";
        double r300 = n300 / (double)total, r50 = n50 / (double)total;
        bool silver = mods.HasFlag(Mods.Hidden) || mods.HasFlag(Mods.Flashlight);
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
        int w = GetScreenWidth(), h = GetScreenHeight();
        if (skin.Tex(name) is not { } sp) return default;
        float sw = sp.Width * h / 768f, sh = sp.Height * h / 768f;
        return new Rectangle(w / 2f - sw / 2, h / 2f + MenuY(name) * h / 480f - sh / 2, sw, sh);
    }

    // Returns the button clicked this frame (mouse or either game key), or null.
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
        int w = GetScreenWidth(), h = GetScreenHeight();
        float s = h / 768f;
        float fade = (float)Math.Clamp((Game.NowMs - menuShownAt) / 300, 0, 1);
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
    }

    // ---- skip button: the skin's play-skip, bottom-right, 60% alpha brightening on hover; fades in over 400ms.
    readonly CachedText<int> bonusText = new(v => v.ToString());
    double skipShownAt = double.NaN;
    int skipFrames = -1;

    Sprite? SkipSprite()
    {
        if (skipFrames < 0) { skipFrames = 0; while (skin.Frame("play-skip", skipFrames) != null) skipFrames++; }
        if (skipFrames == 0) return skin.Tex("play-skip");
        double frameMs = skin.AnimationFramerate > 0 ? 1000 / skin.AnimationFramerate : 1000.0 / skipFrames;
        return skin.Frame("play-skip", (int)(Game.NowMs / frameMs) % skipFrames);
    }

    Rectangle? SkipRect()
    {
        if (SkipSprite() is not { } sp) return null;
        int w = GetScreenWidth(), h = GetScreenHeight();
        float sw = sp.Width * h / 768f, sh = sp.Height * h / 768f;
        return new Rectangle(w - sw, h - sh, sw, sh);
    }

    void DrawSkip()
    {
        if (!CanSkip || finished || paused || failed) { skipShownAt = double.NaN; return; }
        if (double.IsNaN(skipShownAt)) skipShownAt = Game.NowMs;
        if (SkipSprite() is not { } sp || SkipRect() is not { } r)
        {
            Game.Text("Press Space to skip", GetScreenWidth() - 360, GetScreenHeight() - 120, 36, Color.White);
            return;
        }
        float a = (float)Math.Clamp((Game.NowMs - skipShownAt) / 400, 0, 1);
        bool over = playback == null && CheckCollisionPointRec(Input.GetMousePosition(), r);
        sp.Draw(new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2), GetScreenHeight() / 768f, ColorAlpha(Color.White, a * (over ? 1 : 0.6f)));
    }
}
