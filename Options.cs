using System.Globalization;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace StableRemake;

// stable's options panel over the screen it was opened from (main menu or song select), laid out from a stable
// capture in 640x480 units: a black icon strip (sections), the translucent panel, search on top, big section
// titles, bold sub-headers with a rule, circle checkboxes, line sliders, a pink "back" tab. Only settings this
// client reads are listed; changes apply live and go to remake.cfg, never stable's own cfg.
sealed class Options : Screen
{
    enum Kind { Section, Header, Toggle, Slider, Choice, Key }

    sealed record Item(Kind Kind, string Label, string Key = "", double Min = 0, double Max = 0, double Def = 0,
        string Format = "0", string Unit = "", string[]? Values = null, string[]? Names = null, string DefValue = "", int Icon = 0);

    static readonly string[] Limiters = ["VSync", "PowerSaving", "Optimal", "Unlimited"];
    static readonly Color Pink = new(235, 117, 140, 255), Title = new(70, 175, 206, 255),
        Rule = new(68, 65, 77, 255), Strip = new(8, 8, 8, 255), BackPink = new(237, 52, 152, 255);

    readonly Screen back;
    readonly SongSelect songs;
    readonly Item[] items;
    string? jumpTo;
    string search = "";
    float scroll, scrollTarget;
    int dragging = -1, binding = -1;
    double openedAt = Game.NowMs, closingAt = double.NaN;

    // Layout of the visible items (content y, height), rebuilt each frame; slider tracks from the last draw.
    readonly List<(int Item, float Y, float H)> layout = [];
    readonly Dictionary<int, (float X0, float X1)> tracks = [];

    public Options(Screen back, SongSelect songs, string? jumpTo = null)
    {
        this.back = back;
        this.songs = songs;
        this.jumpTo = jumpTo;
        var skinDir = Path.Combine(Config.OsuDir, "Skins");
        var skins = Directory.Exists(skinDir) ? Directory.GetDirectories(skinDir).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
        int m = GetCurrentMonitor();
        var resolutions = new[] { (1280, 720), (1366, 768), (1600, 900), (1920, 1080), (2560, 1440), (3840, 2160), (GetMonitorWidth(m), GetMonitorHeight(m)) }
            .Where(r => r.Item1 <= GetMonitorWidth(m) && r.Item2 <= GetMonitorHeight(m)).Distinct().OrderBy(r => r.Item1).Select(r => $"{r.Item1}x{r.Item2}").ToArray();
        items =
        [
            new(Kind.Section, "GRAPHICS", Icon: 0xf108),
            new(Kind.Header, "LAYOUT"),
            new(Kind.Choice, "Display mode", "DisplayMode", Values: ["Windowed", "Borderless", "Fullscreen"], DefValue: Game.DisplayMode()),
            new(Kind.Choice, "Resolution", "Resolution", Values: resolutions, DefValue: $"{Game.Resolution().W}x{Game.Resolution().H}"),
            new(Kind.Header, "RENDERER"),
            new(Kind.Choice, "Frame limiter", "FrameSync", Values: Limiters, Names: ["VSync", "Power saving (2x)", "Optimal (8x)", "Unlimited"], DefValue: "Unlimited"),
            new(Kind.Header, "DETAIL SETTINGS"),
            new(Kind.Toggle, "Snaking sliders", "SnakingSliders", Def: 1),

            new(Kind.Section, "GAMEPLAY", Icon: 0xf10c),
            new(Kind.Header, "GENERAL"),
            new(Kind.Slider, "Background dim", "DimLevel", 0, 100, 80, "0", "%"),
            new(Kind.Choice, "Progress display", "ProgressBarType", Values: ["Pie", "Off"], Names: ["Pie", "Off"], DefValue: "Pie"),
            new(Kind.Choice, "Score meter type", "ScoreMeter", Values: ["Error", "None"], Names: ["Hit Error", "None"], DefValue: "Error"),
            new(Kind.Slider, "Score meter size", "ScoreMeterScale", 0.5, 2, 1, "0.0", "x"),
            new(Kind.Toggle, "Show key overlay", "KeyOverlay"),

            new(Kind.Section, "AUDIO", Icon: 0xf028),
            new(Kind.Header, "VOLUME"),
            new(Kind.Slider, "Master", "VolumeUniversal", 0, 100, 100, "0", "%"),
            new(Kind.Slider, "Music", "VolumeMusic", 0, 100, 100, "0", "%"),
            new(Kind.Slider, "Effect", "VolumeEffect", 0, 100, 100, "0", "%"),
            new(Kind.Toggle, "Ignore beatmap hitsounds", "IgnoreBeatmapSamples"),
            new(Kind.Header, "OFFSET ADJUSTMENT"),
            new(Kind.Slider, "Universal offset", "RemakeOffset", -300, 300, 0, "0", "ms"),
            new(Kind.Header, "OTHER"),
            new(Kind.Toggle, "osu! music theme in the main menu", "MenuMusic", Def: 1),

            new(Kind.Section, "SKIN", Icon: 0xf1fc),
            new(Kind.Header, "SKIN"),
            new(Kind.Choice, "Current skin", "Skin", Values: skins),
            new(Kind.Toggle, "Ignore all beatmap skins", "IgnoreBeatmapSkins"),
            new(Kind.Header, "CURSOR"),
            new(Kind.Slider, "Cursor size", "CursorSize", 0.5, 2, 1, "0.00", "x"),

            new(Kind.Section, "INPUT", Icon: 0xf11b),
            new(Kind.Header, "MOUSE"),
            new(Kind.Toggle, "Disable mouse buttons in play mode", "MouseDisableButtons"),
            new(Kind.Header, "KEYBOARD"),
            new(Kind.Key, "Left click key", "keyOsuLeft", Def: (int)KeyboardKey.Z),
            new(Kind.Key, "Right click key", "keyOsuRight", Def: (int)KeyboardKey.X),
        ];
    }

    float u;
    int w, h;
    // ContentTop: first section, below stable's header (title 40, blurb 60, search 100) when not scrolled (stable's
    // 140, +3 measured). Header pieces scroll with the content but stop after StickAt, which leaves the search line
    // pinned at the top. JumpTop: where a jump puts a section's band (stable: element at 70).
    const float StripW = 40, PanelR = 313, ContentTop = 143, StickAt = 90, JumpTop = 65, RowH = 13.333f;

    // stable: the last section whose element is no more than 70 + a quarter screen (120) below the scroll.
    int CurrentSection()
    {
        int current = 0;
        foreach (var (i, y, _) in layout)
            if (items[i].Kind == Kind.Section && y - JumpTop - 120 <= scroll) current = Math.Max(0, Array.IndexOf(sections, i));
        return current;
    }

    static string Show(double v, Item it) => v.ToString(it.Format, CultureInfo.InvariantCulture) + it.Unit;

    double Value(Item it) => Config.GetDouble(it.Key, it.Def);

    string ChoiceText(Item it)
    {
        string v = Config.Get(it.Key, it.DefValue.Length > 0 ? it.DefValue : it.Values is { Length: > 0 } vs ? vs[0] : "");
        int idx = it.Values == null ? -1 : Array.FindIndex(it.Values, x => x.Equals(v, StringComparison.OrdinalIgnoreCase));
        return it.Names != null && idx >= 0 ? it.Names[idx] : v;
    }

    static readonly byte[] DefaultRamp = [255, 160, 120, 80, 40, 5];
    readonly List<(float Y, float H)> markers = [];

    // Default as stored, or null when there's none to go back to (display settings default to what's in use).
    static string? DefaultOf(Item it) => it.Kind switch
    {
        Kind.Toggle => it.Def != 0 ? "1" : "0",
        Kind.Slider => Show(it.Def, it with { Unit = "" }),
        Kind.Choice when it.DefValue.Length > 0 && it.Key is not ("DisplayMode" or "Resolution") => it.DefValue,
        Kind.Key => ((KeyboardKey)(int)it.Def).ToString(),
        _ => null,
    };

    bool IsDefault(Item it) => DefaultOf(it) is not { } d || it.Kind switch
    {
        Kind.Toggle => Config.GetBool(it.Key, it.Def != 0) == (it.Def != 0),
        Kind.Slider => Math.Abs(Value(it) - it.Def) < 1e-6,
        Kind.Choice => Config.Get(it.Key, d).Equals(d, StringComparison.OrdinalIgnoreCase),
        _ => Config.Key(it.Key, (KeyboardKey)(int)it.Def) == (KeyboardKey)(int)it.Def,
    };

    void Set(Item it, string value)
    {
        if (Config.Get(it.Key, "") == value) return;
        Config.SetOwn(it.Key, value);
        switch (it.Key)
        {
            case "VolumeUniversal" or "VolumeMusic": songs.ApplyMusicVolume(); break;
            case "FrameSync":
                if (value == "VSync") SetWindowState(ConfigFlags.VSyncHint);
                else ClearWindowState(ConfigFlags.VSyncHint);
                break;
            case "DisplayMode" or "Resolution": Game.ApplyDisplay(); break;
            case "Skin":
                Game.Skin.Unload();
                Game.Skin = new Skin(Path.Combine(Config.OsuDir, "Skins", value));
                break;
        }
    }

    void Close()
    {
        if (!double.IsNaN(closingAt)) return;
        Game.Skin.Sound("menuback")?.Play(BeatmapSamples.EffectVolume);
        closingAt = Game.NowMs;
    }

    // Items shown for the current search (sections/headers kept when anything under them matches).
    void BuildLayout()
    {
        layout.Clear();
        bool Match(Item it) => search.Length == 0 || it.Label.Contains(search, StringComparison.OrdinalIgnoreCase);
        // stable's stacking: each element is its height plus 10 apart (section 10, group header 14 after a 20-unit
        // gap, row 13.33 - measured, the code's 13.125 grows with the text); entries hold the row band (5 above).
        float y = ContentTop;
        for (int i = 0; i < items.Length; i++)
        {
            var it = items[i];
            if (it.Kind is Kind.Section or Kind.Header)
            {
                bool any = false;
                for (int k = i + 1; k < items.Length && (items[k].Kind != Kind.Section) && !(it.Kind == Kind.Header && items[k].Kind == Kind.Header); k++)
                    if (items[k].Kind is not (Kind.Section or Kind.Header) && Match(items[k])) { any = true; break; }
                if (!any) continue;
                if (it.Kind == Kind.Header) y += 20;
                float hgt = it.Kind == Kind.Section ? 10 : 14;
                layout.Add((i, y - 5, hgt + 10));
                y += hgt + 10;
            }
            else if (Match(it))
            {
                layout.Add((i, y - 5, RowH + 10));
                y += RowH + 10;
            }
        }
    }

    float ContentHeight => layout.Count == 0 ? 0 : layout[^1].Y + layout[^1].H + 40;

    Rectangle BackRect => new(0, 444 * u, 57.6f * u, 27 * u);

    public override void Update(double dtMs)
    {
        w = GetScreenWidth(); h = GetScreenHeight();
        u = h / 480f;
        songs.Ambient();
        if (!double.IsNaN(closingAt))
        {
            if (Game.NowMs - closingAt > 200) Game.Screen = back;
            return;
        }

        if (binding >= 0)
        {
            int key = Input.GetKeyPressed();
            if (key == (int)KeyboardKey.Escape) binding = -1;
            else if (key != 0)
            {
                Set(items[binding], ((KeyboardKey)key).ToString());
                binding = -1;
            }
            return;
        }
        if (Input.IsKeyPressed(KeyboardKey.Escape))
        {
            if (search.Length > 0) search = "";
            else Close();
            return;
        }
        if (Input.IsKeyDown(KeyboardKey.LeftControl) && Input.IsKeyPressed(KeyboardKey.O)) { Close(); return; }
        for (int ch = Input.GetCharPressed(); ch != 0; ch = Input.GetCharPressed()) { search += char.ConvertFromUtf32(ch); scrollTarget = 0; }
        if ((Input.IsKeyPressed(KeyboardKey.Backspace) || Input.IsKeyPressedRepeat(KeyboardKey.Backspace)) && search.Length > 0) search = search[..^1];

        BuildLayout();
        if (jumpTo != null) // test hook: start scrolled to a section
        {
            if (layout.FindIndex(l => items[l.Item].Label == jumpTo) is int at and >= 0) scroll = scrollTarget = layout[at].Y - JumpTop;
            jumpTo = null;
        }
        scrollTarget = Math.Clamp(scrollTarget - VolumeControl.Wheel * 60, 0, Math.Max(0, ContentHeight - 480));
        scroll += (scrollTarget - scroll) * (float)Math.Min(1, dtMs / 80);

        var mouse = Input.GetMousePosition();
        bool pressed = Input.IsMouseButtonPressed(MouseButton.Left), right = Input.IsMouseButtonPressed(MouseButton.Right);
        if (pressed && (mouse.X > PanelR * u || CheckCollisionPointRec(mouse, BackRect))) { Close(); return; }
        if (!Input.IsMouseButtonDown(MouseButton.Left)) dragging = -1;

        // Icon strip: widens to show section names while hovered (stable: 40 -> 140 units, eased 0.8 a frame, each
        // change only after the width has sat still for 300ms); a click jumps to that section.
        int? slot = HoveredSlot(mouse);
        float want = slot != null ? 140 : 40, aim = stripW;
        if (stripWait > 0) { if (want != stripW) stripWait -= (float)dtMs; }
        else aim = want;
        if (stripW != aim) stripW = Math.Max(40, aim + (stripW - aim) * (float)Math.Pow(0.8, dtMs / (1000 / 60.0)));
        if (Math.Abs(stripW - want) < 1) stripWait = 300;
        if (pressed && slot is int s)
        {
            var at = layout.FindIndex(l => l.Item == sections[s]);
            if (at >= 0) scrollTarget = layout[at].Y - JumpTop;
            return;
        }

        if (pressed && mouse.X >= 55 * u && mouse.X < 67 * u && HoveredItem(mouse) is int reset && !IsDefault(items[reset]))
        {
            Set(items[reset], DefaultOf(items[reset])!); // stable: clicking the yellow marker resets the setting
            return;
        }
        if ((pressed || right) && mouse.X >= stripW * u && HoveredItem(mouse) is int row)
        {
            var it = items[row];
            switch (it.Kind)
            {
                case Kind.Toggle:
                    Set(it, Config.GetBool(it.Key, it.Def != 0) ? "0" : "1");
                    Game.Skin.Sound("check-" + (Config.GetBool(it.Key, false) ? "on" : "off"))?.Play(BeatmapSamples.EffectVolume);
                    break;
                case Kind.Slider: if (pressed) dragging = row; break;
                case Kind.Choice when it.Values is { Length: > 0 } vals:
                    int k = Array.FindIndex(vals, v => v.Equals(Config.Get(it.Key, it.DefValue.Length > 0 ? it.DefValue : vals[0]), StringComparison.OrdinalIgnoreCase));
                    Set(it, vals[((k < 0 ? 0 : k) + (right ? -1 : 1) + vals.Length) % vals.Length]);
                    Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
                    break;
                case Kind.Key: binding = row; break;
            }
        }
        if (dragging >= 0 && tracks.TryGetValue(dragging, out var tr))
        {
            var it = items[dragging];
            double t = Math.Clamp((mouse.X - tr.X0) / Math.Max(1, tr.X1 - tr.X0), 0, 1);
            Set(it, Show(it.Min + (it.Max - it.Min) * t, it with { Unit = "" }));
        }
    }

    int? HoveredItem(Vector2 mouse)
    {
        if (mouse.X > PanelR * u || mouse.X < stripW * u) return null;
        float cy = mouse.Y / u + scroll;
        foreach (var (i, y, hgt) in layout)
            if (cy >= y && cy < y + hgt && items[i].Kind is not (Kind.Section or Kind.Header)) return i;
        return null;
    }

    // Section buttons: 40-unit slots stacked around the screen's middle, like stable.
    float stripW = 40, stripWait = 300;
    int[] sections = [];
    Rectangle SlotRect(int n) => new(0, (240 - sections.Length * 20 + n * 40) * u, stripW * u, 40 * u);

    int? HoveredSlot(Vector2 mouse)
    {
        if (sections.Length == 0) sections = Enumerable.Range(0, items.Length).Where(i => items[i].Kind == Kind.Section).ToArray();
        for (int n = 0; n < sections.Length; n++)
            if (CheckCollisionPointRec(mouse, SlotRect(n))) return n;
        return null;
    }

    public override void Draw()
    {
        back.Draw();
        if (u == 0) return;
        double t = double.IsNaN(closingAt) ? Math.Clamp((Game.NowMs - openedAt) / 200, 0, 1) : 1 - Math.Clamp((Game.NowMs - closingAt) / 200, 0, 1);
        float slide = (float)(1 - Math.Pow(1 - t, 3));
        float ox = -PanelR * u * (1 - slide);
        var mouse = Input.GetMousePosition();

        DrawRectangleRec(new Rectangle(ox + StripW * u, 0, (PanelR - StripW) * u, h), new Color(0, 0, 0, 175));

        // Content, clipped under the search line.
        BeginScissorMode((int)(ox + StripW * u), 0, (int)((PanelR - StripW) * u), h);
        float size = 13 * u;
        int? hovered = HoveredItem(mouse);
        float ruleTop = float.NaN, prevBottom = 0;
        tracks.Clear();
        // A group's rule runs from its header's top to the bottom of its last row's band.
        void EndRule(float yEnd)
        {
            if (float.IsNaN(ruleTop)) return;
            DrawRectangleRec(new Rectangle(ox + 55 * u, ruleTop, 1.3f * u, yEnd - ruleTop), Rule);
            ruleTop = float.NaN;
        }
        int currentSection = CurrentSection();
        string? tooltip = null;
        foreach (var (i, cyContent, hgt) in layout)
        {
            var it = items[i];
            float y = (cyContent - scroll) * u, rowH = hgt * u, cy = y + rowH / 2;
            if (it.Kind is Kind.Section or Kind.Header) EndRule(prevBottom);
            prevBottom = y + rowH;
            if (it.Kind == Kind.Header) ruleTop = y + 5 * u;
            if (y > h || y + rowH < 0) continue;
            switch (it.Kind)
            {
                case Kind.Section:
                {
                    // stable: 40% unless it's the section in view (measured centre 12.3 below the element's top)
                    float ts = 22 * u;
                    var c = sections.Length > 0 && sections[Math.Max(0, currentSection)] == i ? Title : ColorAlpha(Title, 0.4f);
                    Game.Text(it.Label, ox + 300 * u - Game.TextWidth(it.Label, ts), y + 17.3f * u - ts / 2, ts, c);
                    continue;
                }
                case Kind.Header:
                    Game.TextBold(it.Label, ox + 62 * u, y + 12 * u - size / 2, size, Color.White);
                    continue;
            }
            if (hovered == i) DrawRectangleRec(new Rectangle(ox + StripW * u, y, (PanelR - StripW) * u, rowH), Strip); // measured: opaque (8,8,8)
            if (!IsDefault(it)) markers.Add((y, rowH)); // drawn after the rules, which they add onto
            switch (it.Kind)
            {
                case Kind.Toggle:
                {
                    var c = new Vector2(ox + 67 * u, cy);
                    if (Config.GetBool(it.Key, it.Def != 0)) DrawCircleV(c, 3.8f * u, Pink);
                    else DrawRing(c, 3.0f * u, 3.8f * u, 0, 360, 24, Pink);
                    Game.Text(it.Label, ox + 77.7f * u, cy - size / 2, size, Color.White);
                    break;
                }
                case Kind.Slider:
                {
                    // stable (measured): label only (value in a tooltip), bar 1 unit thick from 5 units past it to 308,
                    // full pink before the knob and half after, stopping at the 10-unit ring with its dark middle.
                    string label = it.Label + ":";
                    Game.Text(label, ox + 62 * u, cy - size / 2, size, Color.White);
                    float x0 = ox + 62 * u + Game.TextWidth(label, size) + 5 * u, x1 = ox + 308 * u;
                    float f = (float)Math.Clamp((Value(it) - it.Min) / (it.Max - it.Min), 0, 1);
                    var knob = new Vector2(x0 + (x1 - x0) * f, cy);
                    DrawRectangleRec(new Rectangle(x0, cy - 0.5f * u, Math.Max(0, knob.X - 5 * u - x0), u), Pink);
                    DrawRectangleRec(new Rectangle(knob.X + 5 * u, cy - 0.5f * u, Math.Max(0, x1 - knob.X - 5 * u), u), ColorAlpha(Pink, 0.5f));
                    DrawCircleV(knob, 5 * u, new Color(45, 22, 27, 255));
                    DrawRing(knob, 4 * u, 5 * u, 0, 360, 32, Pink);
                    tracks[i] = (x0, x1);
                    if (hovered == i || dragging == i) tooltip = Show(Value(it), it);
                    break;
                }
                case Kind.Choice:
                {
                    string label = it.Label + ":";
                    Game.Text(label, ox + 62 * u, cy - size / 2, size, Color.White);
                    float bx = ox + 62 * u + Game.TextWidth(label, size) + 6 * u, bw = ox + 305.3f * u - bx; // measured: box 15 tall to 305.3, chevron at 296
                    DrawRectangleRec(new Rectangle(bx, cy - 7.5f * u, bw, 15 * u), new Color(0, 0, 0, 128));
                    string text = ChoiceText(it);
                    while (text.Length > 3 && Game.TextWidth(text, size) > bw - 18 * u) text = text[..^2] + "...";
                    Game.Text(text, bx + 2 * u, cy - size / 2, size, Color.White);
                    DrawIcon(0xf078, ox + 296 * u, cy, 10.5f * u, Color.White);
                    break;
                }
                case Kind.Key:
                {
                    string label = it.Label + ":";
                    Game.Text(label, ox + 62 * u, cy - size / 2, size, Color.White);
                    float bx = ox + 62 * u + Game.TextWidth(label, size) + 6 * u, bw = ox + 307 * u - bx;
                    DrawRectangleRec(new Rectangle(bx, cy - 8.5f * u, bw, 17 * u), new Color(14, 132, 165, 255));
                    string text = binding == i ? "press a key..." : Config.Key(it.Key, (KeyboardKey)(int)it.Def).ToString();
                    Game.Text(text, bx + bw / 2 - Game.TextWidth(text, size) / 2, cy - size / 2, size, Color.White);
                    break;
                }
            }
        }
        EndRule(layout.Count > 0 ? (layout[^1].Y + layout[^1].H - scroll) * u : 0);
        // stable's "not default" marker: a 6-texel (255,186,0) ramp, alpha 255 -> 5, 2 units a texel, added on.
        BeginBlendMode(BlendMode.Additive);
        foreach (var (my, mh) in markers)
        {
            float tw = 2 * u, x0 = ox + 55 * u;
            static Color C(int k) => new(255, 186, 0, (int)DefaultRamp[k]);
            DrawRectangleRec(new Rectangle(x0, my, tw / 2, mh), C(0));
            for (int k = 0; k < DefaultRamp.Length - 1; k++) DrawRectangleGradientH((int)(x0 + (k + 0.5f) * tw), (int)my, (int)Math.Ceiling(tw), (int)mh, C(k), C(k + 1));
        }
        EndBlendMode();
        markers.Clear();
        // Header: a dark band (fades in over the first 90 units of scroll, up to 50%), "Options" and its blurb centred
        // on the panel, then the search line; all scroll up to StickAt and stay.
        float lift = Math.Min(scroll, StickAt);
        DrawRectangleRec(new Rectangle(ox + StripW * u, -lift * u, 274 * u, 130 * u), new Color(8, 8, 8, (int)(255 * Math.Min(0.5f, scroll / StickAt))));
        float mid = ox + (StripW + 137) * u;
        Game.Text("Options", mid - Game.TextWidth("Options", 20 * u) / 2, (40 - lift) * u, 20 * u, Color.White);
        const string blurb = "Change the way osu! behaves";
        Game.Text(blurb, mid - Game.TextWidth(blurb, 14 * u) / 2, (60 - lift) * u, 14 * u, Pink);
        EndScissorMode();

        float sy = (19 + StickAt - lift) * u;
        DrawIcon(0xf002, ox + 121.6f * u, sy, 15 * u, Color.White);
        Game.Text(search.Length > 0 ? search : "Type to search!", ox + 134 * u, sy - 10 * u, 20 * u, search.Length > 0 ? Color.White : new Color(255, 255, 255, 200));

        // Icon strip: the section in view marked pink on the strip's edge, icons grey (153) unless active or hovered,
        // names at 45 units (size 14) revealed as the strip widens; sections the search hides fade to 20%.
        float sw = stripW * u;
        DrawRectangleRec(new Rectangle(ox, 0, sw, h), Strip);
        int current = currentSection;
        int? hoveredSlot = HoveredSlot(mouse);
        BeginScissorMode((int)ox, 0, (int)sw, h);
        for (int n = 0; n < sections.Length; n++)
        {
            var it = items[sections[n]];
            var r = SlotRect(n);
            float cy = r.Y + r.Height / 2;
            float a = layout.Exists(l => l.Item == sections[n]) ? 1 : 0.2f;
            if (n == current) DrawRectangleRec(new Rectangle(ox + sw - 4 * u, r.Y, 4 * u, 40 * u), Pink); // measured: the full 40-unit slot
            var c = n == current || hoveredSlot == n ? Color.White : new Color(153, 153, 153, 255);
            DrawIcon(it.Icon, ox + 19.6f * u, cy - 1.5f * u, 15 * u, ColorAlpha(c, a));
            string name = it.Label[..1] + it.Label[1..].ToLowerInvariant();
            Game.Text(name, ox + 45 * u, cy - 7 * u, 14 * u, ColorAlpha(Color.White, a));
        }
        EndScissorMode();

        // back tab.
        var b = BackRect;
        b.X += ox;
        DrawRectangleRec(b, BackPink);
        DrawIcon(0xf137, b.X + 10 * u, b.Y + b.Height / 2, 12 * u, Color.White);
        Game.Text("back", b.X + 22 * u, b.Y + b.Height / 2 - 8 * u, 16 * u, Color.White);
        if (tooltip != null) DrawTooltip(tooltip, mouse);
        SongSelect.DrawCursor();
    }

    // stable's tooltip (measured): an 8-unit box down-right of the cursor, black at 50% with a faint white edge.
    void DrawTooltip(string text, Vector2 mouse)
    {
        float ts = 7 * u, bw = Game.TextWidth(text, ts) + 4 * u;
        var box = new Rectangle(mouse.X + 4 * u, mouse.Y + 3.3f * u, bw, 8 * u);
        DrawRectangleRec(box, new Color(0, 0, 0, 128));
        DrawRectangleLinesEx(box, 1, new Color(255, 255, 255, 40));
        Game.Text(text, box.X + 2 * u, box.Y + box.Height / 2 - ts / 2, ts, Color.White);
    }

    static readonly Dictionary<int, string> iconText = []; // drawn every frame (menu music controls): one string each
    internal static void DrawIcon(int codepoint, float cx, float cy, float size, Color colour)
    {
        if (!iconText.TryGetValue(codepoint, out var s)) iconText[codepoint] = s = char.ConvertFromUtf32(codepoint);
        var m = MeasureTextEx(Game.Icons, s, size, 0);
        DrawTextCodepoint(Game.Icons, codepoint, new Vector2(cx - m.X / 2, cy - m.Y / 2), size, colour);
    }
}
