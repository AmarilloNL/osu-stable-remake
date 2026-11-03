using System.Collections.Concurrent;
using System.IO.Compression;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's beatmap import screen (F5, or beatmap archives dropped on the window): osu!ui's beatmapimport-top banner
// over the background, status lines (size 14 with a shadow) centred at y 240 where each new one pushes the older
// ones 20 units up over 500ms as they fade out over 10s, and the beatmapimport-spinner ring tinted (255, 96, 171) 70
// units below, popping in (0.5 -> 1.3 -> 0.9 -> 1 over 500ms), fading in over 2s and turning once per 1.5s.
// Archives go to Songs\<file name> (stable moves the file there first and deletes it once extracted; we leave the
// original where it is), then the library is read again and song select comes back with the imported map selected.
sealed class BeatmapImport : Screen
{
    readonly SongSelect songs;
    readonly Task<string?> work;
    double start = Game.NowMs;
    readonly ConcurrentQueue<string> incoming = new();
    readonly List<(string Text, double At)> lines = [];
    bool reloading;
    readonly bool reload, minimum; // minimum: shown at least 300ms (imports and F5)

    // full: F5 (stable's full process: the whole library is read again)
    public BeatmapImport(SongSelect songs, IReadOnlyList<string> archives, bool full = false)
    {
        this.songs = songs;
        incoming.Enqueue("Checking for new files...");
        var files = archives.ToArray();
        // stable's way into song select passes through here too, with nothing to extract (the main menu only comes here
        // when the Songs folder changed or the library is still loading): the library is only read again for a change
        reload = files.Length > 0 || full || SongSelect.SongsPending;
        minimum = files.Length > 0 || full;
        if (files.Length == 0 && SongSelect.SongsPending) incoming.Enqueue("Loading new files only...");
        else if (files.Length == 0 && full) incoming.Enqueue("Reloading database...");
        work = Task.Run(() => Import(files));
    }

    // Extracts each archive; returns an imported osu! map's MD5 to select.
    string? Import(string[] files)
    {
        SongSelect.IgnoreSongsUntil = double.MaxValue; // (its own extracting isn't news)
        string? select = null;
        foreach (var file in files)
        {
            string name = Path.GetFileName(file);
            incoming.Enqueue($"Importing {(name.Length > 50 ? name.Remove(50) + "..." : name)}");
            var dest = Path.Combine(Config.SongsDir, Path.GetFileNameWithoutExtension(file));
            bool existed = Directory.Exists(dest);
            try
            {
                ZipFile.ExtractToDirectory(file, dest, overwriteFiles: true); // refuses entries that would land outside dest
                select ??= Directory.EnumerateFiles(dest, "*.osu").Order(StringComparer.OrdinalIgnoreCase)
                    .Select(f => SongSelect.ReadMap(f, stars: false)).FirstOrDefault(m => m is { Mode: 0 })?.Md5;
            }
            catch (InvalidDataException)
            {
                // stable moves a broken archive into the Songs folder's "Failed" folder
                Toast.Show("The beatmap file is corrupt or incomplete. Moving to Failed folder.");
                try
                {
                    if (!existed && Directory.Exists(dest)) Directory.Delete(dest, true); // what this import half extracted
                    string failed = Path.Combine(Config.SongsDir, "Failed");
                    Directory.CreateDirectory(failed);
                    File.Move(file, Path.Combine(failed, name), overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Toast.Show($"Unable to move file {name}. It is probably in use by another application."); }
        }
        SongSelect.IgnoreSongsUntil = Game.NowMs + 2000;
        return select;
    }

    public override void Entered() => start = Game.NowMs; // (after a fade, the screen only shows from here)

    public override void Update(double dtMs)
    {
        songs.Ambient(); // the music and background carry on
        while (incoming.TryDequeue(out var text)) lines.Add((text, Game.NowMs));
        if (!work.IsCompleted) return;
        if (!reloading) { reloading = true; if (reload) songs.Reload(work.Result); return; }
        if (!songs.LibraryLoading && (!minimum || Game.NowMs - start >= 300)) Game.Screen = songs; // shown at least 300ms (stable took about that for one small archive)
    }

    public override void Draw()
    {
        int w = Game.W, h = Game.H;
        float u = h / 480f, sd = h / 768f;
        double now = Game.NowMs;
        songs.DrawBackground(w, h);
        if (Game.Skin.DefaultTex("beatmapimport-top") is { } top) top.Draw(new Vector2(w / 2f, top.Height * sd / 2), sd, Color.White);

        float size = 14.45f * u; // stable: 14 (renders this wide, like its other size-14 text)
        for (int i = 0; i < lines.Count; i++)
        {
            // every later line pushed this one 20 units up (500ms each) and set it fading out over 10s
            float y = 240, a = 1;
            for (int j = i + 1; j < lines.Count; j++)
            {
                double t = now - lines[j].At;
                y -= 20 * (float)Math.Clamp(t / 500, 0, 1);
                if (j == i + 1) a = 1 - (float)Math.Clamp(t / 10000, 0, 1);
            }
            if (y < 0 || a <= 0) continue;
            float x = w / 2f - Game.TextWidth(lines[i].Text, size) / 2, ty = y * u - size / 2, drop = Math.Max(1, u / 3);
            Game.Text(lines[i].Text, x + drop, ty + drop, size, ColorAlpha(Color.Black, 0.5f * a));
            Game.Text(lines[i].Text, x, ty, size, ColorAlpha(Color.White, a));
        }

        if (Game.Skin.DefaultTex("beatmapimport-spinner") is { } ring)
        {
            double t = now - start;
            float scale = t < 200 ? 0.5f + 0.8f * (float)Easing.Apply(1, t / 200)
                : t < 400 ? 1.3f - 0.4f * (float)Easing.Apply(2, (t - 200) / 200)
                : 0.9f + 0.1f * (float)Easing.Apply(1, Math.Min(1, (t - 400) / 100));
            float alpha = (float)Math.Min(1, t / 2000), angle = (float)(t / 1500 % 1 * 360);
            ring.Draw(new Vector2(w / 2f, 310 * u), sd * scale, ColorAlpha(new Color(255, 96, 171, 255), alpha), angle);
        }
        SongSelect.DrawCursor();
    }

    // ---- files dropped on the window (stable: the same as opening them with osu!)
    static readonly List<string> queued = []; // archives waiting for a screen that can import them

    public static void Open(string[] files)
    {
        var songs = SongSelect.Instance;
        if (songs == null) return;
        foreach (var file in files)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".osz" or ".olz":
                    queued.Add(file);
                    if (Game.Screen is Player) Notification.Show("New beatmap available - visit song select to finish importing!", 1000); // stable's
                    break;
                case ".osr": OpenReplay(songs, file); break;
                case ".osk": ImportSkin(songs, file); break;
            }
        }
    }

    // Archives wait while a play (or another screen that can't be left) is up; stable imports them afterwards too.
    public static void Poll()
    {
        if (queued.Count == 0 || SongSelect.Instance is not { } songs || Game.Screen is not (SongSelect or MainMenu) || Game.Fading) return;
        Game.Screen = new BeatmapImport(songs, [.. queued]);
        queued.Clear();
    }

    // stable: a replay opens its ranking screen with its map selected
    static void OpenReplay(SongSelect songs, string file)
    {
        if (Game.Screen is Player) return; // stable refuses while playing
        ReplayInfo info;
        try { info = Replays.ReadAnyInfo(file); }
        catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException or ArgumentException or IndexOutOfRangeException)
        {
            Toast.Show("Unable to read the requested replay file."); // stable's
            return;
        }
        if (songs.FindMap(info.BeatmapMd5) is not { } map) { Toast.Show("You do not have the beatmap this replay was made for."); return; }
        Game.FadeTo(() => Player.Summary(Beatmap.Load(map.FilePath, mods: info.Mods), songs, info));
    }

    // stable: a skin goes to Skins\<file name> (a lone inner folder flattened), becomes the current skin, and the main
    // menu opens with the options at the skin section
    static void ImportSkin(SongSelect songs, string file)
    {
        if (Game.Screen is Player) return;
        string name = Path.GetFileNameWithoutExtension(file), dest = Path.Combine(Config.OsuDir, "Skins", name);
        try
        {
            ZipFile.ExtractToDirectory(file, dest, overwriteFiles: true);
            for (int i = 0; i < 5 && Directory.GetFiles(dest).Length == 0 && Directory.GetDirectories(dest) is [var inner]; i++)
            {
                foreach (var f in Directory.GetFiles(inner)) File.Move(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
                foreach (var d in Directory.GetDirectories(inner)) Directory.Move(d, Path.Combine(dest, Path.GetFileName(d)));
                Directory.Delete(inner);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Toast.Show($"Couldn't import {Path.GetFileName(file)}.");
            return;
        }
        Config.SetOwn("Skin", name);
        Game.Skin.Unload();
        Game.Skin = new Skin(dest);
        if (songs.Menu is MainMenu menu) Game.Screen = new Options(menu, songs, "SKIN");
    }
}
