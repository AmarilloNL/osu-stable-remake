<div align="center">

# osu!stable remake

**An offline, from-scratch rewrite of osu!stable — same feel, lower frame times.**

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-raylib--cs%207-239120?logo=csharp&logoColor=white)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D6?logo=windows&logoColor=white)
![Offline](https://img.shields.io/badge/online-never-lightgrey)
![Replay accuracy](https://img.shields.io/badge/stable%20replay%20accuracy-983%2F1088%20(90.3%25)-ff66aa)
![Status](https://img.shields.io/badge/status-work%20in%20progress-orange)

</div>

---

osu!stable remake plays your existing **osu!stable** library — songs, skins, replays, scores and settings —
in a new client written from the ground up in C#. The goal is to judge, look and feel like stable,
while running with sub-millisecond frame times and handling frames better.

It is **offline only**: it never talks to any osu! server. Everything it saves (its own settings, replays,
screenshots) goes next to its own executable; it only writes to your osu! folder when you import a beatmap or skin, save a beatmap in the editor, delete a beatmap (to the Recycle Bin, like stable), export your skin (`Exports`), change collections (`collection.db`) or use Remove from Unplayed / Mark all maps as played (flags in `osu!.db`); the last ones only while stable is closed.

> [!NOTE]
> This is a personal hobby project. It is not affiliated with, endorsed by, or connected to ppy Pty Ltd
> or osu!. No osu! code or assets are included here: skins, fonts and sounds are read from your own
> installation at runtime.

## ✨ Highlights

| | |
|---|---|
| 🎯 **Stable judging** | Hit windows, notelock, slider tracking (stable's button rules and segmented ball), slider curves, stacking, the momentum spinner and HP drain are re-implemented to behave like stable. Checked against real stable replays: **983 of 1088** re-judge to the exact same 300/100/50/miss counts and max combo. |
| ⚡ **Fast** | ~0.2 ms average frame time during gameplay, almost no per-frame allocation, sprites decoded in the background while you pick a map. Judging runs in fixed 1 ms steps, so results never depend on your frame rate. |
| 🎨 **Your skin, your way** | Reads your skins (with stable's default skin as fallback) and recreates stable's HUD: score, combo, accuracy pie, hit error meter, key overlay, health bar and the animated in-game leaderboard. |
| 🥁 **All four modes** | osu!taiko, osu!catch and osu!mania play their own maps, and osu! maps convert to them, with stable's hit windows, scoring, health and Auto. |
| ✏️ **Beatmap editor** | Place and move circles, sliders and spinners on stable's timeline and grid, edit timing points and song setup, test-play and save. |
| 🎬 **Storyboards** | Sprites, animations, all commands and 35 easings, loops, triggers, variables, layers and pass/fail states. |
| 📼 **Replays** | Its own exact replay format, plus import and playback of your stable `.osr` replays, with stable's replay overlay and speed control. |
| 🎵 **Low-latency audio** | BASS audio with a clock that stays within ~0.15 ms of the music. |

## 🕹️ What's in

<details open>
<summary><b>Gameplay</b></summary>

- osu!standard with all of stable's mods and hotkeys: EZ NF HT HR SD PF DT NC HD FL RX AP SO, Auto, Cinema and Score V2 (stable's V2 scoring and slider-head judging)
- Stable's flashlight, countdown, breaks (pass/fail), warning arrows, skip, the fail animation and sound
- Pause and resume exactly like stable (click the orange cursor to resume), hold-to-retry, Visual Settings bar (dim, storyboard, video, skin, hitsound toggles, per map)
- Follow points, snaking sliders, judgement animations and particles, old- and new-style spinners
- Loved/Qualified map warning, local beatmap offset with `+` / `-` (`Alt` for 1ms steps; read from stable's `osu!.db`)
- Stable's keyboard bindings (Options → Change keyboard bindings), F7 frame limiter (with stable's menu, pause and background rates), F10 mouse buttons, F12 screenshots, the Boss key (hides to a notification-area icon)
- Mouse back / forward buttons as Escape / Skip; the cursor kept in the window during a play, raw input included
- Stable's window behaviour: minimise on alt-tab in fullscreen, display kept awake while focused, Windows pen gestures off, the map in the window title during a play
</details>

<details>
<summary><b>Game modes</b></summary>

- Switch modes like stable (`Ctrl+1`-`4` or the Mode button); every map is listed in every mode, taiko / catch / mania maps with their mode's icon, and play in their own mode; osu! maps convert to the chosen one
- **osu!taiko**: stable's hit windows and scroll speed per timing point, osu! sliders converted to hits or drumrolls, swells (their count plus one hits, a bonus that leaves the combo alone), big notes for both hands, bar lines, health from empty (a pass needs it over half) and stable's scoring (kiai 1.2x). The drum, hit target and the mascot (idle, kiai, fail, cheering at combo milestones) from your skin, hit notes thrown to the health bar, combo flowers. Keys: `Z` `X` `C` `V` (rim, centre, centre, rim)
- **osu!catch**: fruits, droplets, tiny droplets and banana showers converted like stable (its seeded randomness included), the catcher's size from CS, walking and dashing, hyperdashes, fruit piling on the plate and thrown off at a combo's end, the catch flashes, dash and hyperdash after-images, `comboburst-fruits`. Keys: `←` `→`, `Shift` to dash
- **osu!mania**: 1-18 keys (10 and more, and Key Coop, on two stages like stable), notes and hold notes, stable's hit windows (MAX at 16ms) and ScoreV1 scoring (a full combo of MAXes is exactly 1,000,000), mania health, stable's default stage and scroll speed (`F3` / `F4`), the hit lighting and combo bursts; osu! maps convert with stable's key count (shown in song select) and its pattern generator, seeded the same way, so they get the same notes as in stable. Your key layouts from stable's settings (`ManiaLayouts`), else stable's defaults (4K: `D` `F` `J` `K`; two stages around `Space` and right `Alt`)
- Each mode's mod select like stable's: mania's key mods (1K-9K for osu! maps), Key Coop, Fade In, Mirror and Random; no Autopilot or Spun Out in taiko and catch; Relax in taiko (either colour, swells in any order) and catch (the mouse moves the catcher); Hidden, Flashlight and Perfect in every mode; each mode's own score multipliers
- Ranking screens with each mode's judgements, replays (stable's `.osr` of these modes too), per-mode leaderboards
</details>

<details>
<summary><b>Beatmap editor</b></summary>

- From the main menu's **Edit** (pick a map in song select) or **Beatmap options** (`F3`) → **Edit**
- Stable's menu bar (File, Edit, View, Compose, Design, Timing, Web, Help) with its items and keys; what this editor can't do yet is greyed out
- **Compose** (`F1`): the timeline with beat ticks coloured by snap (`Alt`+wheel changes the divisor, `Ctrl`+wheel zooms), the playfield with its grid; tools `1`-`4` (select, circle, slider, spinner), new combo `Q` and hitsounds `W` `E` `R`, grid snap `T` and size `G`; drag-select, move, drag in time, copy / cut / paste, delete, undo / redo (`Ctrl+Z` / `Ctrl+Y`)
- Sliders: click the head and each point, right-click (or click the last point again) to finish; the length snaps to the beat. A selected slider's points drag, `Ctrl`+click on its body adds one, right-click removes one
- Stable's Edit menu keys: flip (`Ctrl+H` / `J`), rotate 90° (`Ctrl+<` / `>`), Rotate by / Scale by (`Ctrl+Shift+R` / `S`, previewed as you drag), reverse (`Ctrl+G`), clone (`Ctrl+D`); bookmarks (`Ctrl+B`, `Ctrl+Shift+B`, `Ctrl+←` / `→`); live mapping (`Ctrl+Tab`: tap your taiko or mania keys while the song plays)
- **AiMod** (`Ctrl+Shift+A`): stable's map checker in its own small window, its problems sorted into Compose, Design, Timing, Meta and Mapset (unsnapped and overlapping objects, offscreen objects, short spinners, long combos, distance snap with *Check distance snap*, and more); a click on one jumps there and selects its objects
- osu!mania keysounds: **Sample import** (`Ctrl+Shift+I`) gives the selected notes one of the folder's sound files (or a custom sample set) and a volume, or adds it as a storyboard sound; **Auto Volume Balance** shares out the volume between notes struck together
- View options like stable's: the map's video and storyboard behind the playfield (*Show Video/Storyboard*), *Dim Background*, *Hit animations*, *Follow points*, and the mania view's *Show Sample Name*
- Each mode like stable's editor: taiko maps red and blue (whistle / clap for kat), catch's distance snap across only, and mania maps in their own compose view: a column stage with the beat snap's lines, the song's note density beside it, Note and Hold tools
- **Design** (`F2`): the storyboard at the editor's time with stable's layer toggles; add sprites from the **Sprite Library** (`I`) and keyframe them: pick Move, Scale, Fade or Rotate and drag the sprite (or nudge with `W` `A` `S` `D`) to set its value now, Tweening joins keyframes into a transformation (eased In or Out), the timeline shows the picked sprite's transformations. New sprites go to the map's `.osb` like stable's (*Diff Specific* moves one into the difficulty); sprites with loops, triggers or variables are shown but not edited
- **Timing** (`F3`): add, move and delete red and green points, BPM, offset, meter, slider velocity, volume, sample set and kiai
- **Song setup** (`F4`): artist, title, creator, difficulty name, source, tags, the difficulty sliders (mania's Key Count) and the map's mode
- Playback with hitsounds at 25-100% speed (`Space`, `Z` `X` `C` `V` `B`), the wheel and arrow keys step through the beat snap
- `F5` test-plays the map as it stands from the current position (like stable) and comes back; `Ctrl+S` saves; `Esc` asks to save. Lines you didn't touch are written back exactly as they were
</details>

<details>
<summary><b>Song select</b></summary>

- Reads stable's `osu!.db` directly, in the background (a 100k-map library is ready in a few seconds)
- Stable's carousel, search with filters (`stars>5 ar=9 length<120`), sorting and grouping (collections, recently played, artist, difficulty)
- Mod-adjusted star ratings, preview music, local leaderboard and personal best
- Beatmap thumbnails on the panels from the ones stable already downloaded (`Data\bt`), with stable's layout for maps without one
- Mod select (`F1`) and beatmap options (`F3`) as stable's dialogs, random (`F2`) and rewind (`Shift+F2`), play with Auto (`Ctrl+Enter`) or Cinema (`Ctrl+Shift+Enter`)
</details>

<details>
<summary><b>Menus & UI</b></summary>

- Main menu with the beating cookie, audio visualizer, seasonal backgrounds, tips and music controls
- Ranking screen with stable's layout, HP graph and replay watching
- Options panel as stable's: all nine sections with dropdowns, buttons, stable's tooltips and the skin preview (online parts are shown but never connect; changes go to `remake.cfg`)
- `Ctrl+Alt+Shift+S` reloads the skin from disk like stable's (queued until you're back in the menus during a play or on a ranking screen)
- Stable's offset wizard (Options → Audio): ticks scroll to the beat of the song that's playing while you set the universal offset
- Menu snow, kiai star fountains, break stars, combo bursts and kiai beat pulses, ported from stable
- Volume overlay, stable's FPS / frame time counter and its frame time graph (`Ctrl+F11`: update, draw, present and limiter time per frame, plus garbage collections), notifications
- Beatmap (`.osz`) and skin (`.osk`) imports and stable replays (`.osr`): drop them on the window or open them with the executable, like stable (a game that's already running takes them)
- Stable's display modes: fullscreen at its own resolution, or letterboxed ("Render at native resolution": the monitor keeps its resolution and the game sits in a movable box)
- Stable's Softening filter (Graphics), matching stable pixel for pixel
</details>

<details>
<summary><b>Replays</b></summary>

- Every play is saved as a replay next to the executable (`Replays/`); failed plays can be saved with `F2`
- Stable `.osr` replays from your osu! folder show up in the leaderboards marked `[stable]`
- Stable's replay overlay with speed control (`F`); `H` hides it and the leaderboard
</details>

## 📋 Roadmap

Progress at a glance. Checked items are done; everything else is still open.

**Core**
- [x] osu!standard gameplay with stable's judging, scoring and HP
- [x] All stable mods and mod hotkeys
- [x] Storyboards
- [x] Skins with stable's default skin fallback, and beatmap-specific skin elements
- [x] Song select reading `osu!.db`, collections, search and grouping
- [x] Own replays (exact playback) and stable `.osr` import
- [x] Main menu, options, ranking screen, pause/fail menus
- [x] Background videos (decoded by Windows Media Foundation, no extra libraries; the Video option turns them off)
- [x] Other modes: osu!taiko, osu!catch, osu!mania (their own maps and osu! conversions)
- [x] The beatmap editor (compose, timing, song setup, test play, saving)
- [x] Replays for taiko, catch and mania; stable's osu!mania conversion patterns
- [ ] The Design tab for sprites with loops, triggers or variables, and the Colour transformation
- [ ] Linux / macOS (currently uses Windows fonts and Windows BASS builds)

**Accuracy vs stable**
- [x] Hit windows, notelock, stacking, slider curves and timing ported from stable's behaviour
- [x] Slider tracking with stable's button rules
- [x] Judging identical to a running stable (300/100/50/miss, max combo and score) on 39 maps with generated sloppy replays, and with DoubleTime, HardRock, HalfTime, Easy and Score V2 (scores too)
- [ ] The last ~10% of mismatches on real 60Hz replays (mostly cursor right on the slider follow-radius edge)
- [x] HP drain calibration identical to stable's, with and without HardRock or Easy (checked object by object against a running stable: 39 maps without mods, 32 with HR or EZ)
- [x] Slider curves, timing, ticks, repeats and stacking bit-identical to stable on 39 maps (18,602 objects, also with HR and EZ; one HR slider ends a float step off)
- [x] Song select, main menu, mod select, dialogs, HUD, pause/fail and ranking screens measured pixel by pixel against stable at 720p, 900p and 1080p
- [x] Stable's rule that ignores clicks on a stacked circle while the one below it is still unhit

**Missing pieces**
- [x] Hit lighting
- [x] Beatmap options (`F3`): deleting a set, a difficulty or everything listed (to the Recycle Bin, like stable; also `Shift+Delete`), Remove from Unplayed, Clear local scores (the replays stay; the list leaves those scores out)
- [x] Options: Export as .osk (into osu!'s `Exports` folder, like stable), Delete all unranked maps (to the Recycle Bin, after stable's confirmation) and Mark all maps as played
- [x] Beatmap options (`F3`): Manage Collections (make, rename and delete collections, add or remove the map or its set; written to stable's `collection.db` while stable is closed)
- [x] Visual Settings per map (stable's from `osu!.db`, changes kept in `remake.cfg`)
- [x] Libraries without a readable `osu!.db`: the Songs folder is read in parallel and cached, star ratings come in the background
- [x] Verify guessed details: the Qualified warning text, notice texts and menu tips (read from a running stable)
- [x] Options: Preview gameplay (after stable's confirmation, Auto on a random map from your library)

**Not planned**
- Online features of any kind (login, multiplayer, online leaderboards, score submission, beatmap uploading)

## 🚀 Getting started

**You need**
- Windows 10 or 11 (x64)
- The [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An osu!stable installation with your songs (the game reads its `osu!.db`, `Songs`, `Skins` and settings)

**Build and run**

```powershell
git clone https://github.com/AmarilloNL/osu-stable-remake.git
cd osu-stable-remake
dotnet build -c Release
& ".\bin\Release\net10.0\osu!stable remake.exe"
```

The game looks for osu!stable in its default location (`%LOCALAPPDATA%\osu!`). If yours is somewhere else,
create `remake.cfg` next to `osu!stable remake.exe`:

```ini
OsuDir = D:\Games\osu!
```

## ⚙️ Configuration

Your osu! settings (`osu!.<username>.cfg`) are read as-is: keys, skin, dim, volumes, offset and so on.
Anything you change inside osu!stable remake is written to `remake.cfg` next to the executable instead, and
overrides the osu! value. Your osu! settings and databases are never modified.

## 🧪 Developer tools

The executable doubles as a test harness:

| Command | What it does |
|---|---|
| `--check` | Parses every map in the library and validates the results |
| `--verify-osr N` | Re-judges up to N of your stable replays headlessly and compares them with what stable recorded (`VERIFY_LOG=1` for a per-judgement log, `VERIFY_MD5=<hash>` for one map, `VERIFY_STEP=1` to judge them frame by frame like stable's replay viewer instead of as played live) |
| `--auto-check [filter] [--mods HR]` | Plays maps with Auto at several frame times, lists any run that isn't all 300s, and prints each score |
| `--play <file.osu> [--auto] [--mods HDDT]` | Starts a map directly |
| `--watch <replay>` | Watches a replay |
| `--fuzz-osr <file.osu> <seed> <out.osr> [mods]` | Writes a deliberately sloppy replay as a stable `.osr` (NoFail) and prints our judgement of it; stable can watch the same file, so the two judgements can be compared |
| `--shots 5,10 --exit-after 12` | Takes screenshots at the given seconds and quits; prints frame time stats |
| `--input "3:key Enter, 4:click 640 360"` | Scripted input for UI tests |

## 📜 Credits & licenses

- [raylib](https://www.raylib.com/) via [Raylib-cs](https://github.com/ChrisDill/Raylib-cs) for graphics and input
- [BASS](https://www.un4seen.com/) and BASS_FX by un4seen developments for audio (free for non-commercial use; the DLLs in `lib/` are covered by their license)
- Menu tips are from the [osu! wiki](https://osu.ppy.sh/wiki/en/Client/Menu_tips) (CC BY-NC 4.0)
- osu! is a game by ppy Pty Ltd. All osu! names, skins and sounds belong to their respective owners.

<div align="center">
<sub>Made by AmarilloNL · offline, for the love of stable</sub>
</div>
