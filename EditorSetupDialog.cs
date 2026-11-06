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
    sealed class SetupDialog : WinUi
    {
        public int Tab, Pick = -1;
        public bool NewDifficulty; // (File > Create a new Difficulty...: the name typed here, artist and title kept)
        public string[] Text = new string[8]; // artist, romanised artist, title, romanised title, creator, difficulty, source, tags
        public int HP, CS, AR, OD, Volume, Stack, Mode, OldMode, Speed, CountdownOffset, Set; // (Speed: the countdown's, 1 normal, 2 half, 3 double)
        public bool Countdown, Coop, Overrides, MatchRate, CustomColours, Letterbox, Widescreen, Epilepsy, NPlus1, SetsDiffer, VolumesDiffer;
        public List<Color> Combos = [], OpenCombos = [];
        public int OpenSet, OpenVolume;
        public bool OpenOverrides, OpenCustom;
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

    void OpenSetup() => OpenSetup(false);
    void OpenSetup(bool newDifficulty)
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
        d.Volume = timing.Count > 0 ? Math.Max(5, timing[0].Volume) : 100;
        d.SetsDiffer = timing.Any(p => p.Set != timing[0].Set || p.Index != timing[0].Index || p.Index >= 2);
        if (!d.SetsDiffer && timing.Count > 0 && timing[0].Set is >= 1 and <= 3) d.Set = timing[0].Set - 1; // (the set the points have)
        (d.OpenSet, d.OpenOverrides, d.OpenVolume) = (d.Set, d.Overrides, d.Volume);
        d.VolumesDiffer = timing.Any(p => p.Volume != timing[0].Volume);
        d.MatchRate = General("SamplesMatchPlaybackRate", "0") == "1";
        // the colour buttons start from the first combo's (stable's Combo2, Combo1 last), at least two
        var combos = Colours.Take(8).ToList();
        while (combos.Count < 2) combos.Add(new Color(240, 240, 240, 255));
        combos.Add(combos[0]);
        combos.RemoveAt(0);
        d.Combos = combos;
        d.OpenCombos = [.. combos];
        d.CustomColours = d.OpenCustom = ComboColours().Count > 0;
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
        if (newDifficulty)
        {
            d.NewDifficulty = true;
            d.Text[5] = "";
            d.Focus = 5;
            d.Widescreen = true;
        }
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
        if (d.NewDifficulty && File.Exists(NewDifficultyPath(d))) { Notification.Show("A difficulty with that name already exists.", 3000); return; }
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
            Set("Metadata", "Version", d.NewDifficulty ? d.Text[5].Trim() : d.Text[5]);
            Set("Metadata", "Source", d.Text[6]);
            Set("Metadata", "Tags", d.Text[7]);
            // (values the dialog shows as they were stay as written)
            void Diff(string key, double was, int now, double scale) { if ((int)Math.Round(Math.Clamp(was, 0, 10) * scale) != now) SetKv("Difficulty", key, Num(now / scale)); }
            Diff("HPDrainRate", info.HP, d.HP, 10);
            if (d.Mode == 3) { double keys = Math.Clamp(d.CS * (d.Coop ? 2 : 1), 1, 18); if (keys != info.CS) SetKv("Difficulty", "CircleSize", Num(keys)); }
            else Diff("CircleSize", info.CS, d.CS, 10);
            Diff("ApproachRate", info.AR < 0 ? info.OD : info.AR, d.AR, 10);
            Diff("OverallDifficulty", info.OD, d.OD, 10);
            if (d.Set != d.OpenSet) SetKv("General", "SampleSet", SampleSets[d.Set]);
            // (the timing points only when their set or volume was changed here)
            int set = d.Set + 1;
            bool sets = !d.SetsDiffer && (d.Set != d.OpenSet || d.Overrides != d.OpenOverrides), volumes = !d.VolumesDiffer && d.Volume != d.OpenVolume;
            foreach (var p in timing)
            {
                bool changed = false;
                if (sets && (p.Set != set || p.Index != (d.Overrides ? 1 : 0))) { p.Set = set; p.Index = d.Overrides ? 1 : 0; changed = true; }
                if (volumes && p.Volume != d.Volume) { p.Volume = d.Volume; changed = true; }
                if (changed) p.Raw = null;
            }
            Set("General", "SamplesMatchPlaybackRate", d.MatchRate ? "1" : "0", "0");
            if (d.CustomColours && (!d.OpenCustom || !d.Combos.SequenceEqual(d.OpenCombos)))
            {
                var list = d.Combos.ToList();
                list.Insert(0, list[^1]);
                list.RemoveAt(list.Count - 1);
                var now = list.Select(c => ((int)c.R, (int)c.G, (int)c.B)).ToList();
                if (!now.SequenceEqual(ComboColours())) SetComboColours(now);
            }
            else if (!d.CustomColours && d.OpenCustom) SetComboColours([]);
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
        if (!d.NewDifficulty) { Save(); return; }
        string old = path;
        path = NewDifficultyPath(d);
        if (Kv("Metadata", "BeatmapID") != null) SetKv("Metadata", "BeatmapID", "0");
        if (!TrySave()) { path = old; Restore(undo, redo); redo.Clear(); return; } // (not written: still this difficulty, as it was)
        undo.Clear(); redo.Clear(); // (a new file's own history)
        if (objs.Count > 0) prompt = new("Would you like to clear all objects and start fresh?", null, _ => { objs.Clear(); selected.Clear(); Rebuild(); if (KeepBreaks()) info = ReloadInfo(); Save(); }, "Yes", "No");
    }

    // a difficulty's file: Artist - Title (Creator) [Difficulty].osu in the map's folder
    string NewDifficultyPath(SetupDialog d)
    {
        string file = $"{d.Text[1]} - {d.Text[3]} ({d.Text[4]}) [{d.Text[5].Trim()}].osu";
        foreach (char c in Path.GetInvalidFileNameChars()) file = file.Replace(c.ToString(), "");
        return Path.Combine(info.Folder, file);
    }

    Rectangle SetupRect(float x, float y, float w, float h) => setupDlg!.R(x, y, w, h);

    // true while the dialog is open (it has all the input)
    bool UpdateSetupDialog(Vector2 mouse)
    {
        var d = setupDlg!;
        if (d.Pick < 0 && d.UpdateFrame(mouse, SsW, SsH)) { setupDlg = null; return true; }
        if (d.Drag != null) return true;
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
            if (WinUi.Type(ref d.Text[d.Focus])) TextChanged(d, d.Focus);
            if (Input.IsKeyPressed(KeyboardKey.Tab))
            {
                int next = d.Focus;
                do next = (next + (Input.ShiftHeld ? 7 : 1)) % 8; while (!BoxOn(d, next));
                d.Focus = next;
            }
        }
        if (Input.IsKeyPressed(KeyboardKey.Enter) && d.Open < 0 && d.Pick < 0) { if (SetupOkOn(d)) SetupOk(); return true; }
        SetupUi(d, true);
        return true;
    }

    static bool BoxOn(SetupDialog d, int i) => i switch { 0 or 2 => !d.NewDifficulty, 1 => !Ascii(d.Text[0]), 3 => !Ascii(d.Text[2]), _ => true };
    static bool SetupOkOn(SetupDialog d) => d.Text[2].Length > 0 && d.Text[0].Length > 0 && d.Text[5].Trim().Length > 0 && d.Text[4].Length > 0 && d.Text[1].Length > 0 && d.Text[3].Length > 0;

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
        if (float.IsNaN(d.Pos.X)) return;
        d.DrawFrame("Song Setup", SsW, SsH);
        SetupUi(d, false);
    }

    // the dialog's controls, drawn (act false) or taking the mouse (act true) from one layout
    void SetupUi(SetupDialog d, bool act)
    {
        d.Begin(act, d.Open >= 0 || d.Pick >= 0);
        float s = AiS, fs = WinUi.Fs;
        var mouse = d.Mouse;
        bool click = d.Clicked;
        Color text = WinUi.Text, grey = WinUi.Grey, face = WinUi.Face, blue = WinUi.Blue;
        Rectangle R(float x, float y, float w, float h) => d.R(x, y, w, h);
        bool Hover(Rectangle r) => d.Hover(r);
        bool Hit(Rectangle r) => d.Hit(r);

        // an open drop-down or the colour picker takes the click first
        if (act && click && d.Open >= 0)
        {
            var (items, box) = DropItems(d);
            for (int i = 0; i < items.Length; i++)
                if (CheckCollisionPointRec(mouse, WinUi.ListRow(box, i)))
                {
                    if (d.Open == 0) { d.Text[5] = items[i]; d.Changed = true; }
                    if (d.Open == 1) { d.Skin = i == 0 ? "" : items[i]; d.Changed = true; }
                    if (d.Open == 2 && d.Mode != i) { int was = d.Mode; d.Mode = i; ModeChanged(d, was); d.Changed = true; }
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
                    if (d.Pick == 8) d.Background = col; else if (d.Pick == d.Combos.Count) d.Combos.Add(col); else d.Combos[d.Pick] = col;
                    d.Changed = true;
                }
            d.Pick = -1;
            return;
        }

        void Box(int i, float x, float y, float w, float h) => d.Box(i, d.Text[i], x, y, w, h, BoxOn(d, i));
        // (Difficulty's: a text box with the list's arrow)
        void EditDrop(int id, int box, float x, float y, float w, float h)
        {
            var arrow = R(x + w - 17, y, 17, h);
            if (act) { if (Hit(arrow)) d.Open = id; else Box(box, x, y, w - 17, h); return; }
            var r = R(x, y, w, h);
            DrawRectangleRec(r, Color.White);
            Box(box, x, y, w - 17, h);
            DrawRectangleLinesEx(r, 1, d.Focus == box ? blue : new Color(122, 122, 122, 255));
            WinUi.Arrow(arrow, true);
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

        d.Tabs(ref d.Tab, SetupTabs, 0, 0, 385, 364);
        float ox = 4, oy = 22; // (the page's corner)
        switch (d.Tab)
        {
            case 0:
            {
                float gx = ox + 6, gy = oy + 6;
                d.Group(gx, gy, 365, 223, "Song and Map Metadata");
                d.Label(gx + 32, gy + 22, "Artist", true, null, 105);
                Box(0, gx + 141, gy + 20, 218, 20);
                d.Label(gx + 6, gy + 47, "Romanised Artist", true, null, 131, BoxOn(d, 1));
                Box(1, gx + 141, gy + 45, 218, 20);
                d.Label(gx + 32, gy + 72, "Title", true, null, 105);
                Box(2, gx + 141, gy + 70, 218, 20);
                d.Label(gx + 6, gy + 97, "Romanised Title", true, null, 131, BoxOn(d, 3));
                Box(3, gx + 141, gy + 95, 218, 20);
                d.Label(gx + 5, gy + 122, "Beatmap Creator", true, null, 132);
                Box(4, gx + 141, gy + 120, 218, 20);
                d.Label(gx + 32, gy + 147, "Difficulty", true, null, 105);
                EditDrop(0, 5, gx + 141, gy + 145, 218, 21);
                d.Label(gx + 32, gy + 172, "Source", true, null, 105);
                Box(6, gx + 141, gy + 170, 218, 20);
                d.Label(gx + 32, gy + 197, "Tags", true, null, 105);
                Box(7, gx + 141, gy + 195, 218, 20);
                gy = oy + 234;
                d.Group(gx, gy, 365, 118, "A quick note");
                d.Wrap(gx + 6, gy + 18, 353, "Due to the large number of beatmap submissions, the standard of approval is relatively high.  Please ensure your beatmap is at least timed properly, or it will likely be ignored!", new Color(255, 0, 0, 255));
                d.Link(gx + 31, gy + 91, "Editor Guide/FAQ");
                d.Link(gx + 182, gy + 91, "Official Submission Criteria");
                break;
            }
            case 1:
            {
                float gx = ox + 4, gy = oy + 6;
                bool mania = d.Mode == 3;
                d.Group(gx, gy, 365, 351, "Difficulty");
                d.Label(gx + 10, gy + 28, "HP Drain Rate", true);
                d.Track(0, ref d.HP, 0, 100, gx + 118, gy + 19, 241, 10, true);
                d.Label(gx + 122, gy + 47, "Easy");
                d.Label(gx + 314, gy + 47, "Insane", false, null, 41);
                d.Wrap(gx + 9, gy + 69, 350, "The constant rate of health-bar drain throughout the song");
                d.Label(gx + 10, gy + 102, mania ? "Key Count" : "Circle Size", true);
                if (mania) d.Track(1, ref d.CS, 1, 9, gx + 118, gy + 93, 241, 1, false, true, 1);
                else d.Track(1, ref d.CS, Math.Min(20, d.CS), Math.Max(70, d.CS), gx + 118, gy + 93, 241, 10, true);
                if (mania) { bool coopOn = d.CS >= 5; if (!coopOn) d.Coop = false; d.Check(ref d.Coop, gx + 12, gy + 124, "Co-op mode", coopOn); }
                d.Label(gx + 122, gy + 124, mania ? "1" : "Large");
                if (!mania) d.Label(gx + 216, gy + 124, "Normal");
                d.Label(gx + 319, gy + 124, mania ? "9" : "Small", false, null, 36);
                d.Wrap(gx + 9, gy + 145, 350, mania ? "The column count" : "The radial size of hit circles and sliders");
                d.Label(gx + 10, gy + 176, "Approach Rate", true, null, 0, !mania);
                d.Track(2, ref d.AR, 0, 100, gx + 118, gy + 168, 241, 10, true, !mania);
                d.Label(gx + 122, gy + 196, "Slow", false, null, 0, !mania);
                d.Label(gx + 327, gy + 196, "Fast", false, null, 28, !mania);
                d.Wrap(gx + 9, gy + 218, 350, "The speed at which objects appear");
                d.Label(gx + 10, gy + 250, "Overall Difficulty", true);
                d.Track(3, ref d.OD, 0, 100, gx + 118, gy + 241, 241, 10, true);
                d.Label(gx + 122, gy + 270, "Easy");
                d.Label(gx + 314, gy + 270, "Insane", false, null, 41);
                d.Wrap(gx + 9, gy + 293, 350, "The harshness of the hit window and the difficulty of spinners");
                d.Label(gx + 66, gy + 331, "Hold shift for precise difficulty adjustment.");
                break;
            }
            case 2:
            {
                float gx = ox + 6, gy = oy + 6;
                d.Group(gx, gy, 365, 270, "Default Sample Settings");
                if (d.SetsDiffer)
                {
                    d.Wrap(gx + 13, gy + 26, 336, "This beatmap has timing-section dependent volume settings.  \nYou can therefore not set beatmap-wide settings here.");
                    if (d.Button(gx + 234, gy + 62, 123, 23, "Reset Settings")) { d.SetsDiffer = false; d.Changed = true; }
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
                    d.Check(ref d.Overrides, gx + 153, gy + 22, "Enable custom overrides");
                    d.Link(gx + 150, gy + 45, "How to add custom sample overrides");
                }
                if (d.VolumesDiffer)
                {
                    d.Wrap(gx + 12, gy + 105, 345, "This beatmap has timing-section dependent volume settings.  \nYou can therefore not set beatmap-wide settings here.");
                    if (d.Button(gx + 234, gy + 141, 123, 23, "Reset Settings")) { d.VolumesDiffer = false; d.Changed = true; }
                }
                else
                {
                    d.Label(gx + 8, gy + 101, "Sample Volume");
                    d.Track(4, ref d.Volume, 5, 100, gx + 6, gy + 119, 353, 10, false, true, 1);
                }
                string[] sounds = ["Normal", "Whistle", "Finish", "Clap"];
                float[] at = [5, 98, 190, 280];
                for (int i = 0; i < 4; i++)
                    if (d.Button(gx + at[i], gy + 170, i == 3 ? 79 : 75, 23, sounds[i]))
                        samples.Play(d.Set + 1, "hit" + sounds[i].ToLowerInvariant(), d.Overrides ? 1 : 0, Math.Max(8, d.Volume));
                d.Wrap(gx + 6, gy + 207, 353, "Sample settings (sets and volume) can also be assigned independently for each timing section via the timing setup panel (F6 in the editor).");
                gy = oy + 282;
                d.Group(gx, gy, 365, 44, "Misc. Toggles");
                d.Check(ref d.MatchRate, gx + 8, gy + 21, "Samples match playback rate (for fully-hitsounded maps)");
                break;
            }
            case 3:
            {
                float gx = ox + 3, gy = oy + 3;
                d.Group(gx, gy, 365, 202, "Hitcircle/Slider Combos");
                for (int i = 0; i < d.Combos.Count; i++) Colour(i, gx + (i % 2 == 0 ? 7 : 185), gy + 19 + i / 2 * 34, 172, 32, $"Combo {i + 1}", d.Combos[i]);
                if (d.Combos.Count < 8 && d.Button(gx + (d.Combos.Count % 2 == 0 ? 7 : 185), gy + 19 + d.Combos.Count / 2 * 34, 172, 32, "New Combo Colour"))
                    d.Pick = d.Combos.Count; // (added once a colour is picked)
                if (d.Button(gx + 185, gy + 164, 172, 32, "Remove Combo Colour", d.Combos.Count > 2)) { d.Combos.RemoveAt(d.Combos.Count - 1); d.Changed = true; }
                d.Check(ref d.CustomColours, gx + 12, gy + 171, "Enable Custom Colours");
                gy = oy + 208;
                d.Group(gx, gy, 365, 115, "Playfield Background");
                Colour(8, gx + 7, gy + 19, 352, 32, "Background Colour", d.Background);
                d.Wrap(gx + 6, gy + 57, 353, "Please note that this colour is dimmed in both editor compose mode and in gameplay during playtime.  It will be the exact shade of the chosen colour during breaks.");
                break;
            }
            case 4:
            {
                float gx = ox + 6, gy = oy + 4;
                d.Group(gx, gy, 365, 179, "Countdown");
                d.Check(ref d.Countdown, gx + 10, gy + 19, "Enable countdown");
                bool cdOn = d.Countdown;
                d.Link(gx + 201, gy + 19, "Customising the countdown");
                d.Wrap(gx + 3, gy + 40, 356, "If this is checked, an \"Are you ready? 3, 2, 1, GO!\" countdown will be inserted at the beginning of the beatmap, assuming there is enough time to do so.");
                float px = gx + 3, py = gy + 90;
                d.Label(px + 3, py + 3, "Countdown Speed:", true, null, 0, cdOn);
                d.Radio(ref d.Speed, 2, px + 137, py + 1, "Half", cdOn);
                d.Radio(ref d.Speed, 1, px + 206, py + 1, "Normal", cdOn);
                d.Radio(ref d.Speed, 3, px + 275, py + 1, "Double", cdOn);
                d.Label(px + 3, py + 29, "Countdown Offset:", true, null, 0, cdOn);
                double offset = d.CountdownOffset;
                if (d.Numeric(ref offset, 0, 3, 1, "0", px + 137, py + 27, 49, 20, cdOn)) d.CountdownOffset = (int)offset;
                d.Wrap(px + 3, py + 50, 340, "If the countdown sounds off-time, use this to make it appear\none or more beats early.", null, cdOn);
                gy = oy + 189;
                d.Group(gx, gy, 365, 116, "Misc. Toggles");
                d.Check(ref d.Widescreen, gx + 10, gy + 22, "Widescreen Support");
                d.Check(ref d.Letterbox, gx + 10, gy + 44, "Letterbox during breaks");
                d.Check(ref d.Epilepsy, gx + 10, gy + 66, "Display epilepsy warning (storyboard has quick strobing)");
                gy = oy + 311;
                d.Group(gx, gy, 365, 41, "Skinning");
                d.Label(gx + 6, gy + 19, "Preferred Skin:");
                d.Drop(1, d.Skin.Length > 0 ? d.Skin : d.Skins[0], gx + 95, gy + 16, 263, 21);
                break;
            }
            case 5:
            {
                float gx = ox + 6, gy = oy + 4;
                d.Group(gx, gy, 365, 131, "Stacking");
                d.Label(gx + 6, gy + 24, "Stack Leniency:", true);
                d.Track(5, ref d.Stack, 0, 10, gx + 118, gy + 21, 241, 1, false);
                d.Label(gx + 122, gy + 51, "Rarely Stack");
                d.Label(gx + 279, gy + 51, "Always Stack", false, null, 75);
                d.Wrap(gx + 7, gy + 69, 351, "In play mode, osu! automatically stacks notes which occur at the same location.  Increasing this value means it is more likely to snap notes of further time-distance.");
                gy = oy + 141;
                d.Group(gx, gy, 365, 158, "Mode Specific");
                d.Label(gx + 6, gy + 22, "Allowed Modes:", true);
                d.Drop(2, AllowedModes[d.Mode], gx + 118, gy + 19, 236, 21);
                d.Check(ref d.NPlus1, gx + 8, gy + 45, "Use special style(N+1 style) for mania", d.Mode == 3);
                break;
            }
        }

        if (d.Button(4, 392, 232, 34, "OK", SetupOkOn(d), true)) { SetupOk(); return; }
        if (d.Button(242, 392, 139, 34, "Cancel")) { setupDlg = null; return; }

        // on top: an open drop-down's list, the colour picker
        if (!act && d.Open >= 0)
        {
            var (items, box) = DropItems(d);
            d.DrawList(box, items);
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

    // stable re-ranges circle size for the mode: mania's key count 1-9 (from the map's), the others 2-7
    void ModeChanged(SetupDialog d, int was)
    {
        if (d.Mode == 3) d.CS = (int)Math.Round(Math.Clamp(info.CS / (d.Coop ? 2 : 1), 1, 9));
        else if (was == 3) d.CS = (int)Math.Round(Math.Clamp(info.CS, 0, 10) * 10);
    }
}
