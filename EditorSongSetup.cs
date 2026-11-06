using System.Globalization;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// The map's combo colours and [General] values, as the Song Setup dialog reads and writes them.
sealed partial class Editor
{
    // [Colours]: Combo1..ComboN, as stable writes them ("Combo1 : r,g,b")
    List<(int R, int G, int B)> ComboColours()
    {
        var s = sections.FirstOrDefault(x => x.Name == "Colours");
        var list = new List<(int, int, int)>();
        for (int n = 1; n <= 8; n++)
        {
            var line = s?.Lines.FirstOrDefault(l => l.Split(':')[0].Trim() == $"Combo{n}");
            if (line == null) break;
            var v = line[(line.IndexOf(':') + 1)..].Split(',').Select(p => int.TryParse(p.Trim(), out int x) ? Math.Clamp(x, 0, 255) : 0).ToArray();
            list.Add(v.Length >= 3 ? (v[0], v[1], v[2]) : (255, 255, 255));
        }
        return list;
    }
    void SetComboColours(List<(int R, int G, int B)> colours)
    {
        var s = sections.FirstOrDefault(x => x.Name == "Colours");
        if (s == null)
        {
            s = new Section("Colours");
            sections.Insert(Math.Max(0, sections.FindIndex(x => x.Name == "HitObjects")), s); // (before [HitObjects], where stable puts it)
        }
        int at = s.Lines.FindIndex(l => l.TrimStart().StartsWith("Combo"));
        s.Lines.RemoveAll(l => l.TrimStart().StartsWith("Combo"));
        at = at < 0 ? 0 : Math.Min(at, s.Lines.Count);
        s.Lines.InsertRange(at, colours.Select((c, i) => $"Combo{i + 1} : {c.R},{c.G},{c.B}"));
        modified = true;
        info = ReloadInfo();
    }

    string General(string key, string fallback) => Kv("General", key) is { Length: > 0 } v ? v : fallback;
    void SetGeneral(string key, string value) { if (General(key, "") != value) Change(() => SetKv("General", key, value)); }
}
