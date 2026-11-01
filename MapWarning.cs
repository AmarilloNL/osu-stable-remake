namespace OsuStableRemake;

// stable's warning for maps that award no pp: the yellow notification band for 5s when play starts.
// Texts as a running stable shows them (Loved and Qualified both checked).
sealed partial class Player
{
    bool warningChecked;

    void UpdateMapWarning()
    {
        if (warningChecked || back.StatusOf(mapMd5 ??= Replays.Md5Of(map.File)) is not { } status) return;
        warningChecked = true;
        string? text = status switch
        {
            7 => "This map is in a loved state.\nNo performance points will be awarded.\nLeaderboards may be reset.",
            6 => "This map is in a qualified state.\nNo performance points will be awarded.\nAll scores will be deleted when it is ranked.",
            _ => null,
        };
        if (text != null) Notification.Show(text, 5000, isWarning: true);
    }

    // stable: a map flagged EpilepsyWarning shows osu!ui's epilepsy notice centred over the play as it loads (not on
    // a retry; replays too): in over 200ms, out from 3s to 3.4s (on a clock running at the mod rate), as bright as the
    // storyboard layer (so half in the intro at dim 80; measured).
    internal bool Retried;
    bool epilepsyChecked;
    double epilepsyFrom = double.NaN;

    void DrawEpilepsyWarning(int w, int h)
    {
        if (!epilepsyChecked)
        {
            epilepsyChecked = true;
            if (map.EpilepsyWarning && !Retried && !VirtualClock) epilepsyFrom = Game.NowMs;
        }
        if (double.IsNaN(epilepsyFrom)) return;
        double t = (Game.NowMs - epilepsyFrom) * rate;
        if (t >= 3400) { epilepsyFrom = double.NaN; return; }
        float a = (float)Math.Clamp(Math.Min(t / 200, 1 - (t - 3000) / 400), 0, 1), bright = 1 - bgDim / 100f;
        skin.DefaultTex("epilepsy")?.Draw(new System.Numerics.Vector2(w / 2f, h / 2f), h / 768f, Raylib_cs.Raylib.ColorAlpha(new Raylib_cs.Color((byte)(255 * bright), (byte)(255 * bright), (byte)(255 * bright), (byte)255), a));
    }
}
