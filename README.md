# AutoFantic

**Self-learning fan control for Windows PCs.** Pick a temperature limit (e.g. *Max 80 °C*) and AutoFantic finds the **quietest fan settings** that hold it. It learns from your normal use, such as gaming, instead of hours of stress tests.

* Reads the sensors and drives the fan headers **directly**, with no other fan tool needed.
* Learns which fan cools what and how loud each one is, and raises the quiet, effective fans first.
* Profiles: **Max 80**, **Max 90**, **Max Cooling** (as cool as possible without pointless noise), **Custom**.
* Safe by design: hard temperature limits, and every fan goes back to BIOS control when the program stops.

> **Status: early prototype (Phase 0).** Only a command-line test tool exists so far. Use at your own risk.

## How it works (short)

1. Sensors and fan headers are accessed through [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), compiled into the program: mainboard Super I/O chip, CPU, and NVIDIA/AMD GPU.
2. During steady load (a game, a render) AutoFantic briefly changes one fan group, waits until the temperatures settle, and measures the effect.
3. Measurements are compared as **thermal resistance** (°C above ambient per watt), so data from different games and loads fit together.
4. From that it works out the quietest fan mix that keeps each component under the chosen limit.

## Roadmap

| Phase | Content | State |
|----|----|----|
| 0 | Test tool: read sensors, drive fan headers, find fans, knee sweep, simulated PC | ✅ tested on the real PC (sensors, fan control, discover) |
| 0.5 | Calibration while gaming (power-following fit), fans-off test, quietest mix per load level, curves, "Use my curves" controller | ✅ code ready, first real run pending |
| 1 | AutoFantic window (overview + presets, curve editor, calibration with explanations, settings) + tray icon, start with Windows, sleep handling; logging and a watchdog still to do | 🟡 in use, calibration from the window pending its first real run |
| 2 | Setup wizard: find fans, what they cool, pump detection | planned |
| 3 | Learning: experiments during steady load, thermal model | planned |
| 4 | Presets (Silent / Balanced / Cool / Max cooling) and live control | ✅ presets and curve control; power feed-forward still planned |

## Layout

```
src/AutoFantic.Core     hardware layer, simulated PC, sensor log, analysis (steady state, thermal resistance, knee, safety, load classes, learning opportunities)
src/AutoFantic.Spike    Phase 0 command-line tool: list / watch / analyze / discover / set / sweep / restore
tests/                   unit tests for everything that doesn't need real hardware
```

## Build

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build
dotnet test
dotnet publish src/AutoFantic.Spike -c Release -o publish
```

The last command produces `publish\autofantic-spike.exe`, a self-contained exe, so the target PC needs no .NET installed.

## Trying the Phase 0 tool

Everything runs in a terminal **as administrator** (the hardware driver needs it).

| Command | What it does |
|----|----|
| `list --out hardware.txt` | every sensor and controllable fan, plus the key sensors AutoFantic picked |
| `watch [--csv log.csv]` | one status line per second, read-only; the CSV also records the foreground program |
| `calibrate [--ambient 22] [--profile 80] [--builtin-load]` | **the calibration** (menu step 2): while you play (or with the built-in load), 9 fan combinations, then the quietest mix per load level and a curve per fan |
| `run` | **use the curves** (menu step 4): AutoFantic controls the fans until Ctrl+C |
| `load [--seconds 30]` | just the built-in CPU + GPU load, to check it works (no admin, no fans touched) |
| `analyze <log.csv>` | replays a `watch` log through the experiment rules from the design: how often could AutoFantic have learned during that session, and what blocked it (unstable load, too hot, idle). No admin needed |
| `discover` | runs each fan channel through 100 / 60 / 30 %: which control drives which fan, empty headers, 0-RPM fans, pumps. A channel that looks like a pump is never taken lower |
| `set <ch> <percent> [--seconds 60]` | holds one fan at a fixed speed, then hands it back |
| `sweep <ch,ch> [--ambient 22] [--csv sweep.csv]` | **the real measurement**: under steady load, steps a fan group 100 → 80 → 60 → 45 → 30 %, waits at each step until temperatures settle, and reports the **knee** |
| `restore` | emergency: every fan back to BIOS control (after a hard kill a restart is the reliable way, see Safety) |

Add `--simulate` to any command to run it against a built-in simulated PC: no admin rights, no real fans touched. `--sim-speed 20` runs it 20× faster (a full sweep in under a minute), `--sim-load idle|game|session` picks the load (`discover` defaults to idle, everything else to a steady game; `session` is a scripted hour of desktop, loading, play and menus, handy for `watch --csv` + `analyze`). Useful to see what the output looks like before trying it on real hardware.

### Start here

**Double-click `Start-AutoFantic.cmd`** and allow the Windows admin prompt (the fan chip needs it). The AutoFantic window opens; later it lives as a round icon next to the clock (double-click opens the window, right-click shows the fans, *Pause*, *Start with Windows*, *Exit*).

| Page | What it does |
|----|----|
| **Overview** | status with *Pause*, CPU and GPU temperature, every fan's live speed (*off* where it stands still), and the **preset for the whole PC**: *Silent* (CPU ≤ 87 °C, GPU ≤ 82 °C), *Balanced* (≤ 80 °C), *Cool* (≤ 70 °C) or *Max cooling* (every fan only as fast as it still clearly helps, about 1 °C per +10 %) |
| **Fan curves** | per fan its curve by temperature: drag the points, double-click to add one, right-click to remove one; the recommended curve stays faint behind it; switch stopping at idle on or off (only where the fans-off test found it safe); *Reset to recommended*. Hand-made curves are kept in `runs\curves.json` |
| **Calibration** | start a calibration **with your game** (it waits until the game runs) or with the **built-in load**; a live chart while it runs. Then *Why these settings* (per load level the part that limits the cooling and why each fan runs as it does), *Your fans* (how many fans per output and how loud: quiet fans are raised first), *What each fan cools*, and *Runs* (every run of every calibration with its **bottleneck**, the part closest to its limit) |
| **Settings** | *Start with Windows*, the calibration page and data folder, and **Developer**: find the fans again, all sensors, the test console |

How a calibration works: if the PC is idle, first a **fans-off test** (which fans stop at 0 %, how warm it gets without them). Then 9 fan combinations (a Taguchi L9 plan, so every group's effect can be separated); for each it fits how the temperatures **follow the power second by second**, which gives °C per watt even with a jumpy game load. **Every calibration adds to the last** (`runs\measurements.json`): a GPU-heavy game teaches the GPU side, a CPU render the CPU side, each run counting by the power of the part it heats. From all of it: the quietest fan speeds per load level for the preset, from idle to 30 % beyond the heaviest load seen, and a curve per fan that ends at 100 % just above the target. A run that gets too hot is retried a bit faster; a paused game repeats the run.

Fan control runs by those curves with smoothed temperatures, +4 %/s up, −1 %/s down, fans **off** at low load while CPU and GPU stay ≤ 55 °C (on again above 60 °C) and a short kick when a stopped fan starts. Before sleep the BIOS gets the fans, after waking AutoFantic takes them back.

**Where everything is stored** (`runs\`, plain JSON, git-ignored): `fans.json` (which outputs have fans, how they turn, what you said about them), `measurements.json` (every run of every calibration), `fans-off.json`, `calibration.json` (the current result), `curves.json` (your own curves), plus a readable report `calibration-*.txt` and the page `calibration.html`. `--simulate` runs use `runs\sim\`.

The **test console** (`publish\autofantic-spike.exe test`, or Settings → Developer) has the commands in the table above and the developer tests (record a gaming session, measure one fan group, crash test).

Still to come: a watchdog that restores the BIOS fan settings after a crash, logging to SQLite.

## Safety

* Every fan the tool changes goes back to BIOS/driver control when the command ends, on Ctrl+C, on errors and when the process exits.
* Every command that changes a fan refuses to start at CPU ≥ 90 °C, GPU core ≥ 85 °C, GPU hotspot ≥ 100 °C or GPU memory ≥ 100 °C. Crossing a limit while running sends **every fan to 100 %** until all temperatures have been at least 5 °C below their limits for 10 seconds; then the fans go back to the BIOS.
* It also stops when one of those temperature sensors stops reporting or reads nonsense (0 °C, > 150 °C) for 3 samples in a row: a lost sensor must never look like "all fine".
* Speeds below 25 % need `--force`, because a pump or a fan that stalls could sit on that header.
* A hard kill (Task Manager → End task) skips the hand-back, and the fan keeps its last speed. Try `autofantic-spike restore`, but on mainboard headers it may not work: the library only remembers the original BIOS setting within one run. **A restart always works**, because the BIOS sets the fan controller up again at boot. The later service gets a separate watchdog for this.

## Troubleshooting

* **"needs admin rights"**: start the terminal with "Run as administrator".
* **No Super I/O chip / no mainboard fan controls**: current LibreHardwareMonitor versions use the **PawnIO** driver. Install it from [pawnio.eu](https://pawnio.eu), or run the LibreHardwareMonitor app once, which offers to install it. If the chip still doesn't show up, it isn't supported yet.
* **Fans won't take the speed or jump around**: another fan tool is still controlling them.

## License

Not decided yet. LibreHardwareMonitorLib, which this project uses, is licensed under MPL-2.0.
