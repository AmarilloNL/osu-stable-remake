using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's volume overlay: three meters anchored
// to the bottom-right corner (positions in 640x480 units from that corner), master selected by default. The wheel
// works in the main menu, in play unless MouseDisableWheel, and anywhere with Alt held or a meter hovered; the first
// step only shows the meters, later ones move the selected one by 5. Up/Down do the same and Left/Right pick a meter
// while shown; dragging a meter moves it 1% per pixel. The meters hide 1s after the last input.
static class VolumeControl
{
    sealed class Meter(string key, string label, Vector2 pos, float scale, Vector2 labelOffset)
    {
        public readonly string Key = key, Label = label;
        public readonly Vector2 Pos = pos, LabelOffset = labelOffset;
        public readonly float Scale = scale;
        public bool Selected, Shown, Hovered;
        public double ShownAt;
        public float Arc; // eased toward the value
        public Tween Alpha, Size = new(0.6f * scale), Text, LabelAlpha, Glow, Tint = new(80 / 255f);
        public int Value => Math.Clamp(Config.GetInt(Key, 100), 0, 100);
        public float Target => (Selected ? 1 : 0.95f) * Scale;
    }

    // One animated value: eases from wherever it was toward To over Ms.
    struct Tween(float v)
    {
        float from = v, to = v;
        double at, ms;
        int ease;
        public readonly float Value(double now) =>
            ms <= 0 ? to : from + (to - from) * (float)Easing.Apply(ease, (now - at) / ms);
        public void Go(float target, double durMs, int easing, double now) { from = Value(now); to = target; at = now; ms = durMs; ease = easing; }
    }

    static readonly Meter Effect = new("VolumeEffect", "effect", new(115, 75), 0.5f, Vector2.Zero);
    static readonly Meter Master = new("VolumeUniversal", "master", new(57, 57), 1, new(5, 20)) { Selected = true, Tint = new(1) };
    static readonly Meter Music = new("VolumeMusic", "music", new(87.5f, 110), 0.5f, Vector2.Zero);
    static readonly Meter[] Meters = [Effect, Master, Music];
    static Meter selected = Master;
    static Meter? dragging;
    static float dragY;
    static int repeats;
    static readonly CachedText<int>[] valueText = [new(v => v + "%"), new(v => v + "%"), new(v => v + "%")];

    // The wheel after the overlay took its share: screens scroll with this instead of GetMouseWheelMove.
    public static float Wheel { get; private set; }
    public static bool Shown => Master.Shown;

    public static void Update(SongSelect songs)
    {
        double now = Game.NowMs;
        var mouse = Input.GetMousePosition();
        float raw = Input.GetMouseWheelMove();
        Wheel = raw;
        var player = Game.Screen as Player;
        bool alt = Input.AltHeld;
        bool hoverable = player is not { Playing: true };

        foreach (var m in Meters)
        {
            if (m.Shown && now - m.ShownAt > 1000) Hide(m, now);
            bool over = m.Shown && hoverable && Vector2.Distance(mouse, Centre(m)) <= Radius(m, now);
            if (over && !m.Hovered) Select(m, now); // stable selects on hover
            m.Hovered = over;
            if (m.Shown && m.Alpha.Value(now) > 0.98f) // the arc only moves once the meter is fully in
            {
                float v = m.Value / 100f, d = m.Arc - v;
                m.Arc = Math.Abs(d) < 0.0001f ? v : Math.Clamp(v + d * (float)Math.Pow(0.99, GetFrameTime() * 1000), 0, 1);
            }
        }

        bool hovered = Master.Hovered || Effect.Hovered || Music.Hovered;
        bool active = alt || hovered || Game.Screen is MainMenu { JumpOpen: false } // (the Jump To window scrolls with it)
            || player != null && !Config.GetBool("MouseDisableWheel", false);
        if (raw != 0 && active)
        {
            Step(Math.Sign(raw) * 5, now);
            Wheel = 0;
        }

        if (dragging != null)
        {
            if (!Input.IsMouseButtonDown(MouseButton.Left)) dragging = null;
            else
            {
                ShowAll(now);
                float d = dragY - mouse.Y;
                if (Math.Abs(d) >= 1) { Change(dragging, dragging.Value + (int)Math.Round(d), now, songs); dragY = mouse.Y; }
            }
        }
        else if (hovered && Input.IsMouseButtonPressed(MouseButton.Left))
        {
            dragging = Master.Hovered ? Master : Effect.Hovered ? Effect : Music;
            dragY = mouse.Y;
        }

        // Keys: stable leaves them to gameplay unless Alt is held, and to song select unless Alt/hover.
        if (alt || hovered || Game.Screen is MainMenu { JumpOpen: false }) // (the Jump To window steps with Up / Down)
        {
            int dir = Key(Config.Key("keyVolumeIncrease", KeyboardKey.Up)) ? 1 : Key(Config.Key("keyVolumeDecrease", KeyboardKey.Down)) ? -1 : 0; // stable's bindings
            if (dir != 0) Step(dir * 5, now);
            if (Shown && Input.IsKeyPressed(KeyboardKey.Left)) Select(Meters[(Array.IndexOf(Meters, selected) + 2) % 3], now);
            if (Shown && Input.IsKeyPressed(KeyboardKey.Right)) Select(Meters[(Array.IndexOf(Meters, selected) + 1) % 3], now);
        }

        void Step(int by, double t)
        {
            if (ShowAll(t)) Change(selected, selected.Value + by, t, songs);
        }
    }

    // A held key repeats a change only after its fourth repeat, like stable.
    static bool Key(KeyboardKey k)
    {
        if (Input.IsKeyPressed(k)) { repeats = 0; return true; }
        return Input.IsKeyPressedRepeat(k) && ++repeats >= 4;
    }

    static void Select(Meter m, double now)
    {
        if (m == selected) return;
        foreach (var s in new[] { selected, m })
        {
            s.Selected = s == m;
            s.Tint.Go(s.Selected ? 1 : 80 / 255f, 100, 0, now);
            if (s.Shown) s.Size.Go(s.Target, 600, 25, now);
        }
        selected = m;
        ShowAll(now);
    }

    // Shows (or keeps showing) every meter; true when they were already up.
    static bool ShowAll(double now)
    {
        bool was = Master.Shown;
        foreach (var m in Meters)
        {
            m.ShownAt = now;
            if (m.Shown) continue;
            m.Shown = true;
            m.Alpha.Go(1, 80, 0, now);
            m.Size.Go(m.Target, 600, 25, now);
            m.Text.Go(1, 150, 0, now);
            m.LabelAlpha.Go(1, 250, 0, now);
            m.Glow = new(m.Value == 100 ? 1 : 0);
            m.Arc = 0;
        }
        return was;
    }

    static void Hide(Meter m, double now)
    {
        m.Shown = false;
        m.Alpha.Go(0, 200, 0, now);
        m.Size.Go(0.6f * m.Target, 1000, 1, now);
        m.Text.Go(0, 50, 0, now);
        m.LabelAlpha.Go(0, 250, 0, now);
        m.Arc = 0;
    }

    static void Change(Meter m, int value, double now, SongSelect songs)
    {
        int before = m.Value, after = Math.Clamp(value, 0, 100);
        if (value == before) return;
        ShowAll(now);
        Config.SetOwn(m.Key, after.ToString());
        m.Glow.Go(after == 100 ? 1 : 0, 150, 0, now);
        // stable's tick rises in pitch with the volume, and drops lower when the value is already at its limit
        float pitch = 1.1f - (0.5f - after / 100f) * 0.15f * (after != before ? 1 : 4);
        Game.Skin.Sound("click-short")?.Play(BeatmapSamples.EffectVolume, pitch);
        if (m != Effect) { songs.ApplyMusicVolume(); (Game.Screen as Player)?.ApplyMusicVolume(); }
    }

    static Vector2 Centre(Meter m)
    {
        float u = Game.H / 480f;
        return new(Game.W - m.Pos.X * u, Game.H - m.Pos.Y * u);
    }

    static float Radius(Meter m, double now) => 50 * m.Size.Value(now) * Game.H / 768f;

    public static void Draw()
    {
        double now = Game.NowMs;
        if (Master.Alpha.Value(now) <= 0 && Effect.Alpha.Value(now) <= 0 && Music.Alpha.Value(now) <= 0) return;
        int w = Game.W, h = Game.H;
        float u = h / 480f, sd = h / 768f;

        if (Game.Skin.DefaultTex("volume-background-gradient") is { } grad)
        {
            float gw = grad.Width * sd, gh = grad.Height * sd;
            grad.Draw(new Vector2(w - gw / 2, h - gh / 2), sd, ColorAlpha(Color.White, Master.Alpha.Value(now)));
        }

        for (int i = 0; i < Meters.Length; i++)
        {
            var m = Meters[i];
            float a = m.Alpha.Value(now), s = m.Size.Value(now);
            var c = Centre(m);
            if (a > 0)
            {
                Game.Skin.DefaultTex("volume-circle-bg")?.Draw(c, s * sd, ColorAlpha(Color.White, a * m.Tint.Value(now)));
                if (m.Glow.Value(now) * a is var g and > 0)
                    Game.Skin.DefaultTex("volume-circle-bg-max")?.Draw(c, s * sd, ColorAlpha(Color.White, g));

                // the ring (stable's ring shader): 8 units wide inside radius 50, white progress over a (54,72,78) track,
                // a (17,136,170) glow 10 units to each side, added on at the meter's alpha^8
                Ring.Draw(c, 50 * s * sd, 42 * s * sd, 10 * s * sd, m.Arc, MathF.Pow(a, 8));
            }

            if (m.Text.Value(now) is var ta and > 0)
            {
                string t = valueText[i].Of((int)Math.Round(m.Arc * 100));
                float size = 14 * u * s;
                Game.Text(t, c.X - Game.TextWidth(t, size) / 2, c.Y - size / 2, size, ColorAlpha(Color.White, ta));
            }
            if (m.LabelAlpha.Value(now) is var la and > 0)
            {
                float size = 12 * u;
                float right = w - (m.Pos.X + 36 * m.Scale - m.LabelOffset.X) * u, cy = h - (m.Pos.Y - m.LabelOffset.Y) * u;
                Game.Text(m.Label, right - Game.TextWidth(m.Label, size), cy - size / 2, size, ColorAlpha(Color.White, la));
            }
        }
    }
}

// stable's ring shader (volume meters): a ring between the inner and outer radius, the progress part (clockwise from
// the top) in the foreground colour and the rest in the background colour, plus a glow fading out over the glow width
// on both sides, full along the progress and at 20% elsewhere. Drawn added on (one, one).
static class Ring
{
    const string Fs = """
        #version 330
        in vec2 fragTexCoord;
        out vec4 finalColor;
        uniform vec4 m_Rect; // x, y, w, h of the quad in screen pixels
        uniform vec2 m_CenterPos;
        uniform float m_OuterRadius, m_InnerRadius, m_OuterGlowRadius, m_InnerGlowRadius, m_Alpha, m_Progress;
        uniform vec4 m_RingForegroundColour, m_RingBackgroundColour, m_GlowColour;
        const float TWOPI = 6.28318548, PI = 3.14159274, RING_FUDGE_FACTOR = 0.05, GLOW_OVERSHOOT = 0.3;
        float isRing(float d) { return smoothstep(m_InnerRadius, m_InnerRadius + 1.0, d) * smoothstep(m_OuterRadius, m_OuterRadius - 1.0, d); }
        float isGlow(float d) { return smoothstep(m_InnerGlowRadius, m_InnerRadius, d) * clamp(1.0 - isRing(d), 0.0, 1.0) * smoothstep(m_OuterGlowRadius, m_OuterRadius, d); }
        void main()
        {
            vec2 p = m_Rect.xy + fragTexCoord * m_Rect.zw;
            float dist = distance(p, m_CenterPos);
            float angle = atan(p.x - m_CenterPos.x, p.y - m_CenterPos.y);
            float startAngle = (0.5 - m_Progress) * TWOPI;
            float reduction = smoothstep(PI, PI - GLOW_OVERSHOOT, startAngle);
            float activeRing = smoothstep(startAngle - reduction * RING_FUDGE_FACTOR, startAngle, angle);
            float activeGlow = max(smoothstep(startAngle - GLOW_OVERSHOOT, startAngle + GLOW_OVERSHOOT, angle),
                                   smoothstep(-PI + GLOW_OVERSHOOT, -PI - GLOW_OVERSHOOT, angle))
                               * reduction * smoothstep(PI + GLOW_OVERSHOOT / 2.0, PI - GLOW_OVERSHOOT / 2.0, angle);
            activeGlow += max(0.0, (1.0 - activeGlow) * smoothstep(0.9, 1.0, m_Progress));
            vec4 glow = isGlow(dist) * (activeGlow + (1.0 - activeGlow) * 0.2) * m_GlowColour;
            vec4 ring = isRing(dist) * (activeRing * m_RingForegroundColour + (1.0 - activeRing) * m_RingBackgroundColour);
            finalColor = (ring + glow) * m_Alpha;
        }
        """;
    static Shader? shader;
    static int rect, centre, outerR, innerR, outerG, innerG, alpha, progress;

    public static void Draw(Vector2 c, float outer, float inner, float glow, float progressValue, float alphaValue)
    {
        if (alphaValue <= 0) return;
        if (shader is not { } sh)
        {
            sh = LoadShaderFromMemory(null, Fs);
            shader = sh;
            rect = GetShaderLocation(sh, "m_Rect"); centre = GetShaderLocation(sh, "m_CenterPos");
            outerR = GetShaderLocation(sh, "m_OuterRadius"); innerR = GetShaderLocation(sh, "m_InnerRadius");
            outerG = GetShaderLocation(sh, "m_OuterGlowRadius"); innerG = GetShaderLocation(sh, "m_InnerGlowRadius");
            alpha = GetShaderLocation(sh, "m_Alpha"); progress = GetShaderLocation(sh, "m_Progress");
            SetShaderValue(sh, GetShaderLocation(sh, "m_RingForegroundColour"), new Vector4(1, 1, 1, 1), ShaderUniformDataType.Vec4);
            SetShaderValue(sh, GetShaderLocation(sh, "m_RingBackgroundColour"), new Vector4(54, 72, 78, 255) / 255, ShaderUniformDataType.Vec4);
            SetShaderValue(sh, GetShaderLocation(sh, "m_GlowColour"), new Vector4(17, 136, 170, 255) / 255, ShaderUniformDataType.Vec4);
        }
        float r = outer + glow;
        var quad = new Rectangle(c.X - r, c.Y - r, 2 * r, 2 * r);
        SetShaderValue(sh, rect, new Vector4(quad.X, quad.Y, quad.Width, quad.Height), ShaderUniformDataType.Vec4);
        SetShaderValue(sh, centre, c, ShaderUniformDataType.Vec2);
        SetShaderValue(sh, outerR, outer, ShaderUniformDataType.Float);
        SetShaderValue(sh, innerR, inner, ShaderUniformDataType.Float);
        SetShaderValue(sh, outerG, outer + glow, ShaderUniformDataType.Float);
        SetShaderValue(sh, innerG, inner - glow, ShaderUniformDataType.Float);
        SetShaderValue(sh, alpha, alphaValue, ShaderUniformDataType.Float);
        SetShaderValue(sh, progress, progressValue, ShaderUniformDataType.Float);
        var white = new Texture2D { Id = Rlgl.GetTextureIdDefault(), Width = 1, Height = 1, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 };
        BeginShaderMode(sh);
        BeginBlendMode(BlendMode.AddColors);
        DrawTexturePro(white, new Rectangle(0, 0, 1, 1), quad, Vector2.Zero, 0, Color.White);
        EndBlendMode();
        EndShaderMode();
    }
}
