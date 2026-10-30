using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's replay overlay: the "REPLAY MODE" banner ticker and the buttons down the right
// edge, centred 40 units in from it at y 85 + 20 per button, fading in over 500ms and tinting (255,247,197) on
// hover. Offline, stable skips the Discussion button, so there are three. F cycles the speed like the button;
// H hides the overlay and the leaderboard.
// TODO: Collections shows but does nothing (stable opens its collection editor; we never write collection.db).
sealed partial class Player
{
    double overlayFrom = double.NaN;
    int speedState; // stable's order: 0 = 1x, 2 = x1.5, 1 = x1.5^-2 (about 0.67x)
    double speed = 1;
    bool overlayHidden;

    static readonly string[] OverlayButtons = ["overlay-collections", "speed", "overlay-endreplay"];

    string OverlayTexture(int i) => OverlayButtons[i] != "speed" ? OverlayButtons[i]
        : speedState switch { 2 => "overlay-2x", 1 => "overlay-half", _ => "overlay-1x" };

    Rectangle OverlayRect(int i, Sprite s)
    {
        float u = GetScreenHeight() / 480f, sd = GetScreenHeight() / 768f;
        var c = new Vector2(GetScreenWidth() - 40 * u, ((mods != Mods.None ? 140 : 85) + 20 * i) * u); // below the mod icons when there are any (measured)
        return new Rectangle(c.X - s.Width * sd / 2, c.Y - s.Height * sd / 2, s.Width * sd, s.Height * sd);
    }

    // Returns true when the replay should end (the End Replay button).
    bool UpdateReplayOverlay()
    {
        if (playback == null && !auto) return false; // stable plays Auto as a replay, overlay included
        if (Input.IsKeyPressed(KeyboardKey.H)) overlayHidden = !overlayHidden;
        if (Input.IsKeyPressed(KeyboardKey.F)) CycleSpeed();
        if (overlayHidden) return false;
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        for (int i = 0; i < OverlayButtons.Length && click; i++)
        {
            if (skin.Tex(OverlayTexture(i)) is not { } s || !CheckCollisionPointRec(Input.GetMousePosition(), OverlayRect(i, s))) continue;
            if (OverlayButtons[i] == "overlay-endreplay") return true;
            if (OverlayButtons[i] == "speed") CycleSpeed();
        }
        return false;
    }

    void CycleSpeed()
    {
        (speedState, double factor) = speedState switch { 0 => (2, 1.5), 2 => (1, 1 / 2.25), _ => (0, 1.5) };
        SetSpeed(speed * factor);
    }

    // Replay playback speed: only the clock and the music change; judgement stays in map time.
    void SetSpeed(double s)
    {
        clockOrigin = Raw;
        if (VirtualClock) virtualElapsed = 0;
        else { sw.Restart(); if (paused) sw.Stop(); }
        speed = s;
        music?.SetTempo(rate * speed, mods.HasFlag(Mods.Nightcore));
        if (musicStarted && !musicEnded) music?.Seek(Raw); // flush audio buffered at the old tempo
    }

    void DrawReplayOverlay(int w, float u, double now)
    {
        if (playback == null && !auto || overlayHidden) return;
        if (double.IsNaN(overlayFrom)) overlayFrom = now;
        double age = now - overlayFrom;

        // Banner at y 60: fades in over 4s and crosses from the right edge until fully off the left, each pass
        // lasting 52ms per unit of its width, looping.
        string who = watching == null ? "osu!" : watching.Player.Length > 0 ? watching.Player : Config.Get("Username", ""); // Auto plays as "osu!"
        string text = $"REPLAY MODE - Watching {who} play {map.Artist} - {map.Title} [{map.Version}]";
        float tw = Game.TextWidth(text, 13 * u) / u, wu = w / u;
        double pass = age / (tw * 52) % 1;
        float bannerAlpha = (float)Math.Min(1, age / 4000);
        Game.Text(text, (float)(wu - pass * (wu + tw)) * u, 59 * u, 13 * u, ColorAlpha(Color.White, bannerAlpha));

        float sd = GetScreenHeight() / 768f, fade = (float)Math.Min(1, age / 500);
        var mouse = Input.GetMousePosition();
        for (int i = 0; i < OverlayButtons.Length; i++)
        {
            if (skin.Tex(OverlayTexture(i)) is not { } s) continue;
            var r = OverlayRect(i, s);
            var tint = CheckCollisionPointRec(mouse, r) ? new Color(255, 247, 197, 255) : Color.White;
            s.Draw(new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2), sd, ColorAlpha(tint, fade));
        }
    }
}
