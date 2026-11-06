using System.Globalization;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's Song Setup: a Windows dialog (385x430) over the editor with six tabs - General (the metadata, the romanised
// artist and title only typed when the originals aren't plain ASCII), Difficulty (HP, circle size or mania's key count
// with Co-op, approach rate, overall difficulty: whole steps unless Shift is held), Audio (the default sample set,
// custom overrides, the volume, samples matching the playback rate), Colours (the combo colours, first combo first,
// and the playfield background), Design (countdown, widescreen, letterbox, epilepsy warning, preferred skin) and
// Advanced (stack leniency, the mode, mania's N+1 style). Nothing changes until OK, which saves the map (a change to or
// from osu!mania asks first, then clears the notes); Cancel or Escape leaves it as it was.
sealed partial class Editor
{
    sealed class SetupDialog
    {
        public int Tab, Focus = -1, Open = -1, Dragging = -1, Pick = -1;
        public Vector2 Pos = new(float.NaN), Mouse;
        public Vector2? Drag;
        public string[] Text = new string[8]; // artist, romanised artist, title, romanised title, creator, difficulty, source, tags
        public int HP, CS, AR, OD, Volume, Stack, Mode, OldMode, Speed, CountdownOffset, Set; // (Speed: the countdown's, 1 normal, 2 half, 3 double)
        public bool Countdown, Coop, Overrides, MatchRate, CustomColours, Letterbox, Widescreen, Epilepsy, NPlus1, SetsDiffer, VolumesDiffer, Changed;
        public List<Color> Combos = [];
        public Color Background;
        public string Skin = "";
        public string[] Skins = [];
    }
    SetupDialog? setupDlg;
    const float SsW = 385, SsH = 430;
    static readonly string[] SetupTabs = ["General", "Difficulty", "Audio", "Colours", "Design", "Advanced"];
    static readonly string[] SampleSets = ["Normal", "Soft", "Drum"];
    static readonly string[] AllowedModes = ["All", "osu!taiko", "osu!catch", "osu!mania"];

    static bool Ascii(string s) { foreach (char c in s) if (c > 127) return false; return true; }
    static string Romanise(string s) => new([.. s.Where(c => c <= 127)]);

    void OpenSetup()
    {
        var d = new SetupDialog();
        string K(string section, string key) => Kv(section, key) ?? "";
        string artist = K("Metadata", "Artist"), artistU = K("Metadata", "ArtistUnicode"), title = K("Metadata", "Title"), titleU = K("Metadata", "TitleUnicode");
        d.Text = [artistU.Length > 0 ? artistU : artist, artist.Length > 0 ? artist : Romanise(artistU), titleU.Length > 0 ? titleU : title,
            title.Length > 0 ? title : Romanise(titleU), K("Metadata", "Creator"), K("Metadata", "Version"), K("Metadata", "Source"), K("Metadata", "Tags")];
        d.Mode = d.OldMode = Math.Clamp(mode, 0, 3);
        int Tenths(double v) => (int)Math.Round(Math.Clamp(v, 0, 10) * 10);
        d.HP = Tenths(info.HP);
        d.Coop = mode == 3 && info.CS >= 10;
        d.CS = mode == 3 ? (int)Math.Round(Math.Clamp(info.CS / (d.Coop ? 2 : 1), 0, 10)) : Tenths(info.CS);
        d.AR = Tenths(info.AR < 0 ? info.OD : info.AR);
        d.OD = Tenths(info.OD);
        d.Stack = (int)Math.Max(0, Math.Round((double.TryParse(General("StackLeniency", "0.7"), NumberStyles.Float, Inv, out double sl) ? sl : 0.7) * 10));
        d.Set = Math.Max(0, Array.IndexOf(SampleSets, General("SampleSet", "Normal")));
        d.Overrides = timing.Count > 0 && timing[0].Index == 1;
        d.Volume = timing.Count > 0 ? timing[0].Volume : 100;
        d.SetsDiffer = timing.Any(p => p.Set != timing[0].Set || p.Index != timing[0].Index || p.Index >= 2);
        d.VolumesDiffer = timing.Any(p => p.Volume != timing[0].Volume);
        d.MatchRate = General("SamplesMatchPlaybackRate", "0") == "1";
        // the colour buttons start from the first combo's (stable's Combo2, Combo1 last), at least two
        var combos = ComboColours().Select(c => new Color(c.R, c.G, c.B, 255)).ToList();
        while (combos.Count < 2) combos.Add(new Color(240, 240, 240, 255));
        combos.Add(combos[0]);
        combos.RemoveAt(0);
        d.Combos = combos;
        d.CustomColours = ComboColours().Count > 0;
        d.Background = BackgroundColour() ?? Color.Black;
        int cd = int.TryParse(General("Countdown", "1"), out int c0) ? Math.Clamp(c0, 0, 3) : 1;
        d.Countdown = cd > 0;
        d.Speed = Math.Max(1, cd);
        d.CountdownOffset = int.TryParse(General("CountdownOffset", "0"), out int co) ? Math.Clamp(co, 0, 3) : 0;
        d.Letterbox = General("LetterboxInBreaks", "0") == "1";
        d.Widescreen = General("WidescreenStoryboard", "0") == "1";
        d.Epilepsy = General("EpilepsyWarning", "0") == "1";
        d.NPlus1 = General("SpecialStyle", "0") == "1";
        d.Skin = General("SkinPreference", "");
        var skinDir = Path.Combine(Config.OsuDir, "Skins");
        d.Skins = ["User's preference (No Change)", .. (Directory.Exists(skinDir) ? Directory.GetDirectories(skinDir) : []).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];
        d.Focus = 0;
        setupDlg = d;
    }

    // the playfield background colour: the [Events] line "3,time,r,g,b"
    Color? BackgroundColour()
    {
        foreach (var l in EventsSection.Lines)
        {
            if (!l.StartsWith("3,")) continue;
            var p = l.Split(',');
            if (p.Length >= 5 && int.TryParse(p[2], out int r) && int.TryParse(p[3], out int g) && int.TryParse(p[4], out int b))
                return new Color(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255), 255);
        }
        return null;
    }

    void SetupOk()
    {
        var d = setupDlg!;
        if (!d.Changed) { setupDlg = null; return; }
        if ((d.Mode == 3 || d.OldMode == 3) && d.Mode != d.OldMode)
        {
            prompt = new("You changed play mode, do you want to save changes and clear all notes?", null, _ => ApplySetup(true));
            return;
        }
        ApplySetup(false);
    }

    void ApplySetup(bool clear)
    {
        var d = setupDlg!;
        setupDlg = null;
        string Tenth(int v) => Num(v / 10.0);
        Change(() =>
        {
            // (a key the map doesn't have counts as its default, so one left alone isn't added)
            void Set(string section, string key, string value, string def = "") { if ((Kv(section, key) ?? def) != value) SetKv(section, key, value); }
            if (clear) { objs.Clear(); selected.Clear(); }
            if (d.Mode != mode)
            {
                mode = d.Mode;
                SetKv("General", "Mode", mode.ToString(Inv));
                tool = Math.Min(tool, ToolNames.Length - 1);
                selected.Clear();
            }
            Set("Metadata", "Title", d.Text[3]);
            Set("Metadata", "TitleUnicode", d.Text[2], Kv("Metadata", "Title") ?? "");
            Set("Metadata", "Artist", d.Text[1]);
            Set("Metadata", "ArtistUnicode", d.Text[0], Kv("Metadata", "Artist") ?? "");
            Set("Metadata", "Creator", d.Text[4]);
            Set("Metadata", "Version", d.Text[5]);
            Set("Metadata", "Source", d.Text[6]);
            Set("Metadata", "Tags", d.Text[7]);
            // (values the dialog shows as they were stay as written)
            void Diff(string key, double was, int now, double scale) { if ((int)Math.Round(Math.Clamp(was, 0, 10) * scale) != now) SetKv("Difficulty", key, Num(now / scale)); }
            Diff("HPDrainRate", info.HP, d.HP, 10);
            if (d.Mode == 3) { double keys = Math.Clamp(d.CS * (d.Coop ? 2 : 1), 1, 18); if (keys != info.CS) SetKv("Difficulty", "CircleSize", Num(keys)); }
            else Diff("CircleSize", info.CS, d.CS, 10);
            Diff("ApproachRate", info.AR < 0 ? info.OD : info.AR, d.AR, 10);
            Diff("OverallDifficulty", info.OD, d.OD, 10);
            Set("General", "SampleSet", SampleSets[d.Set], "Normal");
            int set = d.Set + 1;
            foreach (var p in timing)
            {
                bool changed = false;
                if (!d.SetsDiffer && (p.Set != set || p.Index != (d.Overrides ? 1 : 0))) { p.Set = set; p.Index = d.Overrides ? 1 : 0; changed = true; }
                if (!d.VolumesDiffer && p.Volume != d.Volume) { p.Volume = d.Volume; changed = true; }
                if (changed) p.Raw = null;
            }
            Set("General", "SamplesMatchPlaybackRate", d.MatchRate ? "1" : "0", "0");
            if (d.CustomColours)
            {
                var list = d.Combos.ToList();
                list.Insert(0, list[^1]);
                list.RemoveAt(list.Count - 1);
                var now = list.Select(c => ((int)c.R, (int)c.G, (int)c.B)).ToList();
                if (!now.SequenceEqual(ComboColours())) SetComboColours(now);
            }
            else if (ComboColours().Count > 0) SetComboColours([]);
            if (BackgroundColour() is var bg && (bg ?? Color.Black) is var was && (was.R, was.G, was.B) != (d.Background.R, d.Background.G, d.Background.B))
            {
                var ev = EventsSection.Lines;
                string line = $"3,100,{d.Background.R},{d.Background.G},{d.Background.B}";
                int at = ev.FindIndex(l => l.StartsWith("3,"));
                if (at >= 0) ev[at] = line;
                else
                {
                    int comment = ev.FindIndex(l => l.StartsWith("//Background Colour Transformations"));
                    ev.Insert(comment >= 0 ? comment + 1 : ev.Count, line);
                }
            }
            Set("General", "SkinPreference", d.Skin);
            Set("General", "LetterboxInBreaks", d.Letterbox ? "1" : "0", "0");
            Set("General", "WidescreenStoryboard", d.Widescreen ? "1" : "0", "0");
            Set("General", "Countdown", (d.Countdown ? d.Speed : 0).ToString(Inv), "1");
            Set("General", "EpilepsyWarning", d.Epilepsy ? "1" : "0", "0");
            Set("General", "CountdownOffset", d.CountdownOffset.ToString(Inv), "0");
            if (Math.Round((double.TryParse(General("StackLeniency", "0.7"), NumberStyles.Float, Inv, out double sl) ? sl : 0.7) * 10) != d.Stack) SetKv("General", "StackLeniency", Tenth(d.Stack));
            if (d.Mode == 3) Set("General", "SpecialStyle", d.NPlus1 ? "1" : "0", "0");
        });
        info = ReloadInfo();
        Rebuild();
        Save();
    }

    Rectangle SetupRect(float x, float y, float w, float h) { var p = setupDlg!.Pos; float s = AiS; return new Rectangle(p.X + x * s, p.Y + (AiCaption + y) * s, w * s, h * s); }

    // true while the dialog is open (it has all the input)
    bool UpdateSetupDialog(Vector2 mouse)
    {
        var d = setupDlg!;
        float s = AiS;
        d.Mouse = mouse;
        if (float.IsNaN(d.Pos.X)) d.Pos = new((Game.W - SsW * s) / 2, (EH - (SsH + AiCaption) * s) / 2);
        if (prompt != null) return true;
        if (d.Drag is { } dr)
        {
            if (!Input.IsMouseButtonDown(MouseButton.Left)) d.Drag = null;
            else d.Pos = new(Math.Clamp(mouse.X - dr.X, -SsW * s / 2, Game.W - SsW * s / 2), Math.Clamp(mouse.Y - dr.Y, 0, EH - AiCaption * s));
            return true;
        }
        if (Input.IsKeyPressed(KeyboardKey.Escape))
        {
            if (d.Open >= 0 || d.Pick >= 0) { d.Open = d.Pick = -1; return true; }
            setupDlg = null;
            return true;
        }
        // Ctrl+Tab / Ctrl+Shift+Tab: the next / previous page (Windows')
        if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.Tab)) { d.Tab = (d.Tab + (Input.ShiftHeld ? 5 : 1)) % 6; d.Open = -1; return true; }
        // typing into the focused text box
        if (d.Focus >= 0 && d.Tab == 0)
        {
            string before = d.Text[d.Focus];
            for (int c; (c = Input.GetCharPressed()) != 0;) if (c >= 32) d.Text[d.Focus] += char.ConvertFromUtf32(c);
            if ((Input.IsKeyPressed(KeyboardKey.Backspace) || Input.IsKeyPressedRepeat(KeyboardKey.Backspace)) && d.Text[d.Focus].Length > 0) d.Text[d.Focus] = d.Text[d.Focus][..^1];
            if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.V)) d.Text[d.Focus] += (GetClipboardText_() ?? "").Replace("\r", "").Replace("\n", " ");
            if (d.Text[d.Focus] != before) TextChanged(d, d.Focus);
            if (Input.IsKeyPressed(KeyboardKey.Tab))
            {
                int next = d.Focus;
                do next = (next + (Input.ShiftHeld ? 7 : 1)) % 8; while (!BoxOn(d, next));
                d.Focus = next;
            }
        }
        if (Input.IsKeyPressed(KeyboardKey.Enter) && d.Open < 0 && d.Pick < 0) { if (SetupOkOn(d)) SetupOk(); return true; }
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        var cap = new Rectangle(d.Pos.X, d.Pos.Y, SsW * s, AiCaption * s);
        if (click && d.Open < 0 && d.Pick < 0)
        {
            if (CheckCollisionPointRec(mouse, new Rectangle(cap.X + cap.Width - 28 * s, cap.Y, 28 * s, cap.Height))) { setupDlg = null; return true; }
            if (CheckCollisionPointRec(mouse, cap)) { d.Drag = mouse - d.Pos; return true; }
        }
        SetupUi(d, true);
        return true;
    }

    static bool BoxOn(SetupDialog d, int i) => i switch { 1 => !Ascii(d.Text[0]), 3 => !Ascii(d.Text[2]), _ => true };
    static bool SetupOkOn(SetupDialog d) => d.Text[2].Length > 0 && d.Text[0].Length > 0 && d.Text[5].Length > 0 && d.Text[4].Length > 0;

    static void TextChanged(SetupDialog d, int i)
    {
        d.Changed = true;
        if (i is 0 or 2)
        {
            if (Ascii(d.Text[i])) d.Text[i + 1] = d.Text[i];
            d.Text[i + 1] = Romanise(d.Text[i + 1]);
        }
    }

    void DrawSetupDialog()
    {
        var d = setupDlg!;
        float s = AiS, fs = 11 * s;
        if (float.IsNaN(d.Pos.X)) return;
        Color face = new(240, 240, 240, 255), text = Color.Black;
        var whole = new Rectangle(d.Pos.X, d.Pos.Y, SsW * s, (SsH + AiCaption) * s);
        var cap = new Rectangle(d.Pos.X, d.Pos.Y, SsW * s, AiCaption * s);
        DrawRectangle((int)(whole.X + 4 * s), (int)(whole.Y + 4 * s), (int)whole.Width, (int)whole.Height, new Color(0, 0, 0, 70));
        DrawRectangleRec(whole, face);
        DrawRectangleRec(cap, Color.White);
        Game.Text("Song Setup", cap.X + 6 * s, cap.Y + (cap.Height - fs) / 2, fs, text);
        float cx = cap.X + cap.Width - 14 * s, cy = cap.Y + cap.Height / 2;
        DrawLineEx(new(cx - 4 * s, cy - 4 * s), new(cx + 4 * s, cy + 4 * s), 1.2f * s, text);
        DrawLineEx(new(cx - 4 * s, cy + 4 * s), new(cx + 4 * s, cy - 4 * s), 1.2f * s, text);
        DrawRectangleLinesEx(whole, 1, new Color(160, 160, 160, 255));
        SetupUi(d, false);
    }

    // the dialog's controls, drawn (act false) or taking the mouse (act true) from one layout
    void SetupUi(SetupDialog d, bool act)
    {
        float s = AiS, fs = 11 * s;
        var mouse = d.Mouse;
        bool click = act && Input.IsMouseButtonPressed(MouseButton.Left);
        bool down = Input.IsMouseButtonDown(MouseButton.Left);
        Color text = Color.Black, grey = new(109, 109, 109, 255), face = new(240, 240, 240, 255), blue = new(0, 120, 215, 255);
        Rectangle R(float x, float y, float w, float h) => SetupRect(x, y, w, h);
        bool Hover(Rectangle r) => d.Open < 0 && d.Pick < 0 && CheckCollisionPointRec(mouse, r);
        bool Hit(Rectangle r) => click && Hover(r);

        // an open drop-down or the colour picker takes the click first
        if (act && click && d.Open >= 0)
        {
            var (items, box) = DropItems(d);
            for (int i = 0; i < items.Length; i++)
                if (CheckCollisionPointRec(mouse, DropRow(box, i)))
                {
                    if (d.Open == 0) { d.Text[5] = items[i]; d.Changed = true; }
                    if (d.Open == 1) { d.Skin = i == 0 ? "" : items[i]; d.Changed = true; }
                    if (d.Open == 2 && d.Mode != i) { d.Mode = i; ModeChanged(d); d.Changed = true; }
                }
            d.Open = -1;
            return;
        }
        if (act && click && d.Pick >= 0)
        {
            for (int i = 0; i < BasicColours.Length; i++)
                if (CheckCollisionPointRec(mouse, ColourSwatch(i)))
                {
                    uint c = BasicColours[i];
                    var col = new Color((int)(c >> 16 & 0xFF), (int)(c >> 8 & 0xFF), (int)(c & 0xFF), 255);
                    if (d.Pick == 8) d.Background = col; else d.Combos[d.Pick] = col;
                    d.Changed = true;
                }
            d.Pick = -1;
            return;
        }

        void Label(float x, float y, string t, bool bold = false, Color? c = null, float right = 0, bool on = true)
        {
            if (act) return;
            float px = right > 0 ? R(x + right, 0, 0, 0).X - (bold ? Game.BoldWidth(t, fs) : Game.TextWidth(t, fs)) : R(x, 0, 0, 0).X;
            var col = on ? c ?? text : grey;
            if (bold) Game.TextBold(t, px, R(0, y, 0, 0).Y, fs, col); else Game.Text(t, px, R(0, y, 0, 0).Y, fs, col);
        }
        void Wrap(float x, float y, float w, string t, Color? c = null, bool on = true)
        {
            if (act) return;
            float line = 15;
            foreach (var para in t.Split('\n'))
            {
                string cur = "";
                foreach (var word in para.Split(' '))
                {
                    string next = cur.Length == 0 ? word : cur + " " + word;
                    if (cur.Length > 0 && Game.TextWidth(next, fs) > w * s) { Label(x, y, cur, false, c, 0, on); y += line; cur = word; }
                    else cur = next;
                }
                Label(x, y, cur, false, c, 0, on);
                y += line;
            }
        }
        bool Button(float x, float y, float w, float h, string t, bool on = true, bool def = false)
        {
            var r = R(x, y, w, h);
            if (act) return on && Hit(r);
            bool hover = on && Hover(r);
            DrawRectangleRec(r, hover ? new Color(229, 241, 251, 255) : new Color(225, 225, 225, 255));
            DrawRectangleLinesEx(r, def || hover ? 2 * MathF.Max(1, s / 2) : 1, hover || def && on ? blue : new Color(173, 173, 173, 255));
            Game.Text(t, r.X + r.Width / 2 - Game.TextWidth(t, fs) / 2, r.Y + (r.Height - fs) / 2, fs, on ? text : grey);
            return false;
        }
        void Group(float x, float y, float w, float h, string t)
        {
            if (act) return;
            var r = R(x, y + 7, w, h - 7);
            DrawRectangleLinesEx(r, 1, new Color(220, 220, 220, 255));
            float tw = Game.TextWidth(t, fs);
            var gap = R(x + 6, y, 0, 15);
            DrawRectangle((int)gap.X, (int)r.Y - 1, (int)(tw + 4 * s), 3, Color.White);
            Game.Text(t, gap.X + 2 * s, gap.Y, fs, text);
        }
        void Box(int i, float x, float y, float w, float h)
        {
            var r = R(x, y, w, h);
            bool on = BoxOn(d, i);
            if (act) { if (on && Hit(r)) d.Focus = i; return; }
            bool focus = d.Focus == i && d.Tab == 0;
            DrawRectangleRec(r, on ? Color.White : face);
            DrawRectangleLinesEx(r, 1, focus ? blue : on ? new Color(122, 122, 122, 255) : new Color(204, 204, 204, 255));
            string shown = d.Text[i] + (focus && (int)(Game.NowMs / 500) % 2 == 0 ? "|" : "");
            float tw = Game.TextWidth(shown, fs);
            BeginScissorMode((int)r.X + 1, (int)(r.Y + Strip), (int)r.Width - 2, (int)r.Height);
            Game.Text(shown, r.X + 3 * s - (focus ? Math.Max(0, tw - r.Width + 8 * s) : 0), r.Y + (r.Height - fs) / 2, fs, on ? text : grey);
            EndScissorMode();
        }
        void Check(ref bool v, float x, float y, string t, bool on = true)
        {
            var box = R(x, y + 3, 13, 13);
            var whole = R(x, y, 17 + Game.TextWidth(t, fs) / s, 19);
            if (act) { if (on && Hit(whole)) { v = !v; d.Changed = true; } return; }
            DrawRectangleRec(box, Color.White);
            DrawRectangleLinesEx(box, 1, on ? new Color(51, 51, 51, 255) : new Color(204, 204, 204, 255));
            if (v)
            {
                var c = on ? text : grey;
                DrawLineEx(new(box.X + 3 * s, box.Y + 6.5f * s), new(box.X + 5.5f * s, box.Y + 9.5f * s), 1.5f * s, c);
                DrawLineEx(new(box.X + 5.5f * s, box.Y + 9.5f * s), new(box.X + 10 * s, box.Y + 3.5f * s), 1.5f * s, c);
            }
            Game.Text(t, box.X + 17 * s, box.Y + (box.Height - fs) / 2, fs, on ? text : grey);
        }
        void Radio(int value, float x, float y, string t, bool on)
        {
            var whole = R(x, y, 17 + Game.TextWidth(t, fs) / s, 20);
            if (act) { if (on && Hit(whole) && d.Speed != value) { d.Speed = value; d.Changed = true; } return; }
            var c = R(x + 6.5f, y + 10, 0, 0);
            DrawCircleV(new(c.X, c.Y), 6.5f * s, on ? new Color(51, 51, 51, 255) : new Color(204, 204, 204, 255));
            DrawCircleV(new(c.X, c.Y), 5.5f * s, Color.White);
            if (d.Speed == value) DrawCircleV(new(c.X, c.Y), 3.5f * s, on ? text : grey);
            Game.Text(t, c.X + 10.5f * s, c.Y - fs / 2, fs, on ? text : grey);
        }
        void Track(int id, ref int v, int min, int max, float x, float y, float w, int tick, bool snap, bool on = true)
        {
            var r = R(x, y, w, 30);
            float left = r.X + 13 * s, span = r.Width - 26 * s;
            if (act)
            {
                if (on && Hit(r)) d.Dragging = id;
                if (d.Dragging == id)
                {
                    if (!down) { d.Dragging = -1; return; }
                    int to = min + (int)Math.Round(Math.Clamp((mouse.X - left) / span, 0, 1) * (max - min));
                    if (snap && !Input.ShiftHeld) to = Math.Clamp((int)Math.Round(to / 10.0) * 10, min, max);
                    if (to != v) { v = to; d.Changed = true; }
                }
                return;
            }
            var channel = new Rectangle(r.X + 8 * s, r.Y + 8 * s, r.Width - 16 * s, 4 * s);
            DrawRectangleRec(channel, new Color(231, 234, 234, 255));
            DrawRectangleLinesEx(channel, 1, new Color(214, 214, 214, 255));
            for (int t = min; t <= max; t += Math.Max(1, tick))
            {
                float tx = left + (max > min ? (t - min) / (float)(max - min) : 0) * span;
                DrawRectangle((int)tx, (int)(r.Y + 24 * s), 1, (int)((t == min || t == max ? 4 : 3) * s), new Color(196, 196, 196, 255));
            }
            float cx = left + (max > min ? (Math.Clamp(v, min, max) - min) / (float)(max - min) : 0) * span;
            var thumb = on ? d.Dragging == id ? new Color(204, 204, 204, 255) : Hover(r) ? new Color(23, 23, 23, 255) : blue : new Color(204, 204, 204, 255);
            DrawRectangleRec(new Rectangle(cx - 5.5f * s, r.Y + 2 * s, 11 * s, 15 * s), thumb);
            DrawTriangle(new(cx - 5.5f * s, r.Y + 17 * s), new(cx, r.Y + 22 * s), new(cx + 5.5f * s, r.Y + 17 * s), thumb);
            if (d.Dragging == id || Hover(r)) Tip(Num(id == 4 || id == 1 && tick == 1 ? v : v / 10.0)); // (stable's tooltip: the value)
        }
        void Link(float x, float y, string t)
        {
            if (act) return;
            var p = R(x, y, 0, 0);
            Game.Text(t, p.X, p.Y, fs, new Color(0, 102, 204, 255));
            DrawRectangle((int)p.X, (int)(p.Y + fs + 1 * s), (int)Game.TextWidth(t, fs), 1, new Color(0, 102, 204, 255));
        }
        void Drop(int id, string shown, float x, float y, float w, float h, bool editable, int box = -1, bool on = true)
        {
            var r = R(x, y, w, h);
            var arrow = R(x + w - 17, y, 17, h);
            if (act)
            {
                if (!on) return;
                if (Hit(editable ? arrow : r)) d.Open = id;
                else if (editable && Hit(r)) d.Focus = box;
                return;
            }
            bool hover = on && Hover(r);
            DrawRectangleRec(r, editable ? on ? Color.White : face : hover ? new Color(229, 241, 251, 255) : new Color(225, 225, 225, 255));
            DrawRectangleLinesEx(r, 1, hover || editable && d.Focus == box ? blue : new Color(122, 122, 122, 255));
            if (editable && box >= 0) { Box(box, x, y, w - 17, h); DrawRectangleLinesEx(r, 1, d.Focus == box ? blue : new Color(122, 122, 122, 255)); }
            else Game.Text(shown, r.X + 3 * s, r.Y + (r.Height - fs) / 2, fs, on ? text : grey);
            float ax = arrow.X + arrow.Width / 2, ay = arrow.Y + arrow.Height / 2;
            DrawTriangle(new(ax - 3.5f * s, ay - 1.5f * s), new(ax, ay + 2 * s), new(ax + 3.5f * s, ay - 1.5f * s), on ? text : grey);
        }
        void Colour(int i, float x, float y, float w, float h, string t, Color c)
        {
            var r = R(x, y, w, h);
            if (act) { if (Hit(r)) d.Pick = i; return; }
            DrawRectangleRec(r, c);
            DrawRectangleLinesEx(r, 1, Hover(r) ? blue : new Color(100, 100, 100, 255));
            float big = 16 * s;
            Game.TextBold(t, r.X + r.Width / 2 - Game.BoldWidth(t, big) / 2, r.Y + (r.Height - big) / 2, big, Color.White);
        }

        // the tabs (the selected one a little bigger, its page white)
        float tx = 2;
        for (int i = 0; i < SetupTabs.Length; i++)
        {
            float tw = Game.TextWidth(SetupTabs[i], fs) / s + 12;
            var r = R(tx, 2, tw, 20);
            if (act) { if (Hit(r)) { d.Tab = i; d.Open = -1; } }
            else if (d.Tab != i)
            {
                DrawRectangleRec(r, Hover(r) ? new Color(229, 241, 251, 255) : face);
                DrawRectangleLinesEx(r, 1, new Color(217, 217, 217, 255));
                Game.Text(SetupTabs[i], r.X + 6 * s, r.Y + (r.Height - fs) / 2, fs, text);
            }
            tx += tw;
        }
        if (!act)
        {
            var page = R(0, 22, 385, 364);
            DrawRectangleRec(page, Color.White);
            DrawRectangleLinesEx(page, 1, new Color(217, 217, 217, 255));
            tx = 2;
            for (int i = 0; i < d.Tab; i++) tx += Game.TextWidth(SetupTabs[i], fs) / s + 12;
            float sw = Game.TextWidth(SetupTabs[d.Tab], fs) / s + 12;
            var sel = R(tx - 2, 0, sw + 4, 23);
            DrawRectangleRec(sel, Color.White);
            DrawRectangleLinesEx(sel with { Height = sel.Height - 1 * s }, 1, new Color(217, 217, 217, 255));
            DrawRectangleRec(new Rectangle(sel.X + 1, sel.Y + sel.Height - 2 * s, sel.Width - 2, 3 * s), Color.White);
            Game.Text(SetupTabs[d.Tab], sel.X + 8 * s, sel.Y + (sel.Height - fs) / 2, fs, text);
        }

        float ox = 4, oy = 22; // (the page's corner)
        switch (d.Tab)
        {
            case 0:
            {
                float gx = ox + 6, gy = oy + 6;
                Group(gx, gy, 365, 223, "Song and Map Metadata");
                Label(gx + 32, gy + 22, "Artist", true, null, 105);
                Box(0, gx + 141, gy + 20, 218, 20);
                Label(gx + 6, gy + 47, "Romanised Artist", true, null, 131, BoxOn(d, 1));
                Box(1, gx + 141, gy + 45, 218, 20);
                Label(gx + 32, gy + 72, "Title", true, null, 105);
                Box(2, gx + 141, gy + 70, 218, 20);
                Label(gx + 6, gy + 97, "Romanised Title", true, null, 131, BoxOn(d, 3));
                Box(3, gx + 141, gy + 95, 218, 20);
                Label(gx + 5, gy + 122, "Beatmap Creator", true, null, 132);
                Box(4, gx + 141, gy + 120, 218, 20);
                Label(gx + 32, gy + 147, "Difficulty", true, null, 105);
                Drop(0, d.Text[5], gx + 141, gy + 145, 218, 21, true, 5);
                Label(gx + 32, gy + 172, "Source", true, null, 105);
                Box(6, gx + 141, gy + 170, 218, 20);
                Label(gx + 32, gy + 197, "Tags", true, null, 105);
                Box(7, gx + 141, gy + 195, 218, 20);
                gy = oy + 234;
                Group(gx, gy, 365, 118, "A quick note");
                Wrap(gx + 6, gy + 18, 353, "Due to the large number of beatmap submissions, the standard of approval is relatively high.  Please ensure your beatmap is at least timed properly, or it will likely be ignored!", new Color(255, 0, 0, 255));
                Link(gx + 31, gy + 91, "Editor Guide/FAQ");
                Link(gx + 182, gy + 91, "Official Submission Criteria");
                break;
            }
            case 1:
            {
                float gx = ox + 4, gy = oy + 6;
                bool mania = d.Mode == 3;
                Group(gx, gy, 365, 351, "Difficulty");
                Label(gx + 10, gy + 28, "HP Drain Rate", true);
                Track(0, ref d.HP, 0, 100, gx + 118, gy + 19, 241, 10, true);
                Label(gx + 122, gy + 47, "Easy");
                Label(gx + 314, gy + 47, "Insane", false, null, 41);
                Wrap(gx + 9, gy + 69, 350, "The constant rate of health-bar drain throughout the song");
                Label(gx + 10, gy + 102, mania ? "Key Count" : "Circle Size", true);
                if (mania) Track(1, ref d.CS, 1, 9, gx + 118, gy + 93, 241, 1, false);
                else Track(1, ref d.CS, Math.Min(20, d.CS), Math.Max(70, d.CS), gx + 118, gy + 93, 241, 10, true);
                if (mania) { bool coopOn = d.CS >= 5; if (!coopOn) d.Coop = false; Check(ref d.Coop, gx + 12, gy + 124, "Co-op mode", coopOn); }
                Label(gx + 122, gy + 124, mania ? "1" : "Large");
                if (!mania) Label(gx + 216, gy + 124, "Normal");
                Label(gx + 319, gy + 124, mania ? "9" : "Small", false, null, 36);
                Wrap(gx + 9, gy + 145, 350, mania ? "The column count" : "The radial size of hit circles and sliders");
                Label(gx + 10, gy + 176, "Approach Rate", true, null, 0, !mania);
                Track(2, ref d.AR, 0, 100, gx + 118, gy + 168, 241, 10, true, !mania);
                Label(gx + 122, gy + 196, "Slow", false, null, 0, !mania);
                Label(gx + 327, gy + 196, "Fast", false, null, 28, !mania);
                Wrap(gx + 9, gy + 218, 350, "The speed at which objects appear");
                Label(gx + 10, gy + 250, "Overall Difficulty", true);
                Track(3, ref d.OD, 0, 100, gx + 118, gy + 241, 241, 10, true);
                Label(gx + 122, gy + 270, "Easy");
                Label(gx + 314, gy + 270, "Insane", false, null, 41);
                Wrap(gx + 9, gy + 293, 350, "The harshness of the hit window and the difficulty of spinners");
                Label(gx + 66, gy + 331, "Hold shift for precise difficulty adjustment.");
                break;
            }
            case 2:
            {
                float gx = ox + 6, gy = oy + 6;
                Group(gx, gy, 365, 270, "Default Sample Settings");
                if (d.SetsDiffer)
                {
                    Wrap(gx + 13, gy + 26, 336, "This beatmap has timing-section dependent volume settings.  \nYou can therefore not set beatmap-wide settings here.");
                    if (Button(gx + 234, gy + 62, 123, 23, "Reset Settings")) { d.SetsDiffer = false; d.Changed = true; }
                }
                else
                {
                    var list = R(gx + 11, gy + 22, 133, 73);
                    if (act) { for (int i = 0; i < 3; i++) if (Hit(R(gx + 12, gy + 23 + i * 15, 131, 15)) && d.Set != i) { d.Set = i; d.Changed = true; } }
                    else
                    {
                        DrawRectangleRec(list, Color.White);
                        DrawRectangleLinesEx(list, 1, new Color(130, 135, 144, 255));
                        for (int i = 0; i < 3; i++)
                        {
                            var row = R(gx + 12, gy + 23 + i * 15, 131, 15);
                            if (d.Set == i) DrawRectangleRec(row, blue);
                            Game.Text(SampleSets[i], row.X + 2 * s, row.Y + (row.Height - fs) / 2, fs, d.Set == i ? Color.White : text);
                        }
                    }
                    Check(ref d.Overrides, gx + 153, gy + 22, "Enable custom overrides");
                    Link(gx + 150, gy + 45, "How to add custom sample overrides");
                }
                if (d.VolumesDiffer)
                {
                    Wrap(gx + 12, gy + 105, 345, "This beatmap has timing-section dependent volume settings.  \nYou can therefore not set beatmap-wide settings here.");
                    if (Button(gx + 234, gy + 141, 123, 23, "Reset Settings")) { d.VolumesDiffer = false; d.Changed = true; }
                }
                else
                {
                    Label(gx + 8, gy + 101, "Sample Volume");
                    Track(4, ref d.Volume, 0, 100, gx + 6, gy + 119, 353, 10, false);
                }
                string[] sounds = ["Normal", "Whistle", "Finish", "Clap"];
                float[] at = [5, 98, 190, 280];
                for (int i = 0; i < 4; i++)
                    if (Button(gx + at[i], gy + 170, i == 3 ? 79 : 75, 23, sounds[i]))
                        samples.Play(d.Set + 1, "hit" + sounds[i].ToLowerInvariant(), d.Overrides ? 1 : 0, Math.Max(8, d.Volume));
                Wrap(gx + 6, gy + 207, 353, "Sample settings (sets and volume) can also be assigned independently for each timing section via the timing setup panel (F6 in the editor).");
                gy = oy + 282;
                Group(gx, gy, 365, 44, "Misc. Toggles");
                Check(ref d.MatchRate, gx + 8, gy + 21, "Samples match playback rate (for fully-hitsounded maps)");
                break;
            }
            case 3:
            {
                float gx = ox + 3, gy = oy + 3;
                Group(gx, gy, 365, 202, "Hitcircle/Slider Combos");
                for (int i = 0; i < d.Combos.Count; i++) Colour(i, gx + (i % 2 == 0 ? 7 : 185), gy + 19 + i / 2 * 34, 172, 32, $"Combo {i + 1}", d.Combos[i]);
                if (d.Combos.Count < 8 && Button(gx + (d.Combos.Count % 2 == 0 ? 7 : 185), gy + 19 + d.Combos.Count / 2 * 34, 172, 32, "New Combo Colour"))
                {
                    d.Combos.Add(d.Combos[^1]);
                    d.Pick = d.Combos.Count - 1;
                    d.Changed = true;
                }
                if (Button(gx + 185, gy + 164, 172, 32, "Remove Combo Colour", d.Combos.Count > 2)) { d.Combos.RemoveAt(d.Combos.Count - 1); d.Changed = true; }
                Check(ref d.CustomColours, gx + 12, gy + 171, "Enable Custom Colours");
                gy = oy + 208;
                Group(gx, gy, 365, 115, "Playfield Background");
                Colour(8, gx + 7, gy + 19, 352, 32, "Background Colour", d.Background);
                Wrap(gx + 6, gy + 57, 353, "Please note that this colour is dimmed in both editor compose mode and in gameplay during playtime.  It will be the exact shade of the chosen colour during breaks.");
                break;
            }
            case 4:
            {
                float gx = ox + 6, gy = oy + 4;
                Group(gx, gy, 365, 179, "Countdown");
                Check(ref d.Countdown, gx + 10, gy + 19, "Enable countdown");
                bool cdOn = d.Countdown;
                Link(gx + 201, gy + 19, "Customising the countdown");
                Wrap(gx + 3, gy + 40, 356, "If this is checked, an \"Are you ready? 3, 2, 1, GO!\" countdown will be inserted at the beginning of the beatmap, assuming there is enough time to do so.");
                float px = gx + 3, py = gy + 90;
                Label(px + 3, py + 3, "Countdown Speed:", true, null, 0, cdOn);
                Radio(2, px + 137, py + 1, "Half", cdOn);
                Radio(1, px + 206, py + 1, "Normal", cdOn);
                Radio(3, px + 275, py + 1, "Double", cdOn);
                Label(px + 3, py + 29, "Countdown Offset:", true, null, 0, cdOn);
                var num = R(px + 137, py + 27, 49, 20);
                if (act) { if (cdOn && Hit(num)) { int step = mouse.Y < num.Y + num.Height / 2 ? 1 : -1; if (mouse.X > num.X + num.Width - 16 * s) { d.CountdownOffset = Math.Clamp(d.CountdownOffset + step, 0, 3); d.Changed = true; } } }
                else
                {
                    DrawRectangleRec(num, cdOn ? Color.White : face);
                    DrawRectangleLinesEx(num, 1, new Color(122, 122, 122, 255));
                    Game.Text(d.CountdownOffset.ToString(Inv), num.X + 3 * s, num.Y + (num.Height - fs) / 2, fs, cdOn ? text : grey);
                    float ax = num.X + num.Width - 8 * s;
                    DrawTriangle(new(ax - 3 * s, num.Y + 8 * s), new(ax + 3 * s, num.Y + 8 * s), new(ax, num.Y + 4 * s), cdOn ? text : grey);
                    DrawTriangle(new(ax - 3 * s, num.Y + 12 * s), new(ax, num.Y + 16 * s), new(ax + 3 * s, num.Y + 12 * s), cdOn ? text : grey);
                }
                Wrap(px + 3, py + 50, 340, "If the countdown sounds off-time, use this to make it appear\none or more beats early.", null, cdOn);
                gy = oy + 189;
                Group(gx, gy, 365, 116, "Misc. Toggles");
                Check(ref d.Widescreen, gx + 10, gy + 22, "Widescreen Support");
                Check(ref d.Letterbox, gx + 10, gy + 44, "Letterbox during breaks");
                Check(ref d.Epilepsy, gx + 10, gy + 66, "Display epilepsy warning (storyboard has quick strobing)");
                gy = oy + 311;
                Group(gx, gy, 365, 41, "Skinning");
                Label(gx + 6, gy + 19, "Preferred Skin:");
                Drop(1, d.Skin.Length > 0 ? d.Skin : d.Skins[0], gx + 95, gy + 16, 263, 21, false);
                break;
            }
            case 5:
            {
                float gx = ox + 6, gy = oy + 4;
                Group(gx, gy, 365, 131, "Stacking");
                Label(gx + 6, gy + 24, "Stack Leniency:", true);
                Track(5, ref d.Stack, 0, 10, gx + 118, gy + 21, 241, 1, false);
                Label(gx + 122, gy + 51, "Rarely Stack");
                Label(gx + 279, gy + 51, "Always Stack", false, null, 75);
                Wrap(gx + 7, gy + 69, 351, "In play mode, osu! automatically stacks notes which occur at the same location.  Increasing this value means it is more likely to snap notes of further time-distance.");
                gy = oy + 141;
                Group(gx, gy, 365, 158, "Mode Specific");
                Label(gx + 6, gy + 22, "Allowed Modes:", true);
                Drop(2, AllowedModes[d.Mode], gx + 118, gy + 19, 236, 21, false);
                Check(ref d.NPlus1, gx + 8, gy + 45, "Use special style(N+1 style) for mania", d.Mode == 3);
                break;
            }
        }

        if (Button(4, 392, 232, 34, "OK", SetupOkOn(d), true)) { SetupOk(); return; }
        if (Button(242, 392, 139, 34, "Cancel")) { setupDlg = null; return; }

        // on top: an open drop-down's list, the colour picker
        if (!act && d.Open >= 0)
        {
            var (items, box) = DropItems(d);
            var all = new Rectangle(box.X, box.Y + box.Height, box.Width, items.Length * 17 * s + 2);
            DrawRectangleRec(all, Color.White);
            DrawRectangleLinesEx(all, 1, new Color(100, 100, 100, 255));
            for (int i = 0; i < items.Length; i++)
            {
                var row = DropRow(box, i);
                bool hover = CheckCollisionPointRec(mouse, row);
                if (hover) DrawRectangleRec(row, blue);
                Game.Text(items[i], row.X + 3 * s, row.Y + (row.Height - fs) / 2, fs, hover ? Color.White : text);
            }
        }
        if (!act && d.Pick >= 0)
        {
            float u = U;
            var first = ColourSwatch(0);
            var pick = new Rectangle(first.X - 10 * u, first.Y - 26 * u, 8 * 25 * u + 17 * u, 6 * 21 * u + 34 * u);
            DrawRectangleRec(pick, face);
            DrawRectangleLinesEx(pick, 1, new Color(160, 160, 160, 255));
            Game.Text("Basic colours:", pick.X + 8 * u, pick.Y + 6 * u, 10 * u, text);
            for (int i = 0; i < BasicColours.Length; i++)
            {
                var r = ColourSwatch(i);
                uint c = BasicColours[i];
                DrawRectangleRec(r, new Color((int)(c >> 16 & 0xFF), (int)(c >> 8 & 0xFF), (int)(c & 0xFF), 255));
                DrawRectangleLinesEx(r, 1, CheckCollisionPointRec(mouse, r) ? Color.Black : new Color(130, 130, 130, 255));
            }
        }
    }

    (string[] Items, Rectangle Box) DropItems(SetupDialog d) => d.Open switch
    {
        0 => (["Easy", "Normal", "Hard", "Insane"], SetupRect(4 + 6 + 141, 22 + 6 + 145, 218, 21)),
        1 => (d.Skins, SetupRect(4 + 6 + 95, 22 + 311 + 16, 263, 21)),
        _ => (AllowedModes, SetupRect(4 + 6 + 118, 22 + 141 + 19, 236, 21)),
    };
    static Rectangle DropRow(Rectangle box, int i) { float s = AiS; return new Rectangle(box.X + 1, box.Y + box.Height + 1 + i * 17 * s, box.Width - 2, 17 * s); }

    // stable re-ranges circle size for the mode: mania's key count 1-9 (from the map's), the others 2-7
    void ModeChanged(SetupDialog d)
    {
        if (d.Mode == 3) d.CS = (int)Math.Round(Math.Clamp(info.CS / (d.Coop ? 2 : 1), 1, 9));
        else if (d.OldMode == 3 && d.CS < 20) d.CS = (int)Math.Round(Math.Clamp(info.CS, 0, 10) * 10);
    }
}
