# AutoFanatic

**Self-learning fan control for Windows PCs.** Pick a temperature limit (e.g. *Max 80 °C*) and AutoFanatic finds the **quietest fan settings** that hold it. It learns from your normal use, such as gaming, instead of hours of stress tests.

* Reads the sensors and drives the fan headers **directly**, with no other fan tool needed.
* Learns which fan cools what and how loud each one is, and raises the quiet, effective fans first.
* Profiles: **Max 80**, **Max 90**, **Max Cooling** (as cool as possible without pointless noise), **Custom**.
* Safe by design: hard temperature limits, and every fan goes back to BIOS control when the program stops.

> **Status: early prototype (Phase 0).** Only a command-line test tool exists so far. Use at your own risk.

## How it works (short)

1. Sensors and fan headers are accessed through [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), compiled into the program: mainboard Super I/O chip, CPU, and NVIDIA/AMD GPU.
2. During steady load (a game, a render) AutoFanatic briefly changes one fan group, waits until the temperatures settle, and measures the effect.
3. Measurements are compared as **thermal resistance** (°C above ambient per watt), so data from different games and loads fit together.
4. From that it works out the quietest fan mix that keeps each component under the chosen limit.

## Roadmap

| Phase | Content | State |
|----|----|----|
| 0 | Test tool: read sensors, drive fan headers, find fans (`discover`), measure the knee (`sweep`), simulated PC | ✅ code ready, hardware test pending |
| 1 | Background service, logging, load detection | planned |
| 2 | Setup wizard: find fans, what they cool, pump detection | planned |
| 3 | Learning: experiments during steady load, thermal model | planned |
| 4 | Profiles (Max 80 / Max 90 / Max Cooling) and live control | planned |

## Layout

```
src/AutoFanatic.Core     hardware layer, simulated PC, analysis (steady state, thermal resistance, knee, safety limits)
src/AutoFanatic.Spike    Phase 0 command-line tool: list / watch / discover / set / sweep / restore
tests/                   unit tests for everything that doesn't need real hardware
```

## Build

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build
dotnet test
dotnet publish src/AutoFanatic.Spike -c Release -o publish
```

The last command produces `publish\autofanatic-spike.exe`, a self-contained exe, so the target PC needs no .NET installed.

## Trying the Phase 0 tool

Everything runs in a terminal **as administrator** (the hardware driver needs it).

| Command | What it does |
|----|----|
| `list --out hardware.txt` | every sensor and controllable fan, plus the key sensors AutoFanatic picked |
| `watch [--csv log.csv]` | one status line per second, read-only; the CSV also records the foreground program |
| `discover` | runs each fan channel through 30 / 60 / 100 %: which control drives which fan, empty headers, 0-RPM fans, pumps |
| `set <ch> <percent> [--seconds 60]` | holds one fan at a fixed speed, then hands it back |
| `sweep <ch,ch> [--ambient 22] [--csv sweep.csv]` | **the real measurement**: under steady load, steps a fan group 100 → 80 → 60 → 45 → 30 %, waits at each step until temperatures settle, and reports the **knee** |
| `restore` | emergency: every fan back to BIOS control (after a hard kill a restart is the reliable way, see Safety) |

Add `--simulate` to any command to run it against a built-in simulated PC: no admin rights, no real fans touched (`--sim-speed 5` runs it faster). Useful to see what the output looks like before trying it on real hardware.

### First test evening (about 1–2 hours)

1. Turn **off fan control in any other fan tool** (Argus Monitor, Fan Control, Armoury Crate, iCUE, MSI Afterburner's fan curve, …). Monitoring may stay; the tool warns if it sees one running.
2. `autofanatic-spike list --out hardware.txt`. Check that the mainboard shows a Super I/O chip (e.g. `Nuvoton NCT6799D`) with Fan and Control sensors, that the key sensors were found, and that the fan controls include the mainboard headers and the GPU fans.
3. `autofanatic-spike discover --out discover.txt` at idle, about 1–2 minutes. Note which `#` is the CPU fan, the case fans, the GPU fan, and the pump (the pump is never swept).
4. Start a game and keep the load steady: a benchmark in a loop, or stand still in a demanding scene. Then in a second terminal, for example:
   * `autofanatic-spike sweep 3 --ambient 23 --csv sweep-gpu.csv` for the GPU fan
   * `autofanatic-spike sweep 1,2 --ambient 23 --csv sweep-case.csv` for all case fans together
   * `autofanatic-spike sweep 0 --ambient 23 --csv sweep-cpu.csv` for the CPU fan (a CPU-heavy game or a render is better here)

   Each sweep takes about 5–25 minutes. The result table shows temperatures and R (°C above room per watt) per step, and the knee for quiet / balanced / performance.

`hardware.txt`, `discover.txt` and CSV logs are git-ignored on purpose: they describe your machine, so keep them out of the repo.

## Safety

* Every fan the tool changes goes back to BIOS/driver control when the command ends, on Ctrl+C, on errors and when the process exits.
* Every command that changes a fan refuses to start, and stops early, at CPU ≥ 90 °C, GPU core ≥ 85 °C, GPU hotspot ≥ 100 °C or GPU memory ≥ 100 °C.
* Speeds below 25 % need `--force`, because a pump or a fan that stalls could sit on that header.
* A hard kill (Task Manager → End task) skips the hand-back, and the fan keeps its last speed. Try `autofanatic-spike restore`, but on mainboard headers it may not work: the library only remembers the original BIOS setting within one run. **A restart always works**, because the BIOS sets the fan controller up again at boot. The later service gets a separate watchdog for this.

## Troubleshooting

* **"needs admin rights"**: start the terminal with "Run as administrator".
* **No Super I/O chip / no mainboard fan controls**: current LibreHardwareMonitor versions use the **PawnIO** driver. Install it from [pawnio.eu](https://pawnio.eu), or run the LibreHardwareMonitor app once, which offers to install it. If the chip still doesn't show up, it isn't supported yet.
* **Fans won't take the speed or jump around**: another fan tool is still controlling them.

## License

Not decided yet. LibreHardwareMonitorLib, which this project uses, is licensed under MPL-2.0.
