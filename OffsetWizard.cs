using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's offset wizard (Options > Audio > Offset wizard): over the song that's playing, ticks (osu!ui's
// options-offset-tick) slide from x 560 to width - 560 (units) at 120 units a beat, passing a centre marker on the beat,
// where stable plays its metronome and flashes the marker. Up/Down (Shift: 5) or the wheel change the universal offset
// at once, which moves the clock the ticks and the metronome follow. Checkboxes: half tick (every other beat), its
// "tick on other beat", or double tick (every half beat). Everything follows stable's audio clock, offsets included.
sealed class OffsetWizard : Screen
{
    readonly Screen back;
    readonly SongSelect songs;
    readonly BackButton backButton = new();
    bool half, other, dbl; // the three checkboxes
    readonly List<(double At, bool Alive, double DyingAt)> ticks = []; // spawned at audio time At
    DbBeatmap? map;
    int travel, lead, lastSpawn, lastTick, firstSpawn;
    double halfBeat;
    bool started, waiting, clear;
    double flashAt = double.NaN, valueFlashAt = double.NegativeInfinity;

    public OffsetWizard(Screen back, SongSelect songs) { this.back = back; this.songs = songs; }

    static float U => Game.H / 480f;
    static float Wu => Game.W / U; // the width in 640x480 units

    // stable's AudioEngine time: the music minus the map's offsets plus the universal one
    double? AudioTime()
    {
        double pos = songs.MusicMs;
        if (double.IsNaN(pos) || songs.Current is not { } b) return null;
        int local = Config.GetInt($"LocalOffset_{b.Md5}", songs.LocalOffsetOf(b.Md5));
        return pos - (songs.TimingFormat < 5 ? 24 : 0) - local - songs.OnlineOffsetOf(b.Md5) + Config.GetDouble("Offset", 0);
    }

    void Reset(bool force)
    {
        if (songs.Current == map && !force) return;
        map = songs.Current;
        (lastTick, halfBeat, lead, travel, lastSpawn, clear) = (0, 0, 0, 0, 0, true);
    }

    // stable: every beat; double tick every half beat; half tick every other beat, "on other beat" a beat later
    bool Ticks(int halfBeats) => halfBeats % (dbl ? 1 : half ? 4 : 2) == (half && other ? 2 : 0);

    void Options(Action change)
    {
        change();
        (clear, started, waiting) = (true, false, true);
    }

    void ChangeOffset(int by)
    {
        Config.SetOwn("Offset", (Config.GetInt("Offset", 0) + by).ToString(System.Globalization.CultureInfo.InvariantCulture)); // (some regions write a minus the reader can't parse)
        valueFlashAt = Game.NowMs;
    }

    public override void Update(double dtMs)
    {
        songs.Ambient(menu: back is MainMenu);
        var mouse = Input.GetMousePosition();
        int step = Input.ShiftHeld ? 5 : 1;
        if (Input.IsKeyPressed(KeyboardKey.Up)) ChangeOffset(step);
        if (Input.IsKeyPressed(KeyboardKey.Down)) ChangeOffset(-step);
        if (VolumeControl.Wheel is var wheel && wheel != 0) ChangeOffset(Math.Sign(wheel));
        if (Input.IsKeyPressed(KeyboardKey.Escape) || Input.IsMouseButtonPressed(MouseButton.Left) && CheckCollisionPointRec(mouse, BackButton.Rect(Game.H)))
        {
            Game.Skin.Sound("menuback")?.Play(BeatmapSamples.EffectVolume);
            Game.FadeTo(back);
            return;
        }
        if (Input.IsMouseButtonPressed(MouseButton.Left))
            for (int i = 0; i < 3; i++)
                if (Enabled(i) && CheckCollisionPointRec(mouse, BoxRect(i)))
                {
                    if (i == 0) Options(() => { half = !half; if (half) dbl = false; });
                    else if (i == 1) Options(() => { dbl = !dbl; if (dbl) half = false; });
                    else Options(() => other = !other);
                    Game.Skin.Sound(Checked(i) ? "check-on" : "check-off")?.Play(BeatmapSamples.EffectVolume);
                }

        Reset(false);
        if (AudioTime() is not { } now || Red(now) is not { } red || red.BeatLength <= 0) return;
        double beat = red.BeatLength, rel = now - red.Time;
        if (travel == 0) travel = (int)((double)((float)(1120 - Wu) / 120f) * beat);
        if (lead == 0) lead = (int)((double)((float)(560 - Wu / 2) / 120f) * beat);
        if (halfBeat == 0) halfBeat = beat / 2;
        int spawn = (int)((rel + lead) / halfBeat);
        if (spawn > lastSpawn)
        {
            lastSpawn = spawn;
            if (Ticks(spawn))
            {
                ticks.Add((now, true, double.NaN));
                if (!started) (started, firstSpawn) = (true, (int)rel);
            }
        }
        int tick = (int)(rel / halfBeat);
        if (tick < lastTick) Reset(true); // the song went back (a loop or another song)
        else if (tick > lastTick)
        {
            lastTick = tick;
            if (started && !waiting)
            {
                if (Ticks(tick))
                {
                    Game.Skin.Sound("metronomelow")?.Play(BeatmapSamples.EffectVolume);
                    flashAt = now;
                }
            }
            else if (started && rel > firstSpawn + lead / 2) waiting = false;
        }
        ticks.RemoveAll(t => now - t.At >= travel || now < t.At); // past the end (or the clock jumped back)
        if (clear)
        {
            for (int i = 0; i < ticks.Count; i++) if (ticks[i].Alive) ticks[i] = (ticks[i].At, false, now);
            clear = false;
        }
    }

    TimingPoint? Red(double at)
    {
        TimingPoint? red = null;
        foreach (var t in songs.Timing) { if (t.Time > at && red != null) break; if (t.Uninherited) red = t; }
        return red;
    }

    // stable's checkboxes: half tick (50, 60), double tick (150, 60), tick on other beat (50, 80); a box 16 units square
    static Rectangle BoxRect(int i)
    {
        float u = U;
        var (x, y, w) = i switch { 0 => (50, 60, 90), 1 => (150, 60, 100), _ => (50, 80, 130) };
        return new Rectangle(x * u, y * u, w * u, 16 * u);
    }
    bool Checked(int i) => i switch { 0 => half, 1 => dbl, _ => other };
    bool Enabled(int i) => i < 2 || half; // stable: "tick on other beat" only with half tick

    public override void Draw()
    {
        int w = Game.W, h = Game.H;
        float u = U, sd = h / 768f, wu = Wu;
        double now = AudioTime() ?? 0;
        if (Game.Skin.DefaultTex("options-offset-tick") is { } tick)
        {
            foreach (var (at, alive, dyingAt) in ticks)
            {
                double t = now - at, p = travel > 0 ? Math.Clamp(t / travel, 0, 1) : 1;
                float x = 560 + (wu - 1120) * (float)p; // from 560 to width - 560
                double alpha = Math.Min(Math.Clamp(t / 300, 0, 1), Math.Clamp((travel - t) / 300, 0, 1));
                if (!alive) alpha = Math.Min(alpha, 1 - Math.Clamp((now - dyingAt) / 300, 0, 1));
                float sx = t < travel / 2.0 ? 1 : 1 - 0.7f * (float)Math.Clamp((t - travel / 2.0) / (travel / 2.0), 0, 1); // 1 -> 0.3 over the second half
                DrawStretched(tick, new Vector2(x * u, 240 * u), sx * sd, sd, ColorAlpha(Color.White, (float)alpha));
            }
            // the centre marker: white, and white -> black over 80ms on each tick
            float k = double.IsNaN(flashAt) ? 0 : (float)Math.Clamp((now - flashAt) / 80, 0, 1);
            byte g = (byte)(255 * (1 - k));
            DrawStretched(tick, new Vector2(wu / 2 * u, 240 * u), 0.1f * sd, 2 * sd, new Color(g, g, g, (byte)255));
        }

        float size30 = 30 * 1.035f * u;
        string label = "Universal offset:";
        Game.Text(label, 50 * u, 20 * u, size30, Color.White);
        float vk = (float)Math.Clamp((Game.NowMs - valueFlashAt) / 200, 0, 1);
        // the value: YellowGreen after the label's (padded) text bitmap, flashing white on a change
        Game.Text(Config.GetInt("Offset", 0).ToString(), 50 * u + Game.TextWidth(label, size30) + 3.3f * u, 20 * u, size30, ColorLerp(Color.White, new Color(154, 205, 50, 255), vk));

        string[] names = ["Half tick", "Double tick", "Tick on other beat"];
        var pink = new Color(236, 117, 140, 255);
        for (int i = 0; i < 3; i++)
        {
            var r = BoxRect(i);
            var c = new Vector2(r.X + 8 * u, r.Y + 8 * u);
            var col = Enabled(i) ? pink : Xna.Gray;
            if (Game.Skin.DefaultTex(Checked(i) ? "circle-full" : "circle-empty") is { } sp) sp.Draw(c, sd, col);
            float ts = 15 * 1.035f * u;
            Game.Text(names[i], r.X + 20 * u, c.Y - ts / 2, ts, Enabled(i) ? Color.White : Xna.Gray);
        }

        float s16 = 16 * 1.035f * u;
        string[] lines = ["Use Up and Down keys or mousewheel to change the offset.", "Universal Offset will be updated on leaving this wizard."];
        for (int i = 0; i < lines.Length; i++)
            Game.Text(lines[i], w / 2f - Game.TextWidth(lines[i], s16) / 2, 400 * u + i * s16 * 1.2f, s16, Color.White);

        backButton.Draw(h, Input.GetMousePosition());
        SongSelect.DrawCursor();
    }

    static void DrawStretched(Sprite sp, Vector2 centre, float sx, float sy, Color tint)
    {
        float dw = sp.Width * sx, dh = sp.Height * sy;
        DrawTexturePro(sp.Tex, new Rectangle(0, 0, sp.Tex.Width, sp.Tex.Height), new Rectangle(centre.X, centre.Y, dw, dh), new Vector2(dw / 2, dh / 2), 0, tint);
    }

    // stable: closing the window here goes to the main menu
    public override void CloseRequested() => Game.FadeTo(songs.Menu ?? back);
}
