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
| 0 | Test tool: read sensors, drive fan headers, hand them back to the BIOS | ✅ code ready, hardware test pending |
| 1 | Background service, logging, load detection | planned |
| 2 | Setup wizard: find fans, what they cool, pump detection | planned |
| 3 | Learning: experiments during steady load, thermal model | planned |
| 4 | Profiles (Max 80 / Max 90 / Max Cooling) and live control | planned |

## Layout

```
src/AutoFanatic.Core     hardware layer + analysis (steady state, thermal resistance, safety limits)
src/AutoFanatic.Spike    Phase 0 command-line tool: list / watch / set
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

In a terminal **run as administrator** (the hardware driver needs it):

1. Turn **off fan control in any other fan tool** (Argus Monitor, Fan Control, Armoury Crate, iCUE, …). Monitoring may stay. Two programs controlling the same headers fight each other.
2. `autofanatic-spike list --out hardware.txt` lists every sensor and controllable fan. Check that:
   * the mainboard shows a Super I/O chip (e.g. `Nuvoton NCT6799D`) with Fan and Control sensors
   * "Key sensors" found CPU temperature/power and GPU core/hotspot/memory/power
   * "Fan controls" lists the mainboard headers and the GPU fans
3. `autofanatic-spike watch --csv idle.csv` prints one status line per second, read-only.
4. `autofanatic-spike set 0 60 --seconds 20` holds fan control #0 at 60 % for 20 s, then hands it back to the BIOS and shows which fan reacted. Repeat for each control.
5. Run `watch` while gaming: after a minute of steady load, "settled" should switch to `yes`.

`hardware.txt` and CSV logs are git-ignored on purpose: they describe your machine, so keep them out of the repo.

## Safety

* Every fan the tool changes goes back to BIOS/driver control when the command ends, on Ctrl+C, on errors and when the process exits.
* `set` refuses to start, and stops early, at CPU ≥ 90 °C, GPU core ≥ 85 °C, GPU hotspot ≥ 100 °C or GPU memory ≥ 100 °C.
* `set` below 25 % needs `--force`, because a pump or a fan that stalls could sit on that header.
* A hard kill (Task Manager → End task) skips the hand-back. The fan then keeps its last speed until the tool runs again or the PC restarts. The later service gets a separate watchdog for this.

## Troubleshooting

* **"needs admin rights"**: start the terminal with "Run as administrator".
* **No Super I/O chip / no mainboard fan controls**: current LibreHardwareMonitor versions use the **PawnIO** driver. Install it from [pawnio.eu](https://pawnio.eu), or run the LibreHardwareMonitor app once, which offers to install it. If the chip still doesn't show up, it isn't supported yet.
* **Fans won't take the speed or jump around**: another fan tool is still controlling them.

## License

Not decided yet. LibreHardwareMonitorLib, which this project uses, is licensed under MPL-2.0.
