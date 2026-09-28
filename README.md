# AutoFantic

**Self-learning fan control for Windows PCs.** AutoFantic measures what each fan really does in *your* PC while you game, then sets every fan as quietly as possible for the temperatures you want.

> [!WARNING]
> **Early development.** AutoFantic is a work in progress and has so far been tested on **one PC** only. There is no installer or release yet, some features are missing, and things can change or break at any time. It controls your fans directly: **use it at your own risk.** The safety features below are there to protect your hardware, but they are no guarantee.

> [!NOTE]
> **AI disclaimer.** Most of the code, tests and documentation in this project were written with the help of AI (Claude by Anthropic, via Claude Code) and then tested and reviewed by the maintainer. The code can still contain mistakes, including ones a human would not make. Please read the code before you trust it with your hardware, and report problems as issues.

## What it can do today

* **Finds your fans:** which fan outputs have a fan, how fast they turn, and which ones can stop. Empty outputs are ignored.
* **Calibrates while you play:** start a calibration, play your game (or use the built-in load), and after about 15 minutes AutoFantic knows how much each fan cools the CPU and the GPU. Every calibration adds to the ones before, so a GPU-heavy game and a CPU render complete each other.
* **Picks the quietest fan speeds** for a preset for the whole PC: *Silent*, *Balanced*, *Cool* or *Max cooling*.
* **Explains itself:** for each load level, the part that limits the cooling and why each fan runs as it does, plus every measurement run with its bottleneck.
* **Runs in the background:** an icon next to the clock and a window with live temperatures and fan speeds, fan curves you can drag, the calibration and settings. It can start with Windows.
* **Switches fans off** when the PC is idle and cool, for every fan that can stop (you choose which).
* **Stays safe:** hard temperature limits, a check for broken sensors, and every fan goes back to BIOS control when AutoFantic pauses, sleeps or exits.

## Requirements

* Windows 10 or 11 (64-bit), with admin rights (the fan chip driver needs them).
* A mainboard whose fan chip [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) supports, and its **PawnIO** driver (see Troubleshooting).
* An NVIDIA or AMD graphics card, if you want its fans controlled too.
* Tested on: AMD Ryzen 7 9800X3D (air cooler), NVIDIA RTX 3080, MSI mainboard with a Nuvoton NCT6686D fan chip.

## Getting started

There is no download yet, so build it first (see *Build from source*). Then:

1. **The first calibration** (only once, for now in the test console): open a terminal **as administrator**, run `publish\autofantic-spike.exe test`, and choose *1 Find my fans*, then *2 Calibrate*.
2. **Double-click `Start-AutoFantic.cmd`** and allow the admin prompt. The AutoFantic window opens. When you close it, AutoFantic keeps running as an icon next to the clock (right-click it for *Pause*, *Start with Windows* and *Exit*).
3. From then on, calibrate again from the window at any time (**Calibration** page), for example with another game.

| Page | What you find there |
|----|----|
| **Overview** | status and *Pause*, CPU and GPU temperature, every fan's live speed, and the preset: *Silent* (CPU ≤ 87 °C, GPU ≤ 82 °C), *Balanced* (≤ 80 °C), *Cool* (≤ 70 °C) or *Max cooling* (every fan only as fast as it still clearly helps) |
| **Fan curves** | one curve per fan: drag a point, double-click to add one, right-click to remove one. The recommended curve stays faint in the background. *Switch off when the PC is idle and cool* (drawing a point down to 0 % does the same). *Reset to recommended* |
| **Calibration** | start a calibration with your game or the built-in load, with a live chart. Then *Why these settings*, *Your fans* (how many fans per output and how loud they are, since quiet fans are raised first), *What each fan cools* and *Runs* |
| **Settings** | *Start with Windows*, the data folder, and a **Developer** section (find the fans again, all sensors, the test console) |

## How it works (short)

1. AutoFantic reads the sensors and drives the fan outputs through [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), built into the program. No other fan tool is needed.
2. A calibration tries 9 fan combinations while the PC is under load. For each one it measures how the temperatures follow the power, second by second. That gives "°C per watt" even when the game's load jumps around.
3. From all runs it builds a model of your PC: which fan cools what, and how loud each one is.
4. For the chosen preset it finds the quietest fan speeds for every load level, from idle to heavier than anything measured, and turns them into one curve per fan.
5. In daily use the fans follow those curves. Temperatures are smoothed over a few seconds, fans speed up fast (+4 %/s) and slow down gently (−1 %/s) so the noise doesn't jump. That's why a fan can sit a little above its curve for a moment after a load spike.

## Safety

* Limits: CPU 90 °C, GPU 85 °C, GPU hotspot 100 °C, GPU memory 100 °C. When one is crossed, **every fan runs at 100 %** until everything has been at least 5 °C below its limit for 10 seconds.
* A temperature sensor that stops reporting or reads nonsense hands the fans back to the BIOS, since a lost sensor must never look like "all fine".
* A fan that is switched off starts again as soon as its part gets warm (5 °C above where it stopped), whenever anything goes above 65 °C, or when load comes.
* Pause, sleep, exit and errors all hand the fans back to the BIOS.
* **Known gap:** if AutoFantic is killed hard (Task Manager → End task), the mainboard fans keep their last speed until the next restart. A watchdog for this is planned.

## Where your data is stored

Everything is in the `runs\` folder as plain JSON (not uploaded, and git-ignored): `fans.json` (your fans), `measurements.json` (every calibration run), `fans-off.json`, `calibration.json` (the current result), `curves.json` (your own curves), and a readable report, `calibration.html` and `calibration-*.txt`.

## Roadmap

| | |
|----|----|
| ✅ Done | find fans, calibration while gaming, combining calibrations, presets, quietest mix, fans off at idle, window + tray icon, start with Windows, sleep handling, reports |
| 🔜 Next | first calibration from the window (no console), a watchdog after crashes, one downloadable exe, a log of what happened |
| 💡 Later | setup wizard, learning in the background during normal gaming, case fans that follow both CPU and GPU, a quieter GPU idle |

## Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build
dotnet test
dotnet publish src/AutoFantic.App -c Release -o publish     # publish\AutoFantic.exe, the app
dotnet publish src/AutoFantic.Spike -c Release -o publish   # publish\autofantic-spike.exe, the test console
```

Both are self-contained single exe files, so the target PC needs no .NET installed.

```
src/AutoFantic.Core    hardware access, simulated PC, calibration, thermal model, fan control, safety, reports
src/AutoFantic.App     the window and the tray icon (WPF)
src/AutoFantic.Spike   the test console (developer tool)
tests/                 unit tests for everything that doesn't need real hardware
```

<details>
<summary>Test console commands (developer tool)</summary>

Run in a terminal **as administrator**: `publish\autofantic-spike.exe <command>`, or `test` for a menu. Add `--simulate` to any command to use a built-in simulated PC instead (no admin, no real fans touched). `--sim-speed 20` runs it 20× faster.

| Command | What it does |
|----|----|
| `test` | menu: find my fans, calibrate, check the results, use the curves, developer tests |
| `list --out hardware.txt` | every sensor and fan output |
| `watch [--csv log.csv]` | one status line per second, read-only |
| `calibrate [--ambient 22] [--profile balanced] [--builtin-load]` | the calibration |
| `recalculate [--profile silent]` | works the curves out again from the stored measurements |
| `run` | fan control by the curves until Ctrl+C |
| `load [--seconds 30]` | only the built-in CPU + GPU load (no admin, no fans touched) |
| `discover` | runs each fan output through 100 / 60 / 30 % to find the fans |
| `set <ch> <percent> [--seconds 60]` | holds one fan at a fixed speed, then hands it back |
| `sweep <ch,ch>` | measures one fan group step by step under steady load |
| `analyze <log.csv>` | replays a `watch` log: how often could AutoFantic have learned |
| `restore` | emergency: every fan back to BIOS control (a restart always works) |

</details>

## Troubleshooting

* **"needs admin rights":** start it with "Run as administrator" (`Start-AutoFantic.cmd` asks by itself).
* **No mainboard fans found:** current LibreHardwareMonitor versions use the **PawnIO** driver. Install it from [pawnio.eu](https://pawnio.eu), or run the LibreHardwareMonitor app once (it offers to install it). If your fan chip still doesn't show up, it isn't supported yet.
* **Fans jump around or ignore the speed:** another fan tool (the mainboard's own app, Fan Control, Argus Monitor …) is still controlling them. Close it.
* **GPU fans never switch off at idle:** check the GPU's idle power on the Overview. Many cards draw 100 W or more at "idle" with several or high-refresh monitors, and then they get too warm without their fans.

## License

AutoFantic is licensed under the **MIT License**, see [LICENSE](LICENSE).

It uses [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0) and [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT), which keep their own licenses.
