namespace OsuStableRemake;

// File → Open Difficulty... → For Reference (mania, stable's): another difficulty of the set shown beside the map's
// stage, one column to its right, in its own key count and read only; None takes it away.
sealed partial class Editor
{
    List<EObj>? reference;
    int referenceKeys;
    string? referencePath;

    MenuItem[] ReferenceItems() =>
        [new("None", "", () => { reference = null; referencePath = null; }, reference == null),
         .. OtherDifficultyFiles().Select(f => new MenuItem(DifficultyName(f), "", () => LoadReference(f), f == referencePath))];

    void LoadReference(string file)
    {
        try
        {
            var lines = File.ReadAllLines(file);
            string? Get(string key) => lines.Select(l => l.Split(':', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Trim() == key)?[1].Trim();
            if (Get("Mode") != "3") return; // (stable only takes another mania difficulty)
            int at = Array.FindIndex(lines, l => l.Trim() == "[HitObjects]");
            var list = new List<EObj>();
            for (int i = at + 1; at >= 0 && i < lines.Length && !lines[i].TrimStart().StartsWith('['); i++)
                if (lines[i].Trim().Length > 0 && ParseObject(lines[i].Trim()) is { } o) list.Add(o);
            reference = [.. list.OrderBy(o => o.Time)];
            referenceKeys = Math.Clamp((int)Math.Round(double.TryParse(Get("CircleSize"), System.Globalization.NumberStyles.Float, Inv, out double cs) ? cs : 4), 1, 18);
            referencePath = file;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notification.Show("Cannot load this file!", 2000); }
    }
}
