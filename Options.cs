using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's options panel over the screen it was opened from (main menu or song select), laid out from stable
// captures in 640x480 units: a black icon strip (sections), the translucent panel, search on top, big section
// titles, bold sub-headers with a rule, circle checkboxes, line sliders, dropdowns, buttons, a pink "back" tab.
// Every section and setting stable shows is here; the ones this client reads apply live. All changes go to
// remake.cfg, never stable's own cfg. Online parts (sign in, updates, chat) are shown but do nothing: this client
// never connects anywhere.
sealed class Options : Screen
{
    enum Kind { Section, Header, Toggle, Slider, Choice, Key, Button, Text, Warning, Field, Preview, Version }

    sealed record Item(Kind Kind, string Label, string Key = "", double Min = 0, double Max = 0, double Def = 0,
        string Format = "0", string Unit = "", string[]? Values = null, string[]? Names = null, string DefValue = "", int Icon = 0,
        Color Colour = default, Action? Click = null, Func<bool>? Visible = null);

    static readonly Color Pink = new(235, 117, 140, 255), Title = new(70, 175, 206, 255), Rule = new(68, 65, 77, 255),
        Strip = new(8, 8, 8, 255), Panel = new(9, 9, 9, 191), Yellow = new(254, 197, 23, 255),
        Blue = new(14, 132, 165, 255), Green = new(117, 162, 25, 255), Grey = new(107, 107, 107, 255), ButtonPink = new(214, 96, 119, 255),
        BoxHover = new(186, 17, 119, 255), FieldEdge = new(179, 179, 179, 255);

    static Item S(string label, int icon) => new(Kind.Section, label, Icon: icon);
    static Item H(string label) => new(Kind.Header, label);
    static Item T(string label, string key, bool def = false) => new(Kind.Toggle, label, key, Def: def ? 1 : 0);
    static Item B(string label, Color c, Action? click = null) => new(Kind.Button, label, Colour: c, Click: click);
    static Item F(string label) => new(Kind.Field, label);

    static void Explore(string dir)
    {
        if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    readonly Screen back;
    readonly SongSelect songs;
    readonly Item[] items;
    string? jumpTo;
    string search = "";
    float scroll = lastScroll, scrollTarget = lastScroll;
    static float lastScroll; // stable reopens the panel where it was left
    int dragging = -1, binding = -1, dropdown = -1, dropFirst;
    bool showKeys;
    double openedAt = Game.NowMs, closingAt = double.NaN;

    // Layout of the visible items (content y, height), rebuilt each frame; slider tracks and dropdown boxes from the last draw.
    readonly List<(int Item, float Y, float H)> layout = [];
    readonly Dictionary<int, (float X0, float X1)> tracks = [];
    readonly Dictionary<int, Rectangle> boxes = [];

    public Options(Screen back, SongSelect songs, string? jumpTo = null)
    {
        this.back = back;
        this.songs = songs;
        this.jumpTo = jumpTo;
        var skinDir = Path.Combine(Config.OsuDir, "Skins");
        var skins = Directory.Exists(skinDir) ? Directory.GetDirectories(skinDir).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
        int m = GetCurrentMonitor();
        var resolutions = new[] { (800, 600), (1024, 768), (1280, 720), (1366, 768), (1600, 900), (1920, 1080), (2560, 1440), (3840, 2160), (GetMonitorWidth(m), GetMonitorHeight(m)) }
            .Where(r => r.Item1 <= GetMonitorWidth(m) && r.Item2 <= GetMonitorHeight(m)).Distinct().OrderBy(r => r.Item1).Select(r => $"{r.Item1}x{r.Item2}").ToArray();
        string build = $"b{File.GetLastWriteTime(Environment.ProcessPath ?? AppContext.BaseDirectory):yyyyMMdd}";
        items =
        [
            S("GENERAL", 0xf013),
            H("SIGN IN"),
            F("Username"),
            F("Password"),
            T("Remember Username", "SaveUsername", true),
            T("Remember Password", "SavePassword"),
            B("Sign In", Blue),
            B("Create an account", Blue),
            H("LANGUAGE"),
            new(Kind.Choice, "Select language:", "Language", Values: ["en"], Names: ["English"], DefValue: "en"),
            T("Prefer metadata in original language", "ShowUnicode"),
            T("Use alternative font for chat display", "AlternativeChatFont"),
            H("UPDATES"),
            new(Kind.Choice, "Release stream:", "_ReleaseStream", Values: ["Stable", "Beta", "CuttingEdge"], Names: ["Stable", "Beta", "Cutting Edge"], DefValue: "Stable"),
            B("Try out the new osu!(lazer) client!", Green),
            B("Open osu! folder", Blue, () => Explore(Config.OsuDir)),

            S("GRAPHICS", 0xf108),
            H("RENDERER"),
            new(Kind.Choice, "Frame limiter:", "FrameSync", Values: ["VSync", "PowerSaving", "Optimal", "Unlimited"],
                Names: ["VSync", "Power saving (2x refresh)", "Optimal (8x refresh)", "Unlimited (gameplay)"], DefValue: "Optimal"),
            new(Kind.Warning, "Using unlimited frame limiter can lead to stutters. \"Optimal\" is recommended.", Visible: () => Config.Get("FrameSync", "Optimal") == "Unlimited"),
            T("Show FPS counter", "FpsCounter"),
            T("Compatibility mode", "CompatibilityContext"),
            T("Reduce dropped frames", "ForceFrameFlush"),
            T("Detect performance issues", "DetectPerformanceIssues", true),
            H("LAYOUT"),
            new(Kind.Choice, "Resolution:", "Resolution", Values: resolutions, DefValue: $"{Game.Resolution().W}x{Game.Resolution().H}"),
            T("Fullscreen mode", "Fullscreen", true),
            new(Kind.Warning, "Running without fullscreen mode will increase your input latency!", Visible: () => Game.DisplayMode() != "Fullscreen"),
            T("Minimise on alt-tab during fullscreen", "MinimiseOnAltTabFullscreen", true),
            T("Render at native resolution", "HighResolution"),
            new(Kind.Slider, "Horizontal position", "LetterboxPositionX", -100, 100, 0, "0", "%"),
            new(Kind.Slider, "Vertical position", "LetterboxPositionY", -100, 100, 0, "0", "%"),
            H("DETAIL SETTINGS"),
            T("Snaking sliders", "SnakingSliders", true),
            T("Background video", "Video", true),
            T("Storyboards", "ShowStoryboard", true),
            T("Combo bursts", "ComboBurst"),
            T("Hit lighting", "HitLighting", true),
            T("Shaders", "Shaders"),
            T("Softening filter", "BloomSoftening"),
            new(Kind.Choice, "Screenshot format:", "ScreenshotFormat", Values: ["Jpg", "Png"], Names: ["JPEG (web-friendly)", "PNG (lossless)"], DefValue: "Jpg"),
            H("MAIN MENU"),
            T("Snow", "MenuSnow"),
            T("Parallax", "MenuParallax", true),
            T("Menu tips", "ShowMenuTips", true),
            T("Interface voices", "MenuVoice", true),
            T("osu! music theme", "MenuMusic", true),
            new(Kind.Choice, "Seasonal backgrounds", "SeasonalBackgrounds", Values: ["Never", "Sometimes", "Always"], DefValue: "Sometimes"),
            H("SONG SELECT"),
            T("Show thumbnails", "SongSelectThumbnails", true),

            S("GAMEPLAY", 0xf10c),
            H("GENERAL"),
            new(Kind.Slider, "Background dim:", "DimLevel", 0, 100, 80, "0", "%"),
            T("Don't change dim level during breaks", "IHateHavingFun"),
            T("Show leaderboard during gameplay", "ScoreboardVisible", true),
            new(Kind.Choice, "Progress display:", "ProgressBarType", Values: ["Off", "Pie", "TopRight", "BottomRight", "Bottom"],
                Names: ["Off", "Top-Right (Pie)", "Top-Right (Bar)", "Bottom-Right (Bar)", "Bottom (Bar)"], DefValue: "Pie"),
            new(Kind.Choice, "Score meter type:", "ScoreMeter", Values: ["None", "Colour", "Error"], Names: ["None", "Colour", "Hit error"], DefValue: "Error"),
            new(Kind.Slider, "Score meter size:", "ScoreMeterScale", 0.5, 2, 1, "0.0", "x"),
            T("Always show key overlay", "KeyOverlay"),
            T("Show approach circle on first \"Hidden\" object", "HiddenShowFirstApproach", true),
            T("Scale osu!mania scroll speed with BPM", "ManiaSpeedBPMScale"),
            T("Remember osu!mania scroll speed per beatmap", "UsePerBeatmapManiaSpeed"),
            H("SONG SELECT"),
            new(Kind.Slider, "Display beatmaps from", "DisplayStarsMinimum", 0, 10, 0, "0.0", " stars"),
            new(Kind.Slider, "up to", "DisplayStarsMaximum", 0, 10, 10, "0.0", " stars"),

            S("AUDIO", 0xf026),
            H("DEVICES"),
            new(Kind.Choice, "Output device:", "AudioDevice", Values: ["Default"], DefValue: "Default"),
            T("Audio compatibility mode", "AudioCompatibility"),
            H("VOLUME"),
            new(Kind.Slider, "Master:", "VolumeUniversal", 0, 100, 100, "0", "%"),
            new(Kind.Slider, "Music:", "VolumeMusic", 0, 100, 80, "0", "%"),
            new(Kind.Slider, "Effect:", "VolumeEffect", 0, 100, 80, "0", "%"),
            T("Ignore beatmap hitsounds", "IgnoreBeatmapSamples"),
            H("OFFSET ADJUSTMENT"),
            new(Kind.Slider, "Universal offset:", "RemakeOffset", -300, 300, 0, "0", "ms"),
            B("Offset wizard", Grey),

            S("SKIN", 0xf1fc),
            H("SKIN"),
            new(Kind.Preview, ""),
            new(Kind.Choice, "Current skin:", "Skin", Values: skins),
            B("Preview gameplay", ButtonPink),
            B("Open current skin folder", Blue, () => Explore(Path.Combine(Config.OsuDir, "Skins", Config.Get("Skin", "")))),
            B("Export as .osk", Blue),
            T("Ignore all beatmap skins", "IgnoreBeatmapSkins"),
            T("Use skin's sound samples", "SkinSamples", true),
            T("Use Taiko skin for Taiko mode", "UseTaikoSkin"),
            T("Always use skin cursor", "UseSkinCursor"),
            new(Kind.Slider, "Cursor size:", "CursorSize", 0.5, 2, 1, "0.00", "x"),
            T("Automatic cursor size", "AutomaticCursorSizing"),
            T("Use combo colour as tint for slider ball", "ComboColourSliderBall", true),

            S("INPUT", 0xf11b),
            H("MOUSE"),
            new(Kind.Slider, "Sensitivity:", "MouseSpeed", 0.4, 6, 1, "0.00", "x"),
            T("Raw input", "RawInput"),
            T("Map absolute raw input to the osu! window", "AbsoluteToOsuWindow"),
            new(Kind.Choice, "Confine mouse cursor:", "ConfineMouse", Values: ["Never", "Fullscreen", "Always"],
                Names: ["Never", "Only when fullscreen", "Always"], DefValue: "Fullscreen"),
            T("Disable mouse wheel in play mode", "MouseDisableWheel"),
            T("Disable mouse buttons in play mode", "MouseDisableButtons"),
            T("Cursor ripples", "CursorRipple"),
            H("KEYBOARD"),
            B("Change keyboard bindings", Blue, () => showKeys = !showKeys),
            new(Kind.Key, "Left click:", "keyOsuLeft", Def: (int)KeyboardKey.Z, Visible: () => showKeys),
            new(Kind.Key, "Right click:", "keyOsuRight", Def: (int)KeyboardKey.X, Visible: () => showKeys),
            B("osu!mania layout", Blue),
            H("OTHER"),
            T("OS TabletPC support", "Tablet"),
            T("Wiimote/TaTaCon Drum support", "Wiimote"),

            S("EDITOR", 0xf040),
            H("GENERAL"),
            T("Background video", "EditorVideo", true),
            T("Always use default skin", "EditorDefaultSkin"),
            T("Snaking sliders", "EditorSnakingSliders", true),
            T("Hit animations", "EditorHitAnimations"),
            T("Follow points", "EditorFollowPoints", true),
            T("Stacking", "EditorStacking", true),

            S("ONLINE", 0xf0ac),
            H("ALERTS AND PRIVACY"),
            T("Chat ticker", "Ticker"),
            T("Automatically hide chat during gameplay", "AutoChatHide", true),
            T("Show a notification popup when someone says your name", "ChatHighlightName", true),
            T("Show chat message notifications", "ChatMessageNotification", true),
            T("Play a sound when someone says your name", "ChatAudibleHighlight", true),
            T("Share your city location with others", "DisplayCityLocation"),
            T("Show spectators", "ShowSpectators", true),
            T("Automatically link beatmaps to spectators", "AutoSendNowPlaying", true),
            T("Show notification popups instantly during gameplay", "PopupDuringGameplay", true),
            T("Show notification popups when friends change status", "NotifyFriends", true),
            T("Allow multiplayer game invites from all users", "AllowPublicInvites", true),
            H("INTEGRATION"),
            T("Discord Rich Presence", "DiscordRichPresence", true),
            T("Integrate with Yahoo! status display", "YahooIntegration"),
            T("Integrate with MSN Live status display", "MsnIntegration"),
            T("Automatically start osu!direct downloads", "AutomaticDownload", true),
            T("Prefer no-video downloads", "AutomaticDownloadNoVideo"),
            H("IN-GAME CHAT"),
            T("Filter offensive words", "ChatFilter"),
            T("Filter foreign characters", "ChatRemoveForeign"),
            T("Log private messages", "LogPrivateMessages"),
            T("Block private messages from non-friends", "BlockNonFriendPM"),
            F("Chat ignore list (space-separated list)"),
            F("Chat highlight words (space-separated list)"),

            S("MAINTENANCE", 0xf0ad),
            H("GENERAL"),
            B("Delete all unranked maps", Blue),
            B("Repair folder permissions", Blue),
            B("Mark all maps as played", Blue),
            B("Run osu! updater", Blue),
            new(Kind.Version, build),
        ];
    }

    float u;
    int w, h;
    // ContentTop: first section, below stable's header (title 40, blurb 60, search 100) when not scrolled (stable's
    // 140, -1 measured at 1080p). Header pieces scroll with the content but stop after StickAt, which leaves the search line
    // pinned at the top. JumpTop: where a jump puts a section's band (stable: element at 70).
    const float StripW = 40, PanelR = 313, ContentTop = 139, StickAt = 90, JumpTop = 65, RowH = 13.333f, LineH = 12, TextSize = 12.3f;

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

    // Fullscreen follows the display mode in use rather than a stored flag.
    bool On(Item it) => it.Key == "Fullscreen" ? Game.DisplayMode() == "Fullscreen" : Config.GetBool(it.Key, it.Def != 0);

    string ChoiceValue(Item it) => Config.Get(it.Key, it.DefValue.Length > 0 ? it.DefValue : it.Values is { Length: > 0 } vs ? vs[0] : "");

    string NameOf(Item it, int idx) => it.Names != null && idx >= 0 ? it.Names[idx] : it.Values![idx];

    string ChoiceText(Item it)
    {
        string v = ChoiceValue(it);
        int idx = it.Values == null ? -1 : Array.FindIndex(it.Values, x => x.Equals(v, StringComparison.OrdinalIgnoreCase));
        string text = idx >= 0 ? NameOf(it, idx) : v;
        return it.Key == "Resolution" && Game.DisplayMode() == "Borderless" ? text + " (borderless)" : text;
    }

    static readonly byte[] DefaultRamp = [255, 160, 120, 80, 40, 5];
    readonly List<(float Y, float H)> markers = [];

    // Default as stored, or null when there's none to go back to (display settings default to what's in use).
    static string? DefaultOf(Item it) => it.Kind switch
    {
        Kind.Toggle => it.Def != 0 ? "1" : "0",
        Kind.Slider => Show(it.Def, it with { Unit = "" }),
        Kind.Choice when it.DefValue.Length > 0 && it.Key != "Resolution" => it.DefValue,
        Kind.Key => ((KeyboardKey)(int)it.Def).ToString(),
        _ => null,
    };

    bool IsDefault(Item it) => DefaultOf(it) is not { } d || it.Kind switch
    {
        Kind.Toggle => On(it) == (it.Def != 0),
        Kind.Slider => Math.Abs(Value(it) - it.Def) < 1e-6,
        Kind.Choice => Config.Get(it.Key, d).Equals(d, StringComparison.OrdinalIgnoreCase),
        _ => Config.Key(it.Key, (KeyboardKey)(int)it.Def) == (KeyboardKey)(int)it.Def,
    };

    void Set(Item it, string value)
    {
        int m = GetCurrentMonitor();
        bool MonitorSized() => Game.Resolution() is var (rw, rh) && rw >= GetMonitorWidth(m) && rh >= GetMonitorHeight(m);
        if (it.Key == "Fullscreen")
        {
            Config.SetOwn("DisplayMode", value == "1" ? "Fullscreen" : MonitorSized() ? "Borderless" : "Windowed");
            Game.ApplyDisplay();
            return;
        }
        if (Config.Get(it.Key, "") == value) return;
        Config.SetOwn(it.Key, value);
        switch (it.Key)
        {
            case "VolumeUniversal" or "VolumeMusic": songs.ApplyMusicVolume(); break;
            case "FrameSync":
                if (value == "VSync") SetWindowState(ConfigFlags.VSyncHint);
                else ClearWindowState(ConfigFlags.VSyncHint);
                break;
            case "Resolution":
                // stable: the monitor's own size outside fullscreen is borderless.
                if (Game.DisplayMode() != "Fullscreen") Config.SetOwn("DisplayMode", MonitorSized() ? "Borderless" : "Windowed");
                Game.ApplyDisplay();
                break;
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
        lastScroll = search.Length > 0 ? lastScroll : scrollTarget;
    }

    // Labels wrap like stable's (toggles and plain text): lines of 12 units, up to the panel's right edge.
    readonly Dictionary<int, string[]> wrapped = [];
    float wrappedU;
    string[] Lines(int i)
    {
        if (wrappedU != u) { wrapped.Clear(); wrappedU = u; }
        if (wrapped.TryGetValue(i, out var cached)) return cached;
        var it = items[i];
        if (it.Kind is not (Kind.Toggle or Kind.Text or Kind.Warning)) return wrapped[i] = [it.Label];
        float max = (305 - TextX(it)) * u, size = TextSize * u;
        var lines = new List<string>();
        string line = "";
        foreach (var word in it.Label.Split(' '))
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Game.TextWidth(next, size) > max) { lines.Add(line); line = word; }
            else line = next;
        }
        lines.Add(line);
        return wrapped[i] = lines.ToArray();
    }

    static float TextX(Item it) => it.Kind == Kind.Toggle ? 77.7f : 62;

    // Element heights (measured at 1080p): rows 13.33 plus 12 a wrapped line, buttons 19.8 (a 17-unit bar 2.4 down),
    // text fields 31.8 (label, then the box), the skin preview 80.
    float Height(int i) => items[i].Kind switch
    {
        Kind.Section => 10,
        Kind.Header => 14,
        Kind.Button => 19.8f,
        Kind.Field => 31.8f,
        Kind.Preview => 80,
        _ => RowH + LineH * (Lines(i).Length - 1),
    };

    static bool IsGroup(Kind k) => k is Kind.Section or Kind.Header;

    // Items shown for the current search (sections/headers kept when anything under them matches).
    void BuildLayout()
    {
        layout.Clear();
        bool Match(Item it) => (it.Visible?.Invoke() ?? true) && (search.Length == 0 || it.Label.Length > 0 && it.Label.Contains(search, StringComparison.OrdinalIgnoreCase));
        // stable's stacking: each element is its height plus 10 apart (section 10, group header 14 after a 20-unit
        // gap, row 13.33 - measured, the code's 13.125 grows with the text); entries hold the row band (5 above).
        float y = ContentTop;
        for (int i = 0; i < items.Length; i++)
        {
            var it = items[i];
            if (IsGroup(it.Kind))
            {
                bool any = false;
                for (int k = i + 1; k < items.Length && items[k].Kind != Kind.Section && !(it.Kind == Kind.Header && items[k].Kind == Kind.Header); k++)
                    if (!IsGroup(items[k].Kind) && Match(items[k])) { any = true; break; }
                if (!any) continue;
            }
            else if (!Match(it)) continue;
            if (it.Kind == Kind.Header) y += 20;
            else if (it.Kind == Kind.Version) y += 10;
            float hgt = Height(i);
            layout.Add((i, y - 5, hgt + 10));
            y += hgt + 10;
        }
    }

    float ContentHeight => layout.Count == 0 ? 0 : layout[^1].Y + layout[^1].H + 115; // measured: stable scrolls the end up to 115 above the bottom

    readonly BackButton backButton = new();

    // An open dropdown: its options under the box, as many as fit on screen (the wheel scrolls the rest).
    float OptionH => 15 * u;
    (Rectangle List, int Count) DropList()
    {
        var box = boxes.TryGetValue(dropdown, out var b) ? b : default;
        int n = items[dropdown].Values!.Length;
        int count = Math.Max(1, Math.Min(n, (int)((h - box.Y - box.Height) / OptionH)));
        return (new Rectangle(box.X, box.Y + box.Height, box.Width, count * OptionH), count);
    }

    public override void Update(double dtMs)
    {
        w = GetScreenWidth(); h = GetScreenHeight();
        u = h / 480f;
        songs.Ambient();
        if (back is MainMenu menu) menu.UpdateCovered(dtMs); // the cookie keeps beating behind the panel
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
        var mouse = Input.GetMousePosition();
        bool pressed = Input.IsMouseButtonPressed(MouseButton.Left);
        if (dropdown >= 0)
        {
            var it = items[dropdown];
            var (list, count) = DropList();
            dropFirst = Math.Clamp(dropFirst - (int)VolumeControl.Wheel, 0, it.Values!.Length - count);
            if (Input.IsKeyPressed(KeyboardKey.Escape)) dropdown = -1;
            else if (pressed)
            {
                if (CheckCollisionPointRec(mouse, list))
                {
                    Set(it, it.Values[Math.Min(it.Values.Length - 1, dropFirst + (int)((mouse.Y - list.Y) / OptionH))]);
                    Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
                }
                dropdown = -1;
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

        if (pressed && (mouse.X > PanelR * u || CheckCollisionPointRec(mouse, BackButton.Rect(h)))) { Close(); return; }
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
        if (pressed && mouse.X >= stripW * u && HoveredItem(mouse) is int row)
        {
            var it = items[row];
            switch (it.Kind)
            {
                case Kind.Toggle:
                    bool on = !On(it);
                    Set(it, on ? "1" : "0");
                    Game.Skin.Sound("check-" + (on ? "on" : "off"))?.Play(BeatmapSamples.EffectVolume);
                    break;
                case Kind.Slider: dragging = row; break;
                case Kind.Choice when it.Values is { Length: > 0 } vals && boxes.TryGetValue(row, out var box) && CheckCollisionPointRec(mouse, box):
                    dropdown = row;
                    dropFirst = Math.Max(0, Array.FindIndex(vals, v => v.Equals(ChoiceValue(it), StringComparison.OrdinalIgnoreCase)));
                    dropFirst = Math.Clamp(dropFirst, 0, vals.Length - DropList().Count);
                    Game.Skin.Sound("click-short")?.Play(BeatmapSamples.EffectVolume);
                    break;
                case Kind.Key: binding = row; break;
                case Kind.Button:
                    it.Click?.Invoke();
                    Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
                    break;
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
            if (cy >= y && cy < y + hgt && items[i].Kind is Kind.Toggle or Kind.Slider or Kind.Choice or Kind.Key or Kind.Button or Kind.Field or Kind.Text or Kind.Warning) return i; // stable highlights warning rows too
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

    static Color Lighter(Color c, int by) => new(Math.Min(255, c.R + by), Math.Min(255, c.G + by), Math.Min(255, c.B + by), c.A);

    public override void Draw()
    {
        back.Draw();
        if (u == 0) return;
        double t = double.IsNaN(closingAt) ? Math.Clamp((Game.NowMs - openedAt) / 200, 0, 1) : 1 - Math.Clamp((Game.NowMs - closingAt) / 200, 0, 1);
        float slide = (float)(1 - Math.Pow(1 - t, 3));
        float ox = -PanelR * u * (1 - slide);
        var mouse = Input.GetMousePosition();

        DrawRectangleRec(new Rectangle(ox + StripW * u, 0, (PanelR - StripW) * u, h), Panel); // measured: (9,9,9) at 75%

        // Content, clipped under the search line.
        BeginScissorMode((int)(ox + StripW * u), 0, (int)((PanelR - StripW) * u), h);
        float size = TextSize * u;
        int? hovered = dropdown >= 0 ? null : HoveredItem(mouse);
        float ruleTop = float.NaN, prevBottom = 0;
        tracks.Clear();
        boxes.Clear();
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
            float y = (cyContent - scroll) * u, rowH = hgt * u, cy = y + rowH / 2, top = y + 5 * u;
            if (IsGroup(it.Kind) || it.Kind == Kind.Version) EndRule(prevBottom);
            prevBottom = y + rowH;
            if (it.Kind == Kind.Header) ruleTop = y + 5 * u;
            if (y > h || y + rowH < 0) continue;
            switch (it.Kind)
            {
                case Kind.Section:
                {
                    // stable: 40% unless it's the section in view (measured centre 12.3 below the element's top)
                    float ts = 24.6f * u;
                    var c = sections.Length > 0 && sections[Math.Max(0, currentSection)] == i ? Title : ColorAlpha(Title, 0.4f);
                    Game.Text(it.Label, ox + 300 * u - Game.TextWidth(it.Label, ts), y + 17.3f * u - ts / 2, ts, c);
                    continue;
                }
                case Kind.Header:
                    Game.TextBold(it.Label, ox + 62 * u, y + 12 * u - size / 2, size, Color.White);
                    continue;
                case Kind.Version:
                    Game.Text(it.Label, ox + 172 * u - Game.TextWidth(it.Label, size) / 2, cy - size / 2, size, Color.White);
                    continue;
                case Kind.Preview:
                    DrawSkinPreview(ox, top);
                    continue;
            }
            if (hovered == i) DrawRectangleRec(new Rectangle(ox + StripW * u, y, (PanelR - StripW) * u, rowH), Strip); // measured: opaque (8,8,8)
            if (hovered == i && Tips.TryGetValue(it.Key, out var tip)) tooltip = tip;
            if (it.Kind is Kind.Toggle or Kind.Slider or Kind.Choice or Kind.Key && !IsDefault(it)) markers.Add((y, rowH)); // drawn after the rules, which they add onto
            switch (it.Kind)
            {
                case Kind.Toggle or Kind.Text or Kind.Warning:
                {
                    // wrapped lines 12 apart, centred on the row; the circle sits in the middle of them
                    var lines = Lines(i);
                    float first = cy - (lines.Length - 1) * LineH / 2 * u, x = ox + TextX(it) * u;
                    if (it.Kind == Kind.Toggle)
                    {
                        var c = new Vector2(ox + 67 * u, cy);
                        if (On(it)) DrawCircleV(c, 3.8f * u, Pink);
                        else DrawRing(c, 3.0f * u, 3.8f * u, 0, 360, 24, Pink);
                    }
                    var colour = it.Kind == Kind.Warning ? Yellow : Color.White;
                    for (int k = 0; k < lines.Length; k++) Game.Text(lines[k], x, first + k * LineH * u - size / 2, size, colour);
                    break;
                }
                case Kind.Slider:
                {
                    // stable (measured): label only (value in a tooltip), bar 1 unit thick from 5 units past it to 308,
                    // full pink before the knob and half after, stopping at the 10-unit ring with its dark middle.
                    Game.Text(it.Label, ox + 62 * u, cy - size / 2, size, Color.White);
                    float x0 = ox + 62 * u + Game.TextWidth(it.Label, size) + 5 * u, x1 = ox + 308 * u;
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
                    // measured: box 15 tall to 305.3, chevron at 296; pink (186,17,119) while hovered or open
                    Game.Text(it.Label, ox + 62 * u, cy - size / 2, size, Color.White);
                    float bx = ox + 62 * u + Game.TextWidth(it.Label, size) + 6 * u, bw = ox + 305.3f * u - bx;
                    var box = new Rectangle(bx, cy - 7.5f * u, bw, 15 * u);
                    boxes[i] = box;
                    bool lit = dropdown == i || hovered == i && CheckCollisionPointRec(mouse, box);
                    DrawRectangleRec(box, lit ? BoxHover : new Color(0, 0, 0, 128));
                    string text = ChoiceText(it);
                    while (text.Length > 3 && Game.TextWidth(text, size) > bw - 18 * u) text = text[..^4] + "...";
                    Game.Text(text, bx + 2 * u, cy - size / 2, size, Color.White);
                    DrawIcon(0xf078, ox + 296 * u, cy, 10.5f * u, Color.White);
                    break;
                }
                case Kind.Key:
                {
                    Game.Text(it.Label, ox + 62 * u, cy - size / 2, size, Color.White);
                    float bx = ox + 62 * u + Game.TextWidth(it.Label, size) + 6 * u, bw = ox + 302.2f * u - bx;
                    DrawRectangleRec(new Rectangle(bx, cy - 8.5f * u, bw, 17 * u), hovered == i ? Lighter(Blue, 20) : Blue);
                    string text = binding == i ? "press a key..." : Config.Key(it.Key, (KeyboardKey)(int)it.Def).ToString();
                    Game.Text(text, bx + bw / 2 - Game.TextWidth(text, size) / 2, cy - size / 2, size, Color.White);
                    break;
                }
                case Kind.Button:
                {
                    // measured: 62.2 to 302.2, 17 tall, the top half shading up from 85%; 20 lighter while hovered
                    var c = hovered == i ? Lighter(it.Colour, 20) : it.Colour;
                    var dark = new Color((int)(c.R * 0.85f), (int)(c.G * 0.85f), (int)(c.B * 0.85f), 255);
                    float bx = ox + 62.2f * u, by = top + 2.4f * u, bw = 240 * u, bh = 17 * u;
                    int y0 = (int)by, y1 = (int)(by + bh), ym = (y0 + y1) / 2;
                    DrawRectangleGradientV((int)bx, y0, (int)bw, ym - y0, dark, c);
                    DrawRectangle((int)bx, ym, (int)bw, y1 - ym, c);
                    Game.Text(it.Label, bx + bw / 2 - Game.TextWidth(it.Label, size) / 2, by + bh / 2 - size / 2 - u, size, Color.White);
                    break;
                }
                case Kind.Field:
                {
                    // label, then an empty box, white at 5% with a grey 1.33-unit edge (60 to 298.7, 19.1 to 32.4 below the top)
                    Game.Text(it.Label, ox + 62 * u, top + RowH / 2 * u - size / 2, size, Color.White);
                    var field = new Rectangle(ox + 60 * u, top + 19.1f * u, 238.7f * u, 13.3f * u);
                    DrawRectangleRec(field, new Color(255, 255, 255, 13));
                    DrawRectangleLinesEx(field, 1.333f * u, FieldEdge);
                    break;
                }
            }
        }
        EndRule(layout.Count > 0 && items[layout[^1].Item].Kind != Kind.Version ? (layout[^1].Y + layout[^1].H - scroll) * u : prevBottom);
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

        if (dropdown >= 0) DrawDropdown(mouse, size);

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

        backButton.Draw(h, mouse);
        if (tooltip != null) DrawTooltip(tooltip, mouse);
        SongSelect.DrawCursor();
    }

    void DrawDropdown(Vector2 mouse, float size)
    {
        var it = items[dropdown];
        var (list, count) = DropList();
        DrawRectangleRec(list, new Color(0, 0, 0, 230));
        string current = ChoiceValue(it);
        for (int k = 0; k < count && dropFirst + k < it.Values!.Length; k++)
        {
            int idx = dropFirst + k;
            var r = new Rectangle(list.X, list.Y + k * OptionH, list.Width, OptionH);
            if (CheckCollisionPointRec(mouse, r)) DrawRectangleRec(r, BoxHover);
            var c = it.Values[idx].Equals(current, StringComparison.OrdinalIgnoreCase) ? Pink : Color.White;
            Game.Text(NameOf(it, idx), r.X + 2 * u, r.Y + r.Height / 2 - size / 2, size, c);
        }
    }

    // The skin section's preview (measured at 1080p): a numbered hit circle and the 50/100/300 judgements.
    void DrawSkinPreview(float ox, float top)
    {
        var skin = Game.Skin;
        float cy = top + 41.2f * u;
        var centre = new Vector2(ox + 107.6f * u, cy);
        float cs = 49 * u / 128, js = 63.8f * u / 128; // 128 is a circle's SD size; judgements at their resting size
        if (skin.Tex("hitcircle") is { } body) body.Draw(centre, cs, new Color(4, 76, 153, 255));
        if (skin.Glyph(skin.HitCirclePrefix, '1') is { } one) one.Draw(centre, cs * 0.8f, Color.White);
        skin.Tex("hitcircleoverlay")?.Draw(centre, cs, Color.White);
        (string Name, float X)[] judgements = [("hit50", 156.7f), ("hit100", 205.6f), ("hit300", 254.9f)];
        foreach (var (name, x) in judgements)
            if ((skin.Tex(name) ?? skin.Frame(name, 0)) is { } j) j.Draw(new Vector2(ox + x * u, cy), js, Color.White);
    }

    // stable's tooltip (measured): an 8-unit box down-right of the cursor, black at 50% with a faint white edge;
    // each further line adds 8.9 units.
    internal static void DrawTooltip(string text, Vector2 mouse)
    {
        float u = GetScreenHeight() / 480f, ts = 7 * u, lineH = 8.9f * u;
        var lines = text.Split('\n');
        float bw = lines.Max(l => Game.TextWidth(l, ts)) + 4 * u;
        var box = new Rectangle(mouse.X + 4 * u, mouse.Y + 3.3f * u, bw, 8 * u + (lines.Length - 1) * lineH);
        DrawRectangleRec(box, new Color(0, 0, 0, 128));
        DrawRectangleLinesEx(box, 1, new Color(255, 255, 255, 40));
        for (int i = 0; i < lines.Length; i++) Game.Text(lines[i], box.X + 2 * u, box.Y + 4 * u + i * lineH - ts / 2, ts, Color.White);
    }

    // stable's descriptions, by setting
    static readonly Dictionary<string, string> Tips = new()
    {
        ["ShowUnicode"] = "Where available, song titles will be shown in their native language (and character-set).",
        ["AlternativeChatFont"] = "For people who prefer a less stylised font, this sets in-game chat to use Tahoma.",
        ["FpsCounter"] = "Show a subtle FPS counter in the bottom right corner of the screen.",
        ["CompatibilityContext"] = "If you're having trouble running osu!",
        ["ForceFrameFlush"] = "Users which experience regular dropped/stutter frames should try turning this on. May reduce frame rate slightly.",
        ["DetectPerformanceIssues"] = "osu! will actively look for and alert you of issues with your PC which could be hurting your game performance.",
        ["Fullscreen"] = "Switches to dedicated fullscreen mode. Decreases cursor latency in Windows 7+.",
        ["HighResolution"] = "Always use the full native resolution but display osu! in a smaller centered portion of the screen.\nUseful to get the low latency of full screen with a smaller game resolution.",
        ["SnakingSliders"] = "Sliders gradually snake out from their starting point.\nThis should run fine unless you have a low-end PC.",
        ["Video"] = "Enables background video playback.\nIf you get a large amount of lag on beatmaps with video, try disabling this feature.",
        ["ShowStoryboard"] = "Show storyboards in the background of beatmaps. These usually contain story elements, lyrics or special effects.",
        ["ComboBurst"] = "A character image bursts from the side of the screen at combo milestones.",
        ["HitLighting"] = "Adds a subtle glow behind hit explosions which lights the playfield.",
        ["Shaders"] = "Enables shader special effects in gameplay (epic flashes, blurring, tinting and more!).\nHighly recommended, but requires Pixel Shader 2.0 support and a relatively powerful graphics card.\nIf your card is unsupported, this will be automatically disabled.",
        ["BloomSoftening"] = "Adds a softening touch to visuals game-wide. Some people like this, others hate it ;)",
        ["MenuSnow"] = "Little representations of your current game mode will fall from the sky.",
        ["MenuParallax"] = "Add a parallax effect based on the current cursor position.",
        ["ShowMenuTips"] = "The main menu will display tips on how to get the most out of osu!.",
        ["MenuVoice"] = "osu! will greet you when you start and quit.",
        ["MenuMusic"] = "osu! will play themed music throughout the game, instead of using random beatmaps.",
        ["SongSelectThumbnails"] = "Display a preview of each beatmap's background. Requires skin support (version 2.2+).",
        ["IHateHavingFun"] = "Makes the background basically impossible to see during breaks.\nNot recommended.",
        ["ScoreboardVisible"] = "Affects both solo and multiplayer.",
        ["KeyOverlay"] = "Show the key status overlay even when playing locally.\nHandy for recording or streaming your play.",
        ["HiddenShowFirstApproach"] = "Sometimes it can be hard to judge when to click the first hitobject when playing with Hidden on.\nThis allows you to see the first hitobject's approach circle.",
        ["ManiaSpeedBPMScale"] = "The scroll speed will depend on the current beatmap's base BPM.\nWith this enabled, faster beatmaps will scroll faster.",
        ["UsePerBeatmapManiaSpeed"] = "Per-beatmap scroll speeds will be stored and used.",
        ["AudioCompatibility"] = "Use legacy audio engine (higher latency but more compatible)",
        ["IgnoreBeatmapSamples"] = "Custom hitsounds bundled with beatmaps will be ignored.",
        ["IgnoreBeatmapSkins"] = "Defaults game settings to never use skin element overrides provided by beatmaps.",
        ["SkinSamples"] = "If this is not selected, the default osu! soundset will be used for hit samples.",
        ["UseTaikoSkin"] = "If this is selected and the Taiko skin is present,\nit will always be used when playing Taiko mode.",
        ["UseSkinCursor"] = "The selected skin's cursor will override any beatmap-specific cursor modifications.",
        ["AutomaticCursorSizing"] = "Cursor size will adjust based on the CS of the current beatmap.",
        ["ComboColourSliderBall"] = "This only takes effect if you are using a skin which supports it.",
        ["RawInput"] = "Raw input will bypass Windows acceleration and provide the most accurate mouse movement.",
        ["AbsoluteToOsuWindow"] = "Input devices with absolute positioning such as tablets usually affect the entire screen area.\nThis allows your tablet screen area to be entirely dedicated to the osu! window.",
        ["MouseDisableWheel"] = "During play, you can use the mouse wheel to adjust the volume and pause the game.\nThis will disable that functionality.",
        ["MouseDisableButtons"] = "This option will disable all mouse buttons.\nSpecifically for people who use their keyboard to click.",
        ["CursorRipple"] = "The cursor will ripple outwards on clicking.",
        ["Tablet"] = "Enable this if you are running a Tablet PC or using a graphics tablet and your clicks aren't handled correctly.",
        ["Wiimote"] = "Enable support for the Wii Taiko drum controller and Wiimote.\nMake sure you have paired your Wiimote via bluetooth before enabling.",
        ["EditorVideo"] = "Show background video in the editor.",
        ["EditorDefaultSkin"] = "Override any custom skin while editing. The default skin is recommended when editing to check overlaps etc.",
        ["EditorSnakingSliders"] = "Sliders gradually snake out from their starting point in the editor.",
        ["EditorHitAnimations"] = "Hitobjects appear hit instead of fading out.",
        ["EditorFollowPoints"] = "Display follow points in the editor.",
        ["EditorStacking"] = "Display hitobjects stacked in the editor.",
        ["Ticker"] = "Popup new chat messages in a one-line display at the bottom of the screen\nwhen the chat console is not visible.",
        ["AutoChatHide"] = "When enabled, chat will automatically hide itself during gameplay.\nIt will be visible during breaks and intro/outro time.",
        ["ChatHighlightName"] = "A message will flash at the bottom of your screen (even during gameplay)\nwhen someone mentions your name in chat.",
        ["ChatMessageNotification"] = "A notification will be shown when new chat messages arrive.",
        ["ChatAudibleHighlight"] = "A sound will play when someone mentions your name in chat.",
        ["DisplayCityLocation"] = "By default, other users will only be able to see your country. Enabling this adds your city to the\npublicly visible location. This is usually quite accurate.",
        ["ShowSpectators"] = "A list of spectators is visible top-left during play.\nIf you hide this, you will no longer know if you are being spectated.",
        ["AutoSendNowPlaying"] = "Send the currently playing beatmap to the #spectator channel when you have spectators.",
        ["PopupDuringGameplay"] = "Choose whether to allow coloured notifications to appear at the bottom of the screen during gameplay.\nIf not ticked, notifications will only be displayed during break time.",
        ["NotifyFriends"] = "Notifications will be shown when friends go online/offline.",
        ["AllowPublicInvites"] = "Uncheck this to limit incoming invites to friends only.",
        ["DiscordRichPresence"] = "Whether to provide rich presence to discord",
        ["YahooIntegration"] = "Your Yahoo! Messenger will show the song you are currently playing or listening to.",
        ["MsnIntegration"] = "Your Windows Live Messenger will show the song you are currently playing or listening to.",
        ["AutomaticDownload"] = "When spectating or multiplaying, beatmaps will be downloaded for you.",
        ["AutomaticDownloadNoVideo"] = "Choose whether automatic osu!direct downloads come with videos.",
        ["ChatFilter"] = "Attempts to remove words that may be offensive to specific cultures and younger age groups.",
        ["ChatRemoveForeign"] = "Removes any characters outside the standard ASCII (English) range.\nUseful if you experience lag from these.",
        ["LogPrivateMessages"] = "Enabling this option will automatically log all private messages sent and received to the Chat folder in \"(user).txt\" format.",
        ["BlockNonFriendPM"] = "You will not receive any private messages from users who are not on your friends list if this option is enabled.",
    };

    static readonly Dictionary<int, string> iconText = []; // drawn every frame (menu music controls): one string each
    internal static void DrawIcon(int codepoint, float cx, float cy, float size, Color colour)
    {
        if (!iconText.TryGetValue(codepoint, out var s)) iconText[codepoint] = s = char.ConvertFromUtf32(codepoint);
        var m = MeasureTextEx(Game.Icons, s, size, 0);
        DrawTextCodepoint(Game.Icons, codepoint, new Vector2(cx - m.X / 2, cy - m.Y / 2), size, colour);
    }
}
