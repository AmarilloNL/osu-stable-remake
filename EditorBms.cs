using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// File → Import from... → bms/bme (mania maps): stable's BMS importer and its small window. The chart's timing (its
// BPM, BPM changes and measure lengths), its notes (keys 1-7 and the scratch, which makes the map SpecialStyle; long
// notes by LNTYPE 1 pairs or an LNOBJ end) with their keysounds, and its background sounds as storyboard samples; the
// sound files and the STAGEFILE background are copied into the map's folder. As stable does, the map is saved right
// after (and the import can be undone).
sealed partial class Editor
{
    sealed class BmsWindow
    {
        public string File = "";
        public int Encoding; // utf-8, shift_jis, big5, gb2312
        public bool Meta, Image = true, NoNoteSamples, NoEffects, Clear = true, Typing;
        public Vector2 Pos = new(float.NaN);
        public Vector2? Drag;
        public Vector2 Mouse;
    }
    BmsWindow? bmsWindow;
    const float BmsW = 349, BmsH = 282;
    static readonly string[] BmsEncodings = ["utf-8", "shift_jis", "big5", "gb2312"];

    void OpenBmsImport()
    {
        if (mode != 3) { Notification.Show("This feature is only available for mania beatmaps.", 2000); return; }
        bmsWindow ??= new BmsWindow();
    }

    Rectangle BmsRect(float x, float y, float w, float h) { var p = bmsWindow!.Pos; float s = AiS; return new Rectangle(p.X + x * s, p.Y + (AiCaption + y) * s, w * s, h * s); }
    static readonly (string Text, float Y, float W)[] BmsChecks =
        [("Override meta ", 48, 115), ("Override background image", 79, 193), ("Ignore note samples", 112, 148), ("Ignore effect samples", 144, 154), ("Clear all notes before import", 183, 196)];
    bool BmsCheck(int i) { var w = bmsWindow!; return i switch { 0 => w.Meta, 1 => w.Image, 2 => w.NoNoteSamples, 3 => w.NoEffects, _ => w.Clear }; }

    // (a window the editor waits on, like stable's dialog)
    void UpdateBmsImport(Vector2 mouse)
    {
        var w = bmsWindow!;
        float s = AiS;
        w.Mouse = mouse;
        if (float.IsNaN(w.Pos.X)) w.Pos = new((Game.W - BmsW * s) / 2, (EH - (BmsH + AiCaption) * s) / 2);
        if (Input.IsKeyPressed(KeyboardKey.Escape)) { bmsWindow = null; return; }
        // the file's path can be typed or pasted too
        for (int c; (c = Input.GetCharPressed()) != 0;) if (c >= 32 && w.Typing) w.File += char.ConvertFromUtf32(c);
        if (w.Typing && (Input.IsKeyPressed(KeyboardKey.Backspace) || Input.IsKeyPressedRepeat(KeyboardKey.Backspace)) && w.File.Length > 0) w.File = w.File[..^1];
        if (w.Typing && Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.V)) w.File += GetClipboardText_() ?? "";
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        if (w.Drag is { } d)
        {
            if (!Input.IsMouseButtonDown(MouseButton.Left)) w.Drag = null;
            else w.Pos = new(Math.Clamp(mouse.X - d.X, -BmsW * s / 2, Game.W - BmsW * s / 2), Math.Clamp(mouse.Y - d.Y, 0, EH - AiCaption * s));
            return;
        }
        if (!click) return;
        var cap = new Rectangle(w.Pos.X, w.Pos.Y, BmsW * s, AiCaption * s);
        w.Typing = CheckCollisionPointRec(mouse, BmsRect(92, 12, 244, 21));
        if (CheckCollisionPointRec(mouse, new Rectangle(cap.X + cap.Width - 28 * s, cap.Y, 28 * s, cap.Height))) { bmsWindow = null; return; }
        if (CheckCollisionPointRec(mouse, cap)) { w.Drag = mouse - w.Pos; return; }
        if (CheckCollisionPointRec(mouse, BmsRect(11, 11, 75, 23)))
        {
            // (Windows' dialog would take the focus from a fullscreen game, which then minimises)
            if (IsWindowFullscreen()) Notification.Show("Type or paste the file's path here (Ctrl+V): the file browser isn't available in fullscreen.", 3000);
            else if (PickBmsFile() is { } f) w.File = f;
            return;
        }
        for (int i = 0; i < BmsChecks.Length; i++)
            if (CheckCollisionPointRec(mouse, BmsRect(12, BmsChecks[i].Y, BmsChecks[i].W, 21)))
                switch (i) { case 0: w.Meta = !w.Meta; break; case 1: w.Image = !w.Image; break; case 2: w.NoNoteSamples = !w.NoNoteSamples; break; case 3: w.NoEffects = !w.NoEffects; break; default: w.Clear = !w.Clear; break; }
        if (w.Meta && CheckCollisionPointRec(mouse, BmsRect(233, 49, 103, 20))) w.Encoding = (w.Encoding + 1) % BmsEncodings.Length; // (the encoding: only with the meta, like stable's)
        if (CheckCollisionPointRec(mouse, BmsRect(244, 226, 93, 38))) bmsWindow = null;
        if (CheckCollisionPointRec(mouse, BmsRect(12, 226, 215, 38)))
        {
            if (w.File.Trim().Length == 0) { Notification.Show("Select a bms file first!", 2000); return; }
            if (ImportBms(w.File.Trim().Trim('"'), w)) Save();
            bmsWindow = null;
        }
    }

    static unsafe string? GetClipboardText_() { sbyte* p = GetClipboardText(); return p == null ? null : new string(p); }

    // ---- the chart
    sealed class BmsPoint : IComparable<BmsPoint>
    {
        public int Measure;
        public double Fraction, Time, Bpm = -1, Length = 1; // Length: the measure's, in 4/4 bars
        public bool IsBpm = true;
        public int CompareTo(BmsPoint? o) => o == null ? 1 : Measure != o.Measure ? Measure.CompareTo(o.Measure) : Fraction.CompareTo(o.Fraction);
        public BmsPoint Copy() => (BmsPoint)MemberwiseClone();
    }

    bool ImportBms(string file, BmsWindow w)
    {
        string dir = Path.GetDirectoryName(file) ?? "";
        var wavs = new Dictionary<string, string>();
        var bpms = new Dictionary<string, double>();
        var usedNotes = new HashSet<string>();
        var usedEffects = new HashSet<string>();
        var points = new List<BmsPoint>();
        var channels = new List<string>();
        var open = new EObj?[10]; // (long notes waiting for their end)
        var last = new EObj?[10]; // (each column's latest note, for an LNOBJ end)
        int lnType = 1;
        string lnObj = "";
        double keys = 1;
        int count = 1; // (the key count once read)
        bool special = Kv("General", "SpecialStyle") == "1", shift = false;
        var meta = new List<(string Section, string Key, string Value)>();
        string? stage = null;
        var notes = new List<EObj>();
        var sounds = new List<string>();
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            foreach (var raw in File.ReadLines(file, Encoding.GetEncoding(BmsEncodings[w.Encoding])))
            {
                if (raw.IndexOf('#') < 0) continue;
                string text = raw[1..];
                if (text.IndexOf(':') > 0 && text.Split(':') is { Length: 2 } kv && kv[0].Trim().Length == 5)
                {
                    if (!BmsHeaderChannel(kv[0].Trim(), kv[1].Trim())) channels.Add(text);
                    continue;
                }
                int sp = text.IndexOf(' ');
                if (sp <= 0) continue;
                string key = text[..sp].Trim().ToUpperInvariant(), val = text[sp..].Trim();
                if (key.StartsWith("BPM") && key.Length == 5) { bpms.Add(key.Substring(3, 2), double.Parse(val, CultureInfo.InvariantCulture)); continue; }
                if (key.StartsWith("WAV") && key.Length == 5) { wavs.Add(key.Substring(3, 2), BmsSound(dir, val)); continue; }
                switch (key)
                {
                    case "PLAYER" when int.Parse(val, CultureInfo.InvariantCulture) != 1:
                        Notification.Show("Can not convert 2 player style bms file.", 3000);
                        return false;
                    case "TITLE" when w.Meta: meta.Add(("Metadata", "Title", val)); meta.Add(("Metadata", "TitleUnicode", val)); break;
                    case "ARTIST" when w.Meta: meta.Add(("Metadata", "Artist", val)); meta.Add(("Metadata", "ArtistUnicode", val)); break;
                    case "BPM": points.Add(new BmsPoint { Bpm = double.Parse(val, CultureInfo.InvariantCulture) }); break;
                    case "STAGEFILE" when w.Image && File.Exists(Path.Combine(dir, val)): stage = val; break;
                    case "PLAYLEVEL" when w.Meta: meta.Add(("Metadata", "Version", "Lv." + val)); break;
                    case "RANK" when w.Meta:
                        string judge = Math.Max(4, Math.Min(9, 9 - int.Parse(val, CultureInfo.InvariantCulture) * 2)).ToString(CultureInfo.InvariantCulture);
                        meta.Add(("Difficulty", "HPDrainRate", judge)); meta.Add(("Difficulty", "OverallDifficulty", judge));
                        break;
                    case "LNTYPE": lnType = int.Parse(val, CultureInfo.InvariantCulture); break;
                    case "LNOBJ": lnObj = val; break;
                }
            }
            points.Sort();
            // each point's time: the measures since the one before, at its BPM and measure length (a BPM change brings
            // the length back to 1, like stable's)
            var at = points[0].Copy();
            foreach (var p in points)
            {
                p.Time = (p.Measure + p.Fraction - (at.Measure + at.Fraction)) * (240000.0 / at.Bpm) * at.Length + at.Time;
                at.Time = p.Time; at.Measure = p.Measure; at.Fraction = p.Fraction;
                if (p.Bpm != -1) at.Bpm = p.Bpm; else p.Bpm = at.Bpm;
                if (p.Length != at.Length) at.Length = p.Length;
            }
            count = (int)Math.Round(keys);
            if (count is 6 or 8 && !special) keys = count - 1;
            count = (int)Math.Round(keys);
            shift = !special; // (no scratch: key 1 is the first column)
            foreach (var line in channels)
            {
                var kv = line.Split(':');
                if (kv.Length != 2 || kv[0].Trim().Length != 5) continue;
                BmsNotes(kv[0].Trim(), kv[1].Trim());
            }
            if (open.Any(o => o != null)) throw new InvalidDataException("Something wrong with the bms");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or OverflowException or ArgumentException or InvalidDataException or IndexOutOfRangeException or KeyNotFoundException)
        {
            Notification.Show("Convert failed", 3000);
            return false;
        }

        Change(() =>
        {
            if (w.Clear) objs.Clear();
            selected.Clear();
            objs.AddRange(notes);
            timingSel = -1;
            timing = [.. points.Select(p => new ETiming { Time = (int)p.Time, Beat = p.IsBpm ? 60000.0 / p.Bpm : -100, Meter = (int)(p.Length * 4), Set = 0, Index = 0, Volume = 70, Red = p.IsBpm })];
            SetKv("Difficulty", "CircleSize", ((int)Math.Round(keys)).ToString(CultureInfo.InvariantCulture));
            if (special) SetKv("General", "SpecialStyle", "1");
            foreach (var (section, key, value) in meta) SetKv(section, key, value);
            var ev = EventsSection.Lines;
            if (!w.NoEffects) ev.RemoveAll(l => l.StartsWith("Sample,") || l.StartsWith("5,")); // (the map's sounds make way for the chart's)
            int soundsAt = ev.FindIndex(l => l.Trim() == "//Storyboard Sound Samples");
            if (soundsAt >= 0) ev.InsertRange(soundsAt + 1, sounds); else ev.AddRange(sounds);
            if (stage != null)
            {
                string bg = $"0,0,\"{stage}\",0,0";
                int i = ev.FindIndex(l => l.StartsWith("0,0,"));
                if (i >= 0) ev[i] = bg;
                else ev.Insert(ev.FindIndex(l => l.Trim() == "//Background and Video events") + 1, bg);
            }
        });
        // the sounds (and the background) into the map's folder
        foreach (var f in (w.NoNoteSamples ? [] : usedNotes).Concat(w.NoEffects ? [] : usedEffects).Concat(stage == null ? [] : [stage]))
        {
            try
            {
                string from = Path.Combine(dir, f), to = Path.GetFullPath(Path.Combine(info.Folder, f));
                // (only into the map's folder: a name climbing out of it, or a full path, is left where it is)
                if (to.StartsWith(Path.GetFullPath(info.Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !File.Exists(to) && File.Exists(from))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(from, to, overwrite: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        samples.ForgetMisses(); // (the new sounds)
        return true;

        // a channel line read with the headers: measure lengths, BPM changes, and the key count from the note channels
        bool BmsHeaderChannel(string head, string data)
        {
            if (!int.TryParse(head.Substring(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int channel)) return true; // (a channel this doesn't know, e.g. mines: left out)
            int measure = int.Parse(head[..3], CultureInfo.InvariantCulture);
            var ids = Pairs(data);
            switch (channel)
            {
                case 2:
                    var length = points.Find(p => p.Measure == measure && p.Fraction == 0 && !p.IsBpm);
                    if (length == null) points.Add(new BmsPoint { Length = double.Parse(data, CultureInfo.InvariantCulture), Measure = measure, IsBpm = false });
                    else length.Length = double.Parse(data, CultureInfo.InvariantCulture);
                    points.Add(new BmsPoint { Measure = measure + 1, IsBpm = false }); // (the next measure back to 1)
                    return true;
                case 3:
                    for (int i = 0; i < ids.Count; i++)
                        if (ids[i] != "00") points.Add(new BmsPoint { Measure = measure, Fraction = (double)i / ids.Count, Bpm = Convert.ToInt32(ids[i], 16) });
                    return true;
                case 8:
                    for (int i = 0; i < ids.Count; i++)
                        if (ids[i] != "00" && bpms.TryGetValue(ids[i], out double bpm)) points.Add(new BmsPoint { Measure = measure, Fraction = (double)i / ids.Count, Bpm = bpm });
                    return true;
                case 11 or 12 or 13 or 14 or 15 or 16 or 18 or 19 or 51 or 52 or 53 or 54 or 55 or 56 or 58 or 59:
                    int column = Column(channel);
                    if (column >= Math.Round(keys)) keys = (byte)(column + 1);
                    if (column == 0) special = true;
                    return false;
                default:
                    return false;
            }
        }

        // a note channel line (read after the headers): background sounds, notes and long notes
        void BmsNotes(string head, string data)
        {
            int measure = int.Parse(head[..3], CultureInfo.InvariantCulture), channel = int.Parse(head.Substring(3, 2), CultureInfo.InvariantCulture);
            var ids = Pairs(data.ToUpperInvariant()); // (ids as the headers have them)
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] == "00") continue;
                int t = (int)TimeAt(measure, (double)i / ids.Count);
                if (channel == 1)
                {
                    if (w.NoEffects || !wavs.TryGetValue(ids[i], out var sound) || sound.Length == 0 || sound.IndexOfAny([',', ':']) >= 0) continue; // (not found; a comma or colon would break the line)
                    sounds.Add($"Sample,{t},0,\"{sound}\",70");
                    usedEffects.Add(sound);
                    continue;
                }
                bool longNote = channel > 50;
                if (channel is not (11 or 12 or 13 or 14 or 15 or 16 or 18 or 19 or 51 or 52 or 53 or 54 or 55 or 56 or 58 or 59) || longNote && lnType != 1) continue;
                int column = Column(channel) - (shift ? 1 : 0);
                if (!longNote && lnObj.Length > 0 && ids[i] == lnObj.ToUpperInvariant() && last[column] != null)
                {
                    last[column]!.Type = 128; // (the note before it in this column becomes a hold ending here)
                    last[column]!.End = t;
                    last[column] = null;
                    continue;
                }
                if (longNote && open[column] != null)
                {
                    open[column]!.End = t;
                    open[column] = null;
                    continue;
                }
                var note = new EObj { X = ColumnX(column), Y = 192, Time = t, End = t, Type = longNote ? 128 : 1, Sample = "0:0:0:1:" };
                if (wavs.TryGetValue(ids[i], out var file) && !w.NoNoteSamples && file.Length > 0 && file.IndexOfAny([',', ':']) < 0) { note.Sample = $"0:0:0:0:{file}"; usedNotes.Add(file); }
                notes.Add(note);
                if (longNote) open[column] = note; else last[column] = note;
            }
        }

        double TimeAt(int measure, double fraction)
        {
            var key = new BmsPoint { Measure = measure, Fraction = fraction };
            int n = points.BinarySearch(key);
            if (n < 0) n = ~n;
            var p = points[n != 0 ? n - 1 : 0];
            return (measure + fraction - (p.Measure + p.Fraction)) * (240000.0 / p.Bpm) * p.Length + p.Time;
        }

        int ColumnX(int column) => (int)((column >= count ? column - 0.5f : 0.5f + column) * (512f / count));
    }

    // a key channel's column (11-19, long notes 51-59): keys 1-5 then 6-7 (18, 19); the scratch (16) is column 0
    static int Column(int channel) { int c = channel > 50 ? channel - 40 : channel; return c == 16 ? 0 : c < 16 ? c - 10 : c - 12; }

    static List<string> Pairs(string data) { var list = new List<string>(data.Length / 2); for (int i = 0; i < data.Length - 1; i += 2) list.Add(data.Substring(i, 2)); return list; }

    // a #WAV file: as written if it has a sound extension, else the .ogg, .wav or .mp3 found by that name
    static string BmsSound(string dir, string name)
    {
        if (Regex.IsMatch(name, @"\.(mp3|wav|ogg)$", RegexOptions.IgnoreCase)) return name;
        foreach (var ext in new[] { ".ogg", ".wav", ".mp3" })
            if (File.Exists(dir + name + ext)) return name + ext; // (stable's: the folder and name joined without a separator)
        return "";
    }

    // Windows' file dialog, like stable's "Select file"
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OpenFileName
    {
        public int StructSize;
        public IntPtr Owner, Instance;
        public string Filter;
        public IntPtr CustomFilter;
        public int MaxCustomFilter, FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public IntPtr FileTitle;
        public int MaxFileTitle;
        public string? InitialDir, Title;
        public int Flags;
        public short FileOffset, FileExtension;
        public string? DefExt;
        public IntPtr CustData, Hook;
        public string? TemplateName;
        public IntPtr Reserved;
        public int Reserved2, FlagsEx;
    }
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW")] static extern bool GetOpenFileName(ref OpenFileName ofn);

    static unsafe string? PickBmsFile()
    {
        IntPtr owner = (IntPtr)GetWindowHandle();
        string? picked = null;
        var thread = new Thread(() => picked = PickBmsFile(owner));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return picked;
    }

    static unsafe string? PickBmsFile(IntPtr owner)
    {
        IntPtr buffer = Marshal.AllocHGlobal(4096 * 2);
        try
        {
            *(char*)buffer = '\0';
            var ofn = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(), Owner = owner, Filter = "BMS file\0*.bms;*.bme\0\0",
                File = buffer, MaxFile = 4096, Flags = 0x1000 | 0x800 | 0x8 | 0x80000, // file and path must exist, keep the folder, explorer style
            };
            return GetOpenFileName(ref ofn) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    void DrawBmsImport()
    {
        var w = bmsWindow!;
        float s = AiS, fs = 11 * s;
        if (float.IsNaN(w.Pos.X)) return;
        Color face = new(240, 240, 240, 255), text = Color.Black, grey = new(160, 160, 160, 255);
        var whole = new Rectangle(w.Pos.X, w.Pos.Y, BmsW * s, (BmsH + AiCaption) * s);
        var cap = new Rectangle(w.Pos.X, w.Pos.Y, BmsW * s, AiCaption * s);
        DrawRectangle((int)(whole.X + 4 * s), (int)(whole.Y + 4 * s), (int)whole.Width, (int)whole.Height, new Color(0, 0, 0, 70));
        DrawRectangleRec(whole, face);
        DrawRectangleRec(cap, Color.White);
        Game.Text("Import from bms", cap.X + 6 * s, cap.Y + (cap.Height - fs) / 2, fs, text);
        float cx = cap.X + cap.Width - 14 * s, cy = cap.Y + cap.Height / 2;
        DrawLineEx(new(cx - 4 * s, cy - 4 * s), new(cx + 4 * s, cy + 4 * s), 1.2f * s, text);
        DrawLineEx(new(cx - 4 * s, cy + 4 * s), new(cx + 4 * s, cy - 4 * s), 1.2f * s, text);
        DrawRectangleLinesEx(whole, 1, grey);

        void Button(string label, Rectangle r, bool on = true)
        {
            bool hover = on && CheckCollisionPointRec(w.Mouse, r);
            DrawRectangleRec(r, hover ? new Color(229, 241, 251, 255) : new Color(225, 225, 225, 255));
            DrawRectangleLinesEx(r, 1, hover ? new Color(0, 120, 215, 255) : new Color(173, 173, 173, 255));
            Game.Text(label, r.X + r.Width / 2 - Game.TextWidth(label, fs) / 2, r.Y + (r.Height - fs) / 2, fs, on ? text : grey);
        }
        Button("Select file", BmsRect(11, 11, 75, 23));
        var field = BmsRect(92, 12, 244, 21);
        DrawRectangleRec(field, Color.White);
        DrawRectangleLinesEx(field, 1, w.Typing ? new Color(0, 120, 215, 255) : new Color(122, 122, 122, 255));
        BeginScissorMode((int)field.X + 1, (int)(field.Y + Strip), (int)field.Width - 2, (int)field.Height);
        string shown = w.File + (w.Typing && (int)(Game.NowMs / 500) % 2 == 0 ? "|" : "");
        float tw = Game.TextWidth(shown, fs);
        Game.Text(shown, field.X + 3 * s - Math.Max(0, tw - field.Width + 8 * s), field.Y + (field.Height - fs) / 2, fs, text);
        EndScissorMode();

        for (int i = 0; i < BmsChecks.Length; i++)
        {
            var box = BmsRect(12, BmsChecks[i].Y + 4, 13, 13);
            DrawRectangleRec(box, Color.White);
            DrawRectangleLinesEx(box, 1, new Color(51, 51, 51, 255));
            if (BmsCheck(i))
            {
                DrawLineEx(new(box.X + 3 * s, box.Y + 6.5f * s), new(box.X + 5.5f * s, box.Y + 9.5f * s), 1.5f * s, text);
                DrawLineEx(new(box.X + 5.5f * s, box.Y + 9.5f * s), new(box.X + 10 * s, box.Y + 3.5f * s), 1.5f * s, text);
            }
            Game.Text(BmsChecks[i].Text, box.X + 17 * s, box.Y + (box.Height - fs) / 2, fs, text);
        }
        var label = BmsRect(153, 49, 74, 17);
        Game.Text("File encode", label.X, label.Y + 2 * s, fs, w.Meta ? text : grey);
        var combo = BmsRect(233, 49, 103, 20);
        DrawRectangleRec(combo, w.Meta ? Color.White : face);
        DrawRectangleLinesEx(combo, 1, new Color(122, 122, 122, 255));
        Game.Text(BmsEncodings[w.Encoding], combo.X + 3 * s, combo.Y + (combo.Height - fs) / 2, fs, w.Meta ? text : grey);
        float ax = combo.X + combo.Width - 9 * s, ay = combo.Y + combo.Height / 2;
        DrawTriangle(new(ax - 3.5f * s, ay - 1.5f * s), new(ax, ay + 2 * s), new(ax + 3.5f * s, ay - 1.5f * s), w.Meta ? text : grey);
        Button("Start", BmsRect(12, 226, 215, 38));
        Button("Cancel", BmsRect(244, 226, 93, 38));
    }
}
