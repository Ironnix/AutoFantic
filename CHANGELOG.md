# Changelog



## [0.2.0] - not released yet




## [0.1.0] - 30.09.2026

The first version for others to try. **Early development:** tested on one PC so far (Ryzen 9800X3D, RTX 3080, Nuvoton NCT6686D). It controls your fans directly: use it at your own risk.

### What it does

- **Set-up in the window:** checks that it can reach your fans (fan chip and PawnIO driver, graphics card, other fan programs), finds which outputs have a fan, and runs the first calibration. Until then the BIOS keeps your fans.
- **Calibrates while you play** (about 15 minutes, or with a built-in load) and learns what each fan cools. Calibrations add up: a game teaches the GPU side, a render the CPU side.
- **Presets for the whole PC:** Silent, Balanced, Cool, Max cooling. The quietest fan speeds that hold them, one curve per fan, which you can drag and change.
- **Fans off** when the PC is idle and cool, for every fan that can stop.
- **Extra quiet** when you're away and/or at night: every fan at its slowest, and off where it can stop, as long as it stays cool.
- **Monitor:** charts of temperatures, power and fans from the last 10 minutes to the last 30 days, and warnings you set.
- **Reports:** every game session, with its temperatures, fan speeds and power, and a summary per game.
- **Cooling health:** tells a warmer room apart from dust or old thermal paste, and says when it's time to clean.
- **BIOS fallback:** each fan curve as the points a BIOS fan curve takes, to copy.
- **Safety:** temperature limits (every fan to 100 %), a check for broken sensors, the fans back to the BIOS on pause, sleep and exit, and a watchdog that hands them back if AutoFantic is killed.
- **Light:** about 25 MB of memory with the window closed; it reads only the sensors it needs.

### Good to know

- Windows 10 or 11, 64-bit, admin rights, and the [PawnIO](https://pawnio.eu) driver for the mainboard's fan chip.
- The exe isn't signed: Windows SmartScreen shows "Windows protected your PC". Choose *More info* → *Run anyway*, or build it yourself from the source.
- Your data is in `%LocalAppData%\AutoFantic`. Nothing is sent anywhere.
