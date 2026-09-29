# Changelog

## [0.1.8] - 29.09.2026

### New

- **German interface:** *Settings → Language*: like Windows, English or Deutsch. AutoFantic starts again to switch.
- **Worn fans:** *Cooling health → Fans* compares how fast each fan turns at the same setting with its first week. A worn bearing or dirt makes a fan slower; from 8 % slower AutoFantic says so next to the clock. *Start again* after cleaning or replacing a fan.
- *Settings → Developer* shows how much memory and CPU AutoFantic itself uses, now and with the window closed.
- **Code signing prepared** (SignPath, free for open source). Once AutoFantic is signed, updates must be signed by the same publisher or they aren't installed.

## [0.1.7] - 30.09.2026

### New

- **Updates in the app:** *Settings → Updates → Update to …* downloads the new version from GitHub, checks it against GitHub's checksum, and restarts AutoFantic into it (the BIOS has the fans for those few seconds). Your data stays. If anything goes wrong, the old version keeps running and nothing is changed.
- **Checks by itself** at every start and once a day, and says so next to the clock. You can switch that off (*Check by itself*). It's the only connection AutoFantic makes: one request to GitHub's public list of releases, nothing about your PC is sent.
- **Dark mode:** *Settings → Appearance*: like Windows (as before), always light or always dark. It switches right away. The charts use their own colours in dark mode, so no line is too dim on the dark background.
- The temperature chart's **GPU hotspot** and **GPU memory** lines are now green and yellow: the old red and violet were too easy to mix up with GPU (orange) and CPU (blue).
- **Monitor over a year:** *3 months* and *1 year* next to the other time ranges (the hourly values were already kept for 400 days).
- **Cooling health in numbers:** the last 7 days against a week, 4 weeks, 3 months and a year ago, and against the first week after the calibration: °C warmer at full load and how much worse the cooling works in % (e.g. "+1.2 °C (+2.2 %)").
- **Cooling health over a year:** the chart has *30 days*, *3 months* (per week) and *1 year*. Each day counts against the first week after its calibration, so a new calibration after cleaning starts again at 0.
- **One fan back to the BIOS:** *Fan curves → Let the BIOS control these fans* hands just that fan (group) back to the BIOS; the others stay with AutoFantic. Its curve is kept for when you switch back, and the Overview shows what the BIOS runs it at ("BIOS 45 %").
- The Monitor names AutoFantic's temperature limits and what the preset keeps the GPU and hotspot at. The values under the mouse are in neat columns.
- *Start with Windows* says when it starts another copy of AutoFantic (for example an older one from before you unpacked it somewhere else).

### Good to know

- 0.1.0 can't update itself: download this version by hand once, unpack it over the old one (or anywhere), and switch *Start with Windows* off and on again if it points to the old copy. Every later version is one click.

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
