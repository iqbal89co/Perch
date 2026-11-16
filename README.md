<p align="center"><img src="src/Perch/Assets/perch.png" width="96" alt="Perch logo: a white bird sitting on a dark island bar"></p>

<h1 align="center">Perch</h1>
<p align="center"><b>A living island for the top of your screen.</b><br>
Windows 10 and 11 · native .NET · no account · nothing leaves your PC except lyrics lookups</p>

Perch sits at the top centre of your screen as a small pill. While it rests it shows the time, or whatever
matters right now: the song that's playing, your focus timer, the volume as you change it, the charger
going in. Point at it and it springs open into a panel with tabs.

## What it does

| Tab | What you get |
| --- | --- |
| **Music** | Artwork, title and artist for whatever plays in Windows' media controls (Spotify, browsers, Media Player…), play/pause/skip, a seek bar, and time-synced lyrics from LRCLIB. |
| **Today** | The date, how much of the day is gone, and a month calendar you can page through. |
| **Shelf** | Drag files onto the island to keep them here; drag them out again into any app. Text drops become notes. |
| **Tasks** | A small to-do list. Enter to add, click to tick, double-click to edit. |
| **Focus** | A Pomodoro timer with short and long breaks. It keeps counting on the resting island. |
| **AI** | Three rows from Claude Code's own logs on this PC: **Usage 5h** (the current 5-hour window), **Usage Weekly** (the last 7 days) and **Usage Fable** (the last 7 days of Fable models). Optional budget per row for a bar, and "tell me before I run out" notices. |
| **Prayer** | Today's prayer times for your city, computed on your PC (Kemenag, MWL, ISNA, Umm al-Qura and more), the Hijri date, a countdown on the island before each prayer, a gentle chime and the qibla. |
| **System** | CPU, memory, network, disks, battery and the busiest apps, live while the tab is open. |

**On the resting island:** the clock, now playing with a little equaliser, the focus countdown, a volume
bar when you press the volume keys, and charging / low-battery notices.

**Looks:** four themes (Midnight, Daylight, Smoke, Aurora), six accent colours, and two shapes: *Docked*
(hangs from the top edge with soft shoulders) or *Floating* (a capsule just below the edge). Motion is a
real spring, stepped with the display's frame clock.

**Manners:** hides while a full-screen game, video or presentation is in front; stays out of Alt+Tab and
the taskbar; the empty space around the island is click-through; drawing stops when nothing moves.

## Install

Run `Perch-Setup-1.0.0.exe`. It installs for your user only (no admin prompt) into
`%LOCALAPPDATA%\Programs\Perch`, adds a Start menu entry and, if you leave the box ticked, starts Perch when
you sign in. The app is self-contained: no .NET download needed.

Right-click the tray icon for Settings, Start with Windows and Quit. Uninstall from *Settings › Apps*;
your settings in `%APPDATA%\Perch` are kept for a reinstall (delete the folder to remove them).

## Privacy

Tasks, the Shelf, focus history and settings are plain files in `%APPDATA%\Perch`. There are no accounts,
ads or analytics. The only network request is the lyrics lookup to `lrclib.net` for the song that is
playing; switch *Lyrics* off in Settings › Music to stop it. Prayer times are calculated on your PC, and
the AI tab reads only token counts, model names and times from Claude Code's local logs: no sign-ins, no credentials,
nothing sent anywhere.

## Build from source

Requires the .NET 8 SDK and (for the installer) Inno Setup 6.

```bash
dotnet build src/Perch/Perch.csproj
dotnet test tests/Perch.Tests
powershell -ExecutionPolicy Bypass -File build/publish.ps1          # tests + self-contained exe + installer in dist/
powershell -ExecutionPolicy Bypass -File build/e2e.ps1 -Installer dist/Perch-Setup-1.0.0.exe
```

`docs/ARCHITECTURE.md` explains how pages and background features plug in.

## Licence

MIT. See `LICENSE.txt`.
