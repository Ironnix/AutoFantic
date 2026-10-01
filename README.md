# <img src="assets/logo.svg" width="40" height="40" align="top" alt=""> AuFantic

**Self-learning fan control for Windows PCs.** AuFantic measures what each fan really does in *your* PC while you game, then sets every fan as quietly as possible for the temperatures you want.

AuFantic was called AutoFantic up to version 0.1.9. The program file (`AutoFantic.exe`), its data folder and this repository keep the old name, so updates and your data carry on as before.

> [!WARNING]
> **Early development.** AuFantic is a work in progress and has so far been tested on **one PC** only. There is no installer (unpack the zip and run it), some features are missing, and things can change or break at any time. It controls your fans directly: **use it at your own risk.** The safety features below are there to protect your hardware, but they are no guarantee.

> [!NOTE]
> **AI disclaimer.** Most of the code, tests and documentation in this project were written with the help of AI (Claude by Anthropic, via Claude Code) and then tested and reviewed by the maintainer. The code can still contain mistakes, including ones a human would not make. Please read the code before you trust it with your hardware, and report problems as issues.

## What it can do today

* **Sets itself up in the window:** it checks that it can reach your fans (fan chip, PawnIO driver, graphics card, other fan programs), finds which fan outputs have a fan, and runs the first calibration. Until then the BIOS keeps your fans.
* **Finds your fans:** which fan outputs have a fan, how fast they turn, and which ones can stop. Empty outputs are ignored.
* **Calibrates while you play:** start a calibration, play your game (or use the built-in load), and after about 15 minutes AuFantic knows how much each fan cools the CPU and the GPU. Every calibration adds to the ones before, so a GPU-heavy game and a CPU render complete each other.
* **Picks the quietest fan speeds** for a preset for the whole PC: *Silent*, *Balanced*, *Cool* or *Max cooling*. Case fans follow whichever of CPU and GPU is warmer; curves never jump steeply.
* **Explains itself:** for each load level, the part that limits the cooling and why each fan runs as it does, plus every measurement run with its bottleneck.
* **Monitor:** charts of temperatures, power and every fan's speed and RPM, from the last 10 minutes to the last year, the lowest / average / highest values, and warnings you set yourself (e.g. "GPU hotspot above 90 °C for 10 s", or a fan that stands still although it should turn).
* **Runs in the background:** an icon next to the clock and a window with live temperatures and fan speeds, fan curves you can drag, the calibration, what it did (*Activity*) and settings. It can start with Windows. Every second it reads only the sensors it needs.
* **A fallback without AuFantic:** each fan curve as the 4 points a BIOS fan curve takes (or, for the graphics card, as an MSI Afterburner curve), ready to copy.
* **Switches fans off** when the PC is idle and cool, for every fan that can stop (you choose which).
* **Extra quiet** when you're away (after a few minutes without keyboard or mouse) and/or at night (a time you set): every fan at its slowest, and off where it can stop, the graphics card's too, as long as it stays cool.
* **Reports:** every game session (a program that kept the PC busy for a few minutes) with its temperatures, fan speeds and power, and a summary per game.
* **Cooling health:** compares every steady minute with what the calibration expects, tells a warmer room apart from dust or old thermal paste, and says when it's time to clean. In numbers ("CPU +1.2 °C at full load, the cooling works 2 % worse than 4 weeks ago") and as a chart over up to a year.
* **Worn fans:** how fast each fan turns at the same setting, against its first week. A worn bearing or dirt makes a fan slower; AuFantic says so next to the clock.
* **English or German:** *Settings → Language* (like Windows, English or Deutsch).
* **Light or dark:** like Windows, or always light or always dark (*Settings → Appearance*). The charts have their own colours for dark mode, so every line stays easy to tell apart.
* **Updates itself:** *Settings → Updates → Update to …* downloads the new version from GitHub, checks it against GitHub's checksum and restarts into it; your data stays. It looks for a new version at every start and once a day (you can switch that off) and says so next to the clock.
* **Stays safe:** hard temperature limits, a check for broken sensors, every fan goes back to BIOS control when AuFantic pauses, sleeps or exits, and a **watchdog** hands the fans back even if AuFantic is killed.

## Requirements

* Windows 10 or 11 (64-bit), with admin rights (the fan chip driver needs them).
* A mainboard whose fan chip [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) supports, and the **PawnIO** driver for it. AuFantic checks both when it starts, and installs the driver for you if you say yes.
* An NVIDIA or AMD graphics card, if you want its fans controlled too.
* Tested on: AMD Ryzen 7 9800X3D (air cooler), NVIDIA RTX 3080, MSI mainboard with a Nuvoton NCT6686D fan chip.

## Getting started

Download the zip from [Releases](https://github.com/Ironnix/AutoFantic/releases), unpack it anywhere and run `AutoFantic.exe`, or build it yourself (see *Build from source*). What changed in each version is in [CHANGELOG.md](CHANGELOG.md). Then:

1. **Start `AutoFantic.exe`** (or, from the source, double-click `Start-Dev-Build.cmd`) and allow the admin prompt. The first time, the window opens on **Set up**.
2. **Before you start:** the checks show whether AuFantic can reach the mainboard and graphics card fans and whether another fan program is running. Fix what's marked, then *Check again*.
3. **Step 1 · Find my fans** (about 2 minutes, best at idle), then **Step 2 · Calibrate** with your game or the built-in load (about 15 minutes). When it's done, AuFantic controls your fans.
4. When you close the window, AuFantic keeps running as an icon next to the clock (right-click it for *Pause*, *Start with Windows* and *Exit*). Calibrate again from the window any time, for example with another game.
5. **New versions:** *Settings → Updates*. One click downloads and installs it (0.1.0 can't update itself: download a newer version by hand once).

| Page | What you find there |
|----|----|
| **Overview** | status and *Pause*, CPU and GPU temperature, every fan's live speed, **extra quiet** (away / at night), and the preset: *Silent* (CPU ≤ 87 °C, GPU ≤ 82 °C), *Balanced* (≤ 80 °C), *Cool* (≤ 70 °C) or *Max cooling* (every fan only as fast as it still clearly helps). Anything the start-up checks found that needs a look is shown on top |
| **Monitor** | charts of temperatures, power, fan speed and RPM (10 min to 1 year; hover for the values), a table with now / lowest / average / highest, and your **warnings** |
| **Fan curves** | one curve per fan: drag a point, double-click to add one, right-click to remove one. The recommended curve stays faint in the background. *Switch off when the PC is idle and cool* (drawing a point down to 0 % does the same). *Let the BIOS control these fans*: hands just this fan back to the BIOS, the others stay with AuFantic. *Reset to recommended*. Below: the same curve for the BIOS (or Afterburner), to copy |
| **Reports** | every game session and a summary per game: time played, GPU and CPU temperature, hotspot, fan speed, power |
| **Cooling health** | whether CPU and GPU run warmer than after the calibration at the same load (dust, paste): the last 7 days against a week, 4 weeks, 3 months and a year ago, in °C and in % (how much worse the cooling works), and a chart per day or per week over 30 days, 3 months or a year, with the room. **Fans:** each fan's speed at the same setting against its first week (worn bearing, dirt), *Start again* after cleaning |
| **Calibration** (before the first one: **Set up**) | start a calibration with your game or the built-in load, with a live chart. Then *Why these settings*, *Your fans* (how many fans per output and how loud they are, since quiet fans are raised first), *What each fan cools* and *Runs* |
| **Activity** | what AuFantic did, newest first: safety stops, sensor problems, fans switching off and on, calibrations, pauses, and what the watchdog did after a crash |
| **Settings** | **Updates** (check now, update in one click, check by itself), *Start with Windows* (with a hint if it starts another copy of AuFantic), **Appearance** (like Windows, light or dark) and **Language** (English / Deutsch), the data folder, and a **Developer** section (AuFantic's own memory and CPU, find the fans again, all sensors, the test console) |

## How it works (short)

1. AuFantic reads the sensors and drives the fan outputs through [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), built into the program. No other fan tool is needed.
2. A calibration tries 9 fan combinations while the PC is under load. For each one it measures how the temperatures follow the power, second by second. That gives "°C per watt" even when the game's load jumps around.
3. From all runs it builds a model of your PC: which fan cools what, and how loud each one is.
4. For the chosen preset it finds the quietest fan speeds for every load level, from idle to heavier than anything measured, and turns them into one curve per fan. GPU fans follow the GPU, the CPU cooler the CPU, and fans that clearly cool both (case fans) follow whichever is warmer. No curve rises faster than 5 % per °C (except the last ramp to 100 % just above the target), so a fan doesn't jump between speeds when the temperature wobbles.
5. In daily use the fans follow those curves. When nothing is going on (low load, cool) it reads every 2 seconds instead of every second. Temperatures are smoothed over a few seconds, fans speed up fast (+4 %/s) and slow down gently (−1 %/s) so the noise doesn't jump. That's why a fan can sit a little above its curve for a moment after a load spike.

## Safety

* Limits: CPU 90 °C, GPU 85 °C, GPU hotspot 100 °C, GPU memory 100 °C. When one is crossed, **every fan runs at 100 %** until everything has been at least 5 °C below its limit for 10 seconds.
* A temperature sensor that stops reporting or reads nonsense hands the fans back to the BIOS, since a lost sensor must never look like "all fine".
* A fan that is switched off starts again as soon as its part gets warm (5 °C above where it stopped), whenever anything goes above 65 °C, or when load comes.
* Pause, sleep, exit and errors all hand the fans back to the BIOS.
* A fan you gave to the BIOS (*Fan curves → Let the BIOS control these fans*) is left alone completely, also at a limit: then the BIOS's own curve applies to it.
* **Watchdog:** while AuFantic drives a fan, it keeps what the BIOS had set in a small file (`fans-in-use-<process id>.json`: the fan chip's own memory of its BIOS setup). A tiny second process waits for AuFantic to end; if it ended without handing the fans back (a crash, Task Manager → End task), the watchdog does it with that file. If the watchdog didn't run either, AuFantic does it at its next start. A PC restart always restores the BIOS setup too.

## Where your data is stored

Everything is in `%LocalAppData%\AutoFantic` as plain files (not uploaded): `fans.json` (your fans), `measurements.json` (every calibration run), `fans-off.json`, `calibration.json` (the current result), `curves.json` (your own curves), `activity.log` (what AuFantic did), `history.db` (SQLite: the monitor's history at 5-second values for 2 days, minute values for 30 days and hourly values for a year, plus the game sessions and the daily cooling health, kept for good; about 65 MB at most, of which the year of hourly values is about 7 MB), `warnings.json` (your warnings), `quiet.json` (extra quiet), `updates.json` (check by itself or not), `appearance.json` (light or dark, language), `fan-wear.json` (when a fan's first week starts, after cleaning or replacing it), and a readable report, `calibration.html` and `calibration-*.txt`. Earlier versions kept it in `runs\` next to the program; the first start copies it over once and leaves the old folder as it was. To use another folder, set the environment variable `AUTOFANTIC_DATA`.

## Privacy

**Nothing is uploaded.** Your data stays in the folder above. The only connection AuFantic makes is the **update check**: at every start and once a day it asks GitHub's public API for the list of AuFantic releases. GitHub sees your IP address and "AutoFantic/<version>", nothing about your PC, fans or measurements. Switch it off in *Settings → Updates → Check by itself*. A new version is downloaded only when you click *Update*, and the PawnIO driver's installer (from PawnIO's own releases on GitHub) only when you say yes to *Install PawnIO*.

## Uninstall

1. Right-click the icon next to the clock → *Exit* (the fans go back to the BIOS).
2. If *Start with Windows* is on: switch it off first (*Settings*), or delete the task "AutoFantic" in the Task Scheduler.
3. Delete AuFantic's folder (where you unpacked it) and its data, `%LocalAppData%\AutoFantic`.

AuFantic installs no service and has no driver of its own. The PawnIO driver is a separate program, whether AuFantic installed it for you or you did: uninstall it in *Apps* if nothing else needs it.

## Roadmap

| | |
|----|----|
| ✅ Done | GitHub releases and updates in the app, dark mode, find fans, calibration while gaming, combining calibrations, presets, quietest mix, fans off at idle, window + tray icon, set-up in the window, a watchdog after crashes, an activity log, case fans that follow CPU and GPU, smoother curves, a monitor with history and warnings, BIOS fallback curves, extra quiet (away / night), game reports, cooling health, start with Windows, sleep handling |
| 💡 Later | learning in the background during normal gaming, reacting to power before the temperature rises, a quieter GPU idle |

## Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build
dotnet test
dotnet publish src/AutoFantic.App -c Release -o publish     # publish\AutoFantic.exe, the app
dotnet publish src/AutoFantic.Spike -c Release -o publish   # publish\autofantic-spike.exe, the test console
```

Both are self-contained single exe files, so the target PC needs no .NET installed.

**Texts and languages:** every text the user sees goes through `T(...)` (`src/AutoFantic.Core/Texts.cs`): the English text is the key, the German one is in `src/*/Lang/*.json`. A text without a translation stays English. To find them: set `AUTOFANTIC_MISSING_TEXTS` to a file, run the app in German (e.g. `--simulate --screenshot … --page …`), and it lists every text it showed without a translation. A test checks that each German text has the same placeholders as its English one.

**Trying a change before pushing it:** exit AuFantic, then double-click `Start-Dev-Build.cmd`. It builds the source as it is now into `dev\` and starts it (Settings shows its version with *-dev*, e.g. 0.2.0-dev), with the same data as the installed AuFantic. Start the installed one again afterwards.

```
src/AutoFantic.Core    hardware access, simulated PC, calibration, thermal model, fan control, safety, hand-back after a crash, activity log, monitor history and warnings, reports
src/AutoFantic.App     the window, the tray icon and the watchdog (WPF)
src/AutoFantic.Spike   the test console (developer tool)
tests/                 unit tests for everything that doesn't need real hardware
```

**Code signing:** releases are signed through [SignPath](https://signpath.org) (free for open source) once it's set up; see [CODE_SIGNING.md](CODE_SIGNING.md). Until then they are unsigned.

**Making a release:** give the version its section in `CHANGELOG.md` (`## [0.2.0] - date`), then push a tag `v0.2.0`. GitHub Actions (`.github/workflows/release.yml`) runs the tests, builds both exes and makes a **draft** pre-release with that section as its text; it's public only once you publish the draft on GitHub. From then on the app's update check finds it (drafts are invisible to it), so **publish** each release, and give it a plain version tag: `v0.2.0-test` is ignored.

The app can check itself without touching real fans: `AutoFantic.exe --simulate --selftest` (runs 5 s), `--simulate --selftest-calibration --sim-speed 60` (a whole first calibration), `--simulate --selftest-update <url of a releases.json>` (a whole update from a local copy of GitHub's list: check, download, install, restart), `--simulate --screenshot page.png --page overview|monitor|curves|reports|health|calibration|log|settings [--full]` (`--full` = the whole page, also what is scrolled out of view).

<details>
<summary>Test console commands (developer tool)</summary>

Run in a terminal **as administrator**: `publish\autofantic-spike.exe <command>`, or `test` for a menu. Add `--simulate` to any command to use a built-in simulated PC instead (no admin, no real fans touched). `--sim-speed 20` runs it 20× faster.

| Command | What it does |
|----|----|
| `test` | menu: find my fans, calibrate, check the results, use the curves, developer tests (including the crash test) |
| `list --out hardware.txt` | every sensor and fan output |
| `watch [--csv log.csv]` | one status line per second, read-only |
| `calibrate [--ambient 22] [--profile balanced] [--builtin-load]` | the calibration |
| `recalculate [--profile silent]` | works the curves out again from the stored measurements |
| `run` | fan control by the curves until Ctrl+C |
| `load [--seconds 30]` | only the built-in CPU + GPU load (no admin, no fans touched) |
| `discover` | runs each fan output through 100 / 60 / 30 % to find the fans |
| `set <ch> <percent> [--seconds 60]` | holds one fan at a fixed speed, then hands it back |
| `sweep <ch,ch>` | measures one fan group step by step under steady load |
| `analyze <log.csv>` | replays a `watch` log: how often could AuFantic have learned |
| `restore` | emergency: hands back what a crashed run left behind (from its fans-in-use file), then every fan back to BIOS control (a restart always works) |

</details>

## Troubleshooting

* **"needs admin rights":** start it with "Run as administrator" (`Start-Dev-Build.cmd` and the exe ask by themselves).
* **No mainboard fans found, or the CPU shows 0 °C** (the set-up check says so): both are read through the **PawnIO** driver. AuFantic asks whether it should install it (or click *Install PawnIO* next to the check): it downloads PawnIO's official installer, checks it, installs it and starts again. If that doesn't work, install it yourself from [pawnio.eu](https://pawnio.eu) and start AuFantic again. If your fan chip still doesn't show up, it isn't supported yet: *Settings → Developer → Show all sensors* writes a list you can attach to a GitHub issue.
* **It starts an old version after a restart:** there is a second copy of AuFantic in another folder, and *Start with Windows* still starts that one. Exit it and start the copy you want: from 0.1.9 on, *Start with Windows* follows the copy you start.
* **Fans jump around or ignore the speed:** another fan tool (the mainboard's own app, Fan Control, Argus Monitor …) is still controlling them. The check on the Overview names the ones it knows. Close it or switch its fan control off.
* **GPU fans never switch off at idle:** check the GPU's idle power on the Overview. Many cards draw 100 W or more at "idle" with several or high-refresh monitors, and then they get too warm without their fans.
* **Something happened and you don't know why:** the *Activity* page (and `activity.log` in the data folder) lists every safety stop, sensor problem and fan switching off or on.

## License

AuFantic is licensed under the **MIT License**, see [LICENSE](LICENSE).

It uses [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0), [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT) and [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) with SQLite (MIT / public domain), which keep their own licenses.
