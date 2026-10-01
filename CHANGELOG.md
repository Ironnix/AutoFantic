# Changelog

## [0.2.3] - 01.10.2026

### New

- **Fans on two outputs can run together** (*Calibration → Your fans*): a CPU cooler with two fans is often plugged into two headers (CPU Fan 1 and CPU Fan 2), and each got its own tab and its own curve. Under the list of your fans you can now pick the two and click *Run together*. From then on they are one group, like the fans of a graphics card: one tab under *Fan curves*, one curve, always the same speed, for example "CPU Fan 1 + 2 (#0, #1)". *Take apart* undoes it. Nothing has to be calibrated again: the curve is worked out from what was already measured for each of them, and the Monitor's charts, the cooling health and the worn fan detection carry on from the history of both. A curve you drew yourself for one of them is not used while they run together; it is back when you take them apart. It works for any mainboard fans, not for a graphics card's fans (those always stay with their card).
- **React early** (*Overview*, under *Extra quiet*; off until you switch it on): normally the fans follow the temperature, so they only speed up once the graphics card is already warm. With *Speed up as soon as the graphics card's power jumps* ticked, the card's fans and the case fans speed up the moment the card draws much more power (a game starts, a heavy scene), by about as much as the calibration says that power will warm it. The lead fades over a minute or two as the real temperature catches up. On the simulated PC the fans are up about 20 seconds earlier and the GPU is about 3 °C cooler one minute into a game; where it settles in the end is the same. The price: the fans change speed a little more often. The small ups and downs of a running game are ignored, a falling power never speeds anything up, the CPU's fans are left out (a CPU's power jumps all the time), and *Extra quiet* goes first. While a fan is ahead of its curve, *Fan curves* says so next to its speed.
- **The last 7 days at a glance** (*Reports*, at the top): one line like "12 h 05 min played · GPU up to 78 °C · cooling OK". Under it: how many sessions and which game most, whether that is more or less than the week before, the highest GPU hotspot and CPU temperature, and what *Cooling health* says in one sentence.

## [0.2.2] - 01.10.2026

### Fixed

- **A second CPU fan was called "Pump Fan header":** the names of the mainboard's fan outputs come from one fixed list per fan chip, and on a mainboard that list wasn't written for they can be wrong. On the ASRock X870 Steel Legend WiFi the two CPU fan headers are now called *CPU Fan 1* and *CPU Fan 2*, as printed on the board. AuFantic reads the mainboard's make and model for this and uses the right names where it knows them; more boards follow as they are reported. Your fans, curves and the Monitor's history stay as they are, only the names change, and the Log says so once. Nothing has to be found or calibrated again.

### Changed

- **The sensor list names the mainboard** (*Settings → Developer → Show all sensors*): its first lines now say which mainboard it is. If a fan has the wrong name on your PC, that file is what's needed to fix it.

## [0.2.1] - 01.10.2026

### New

- **Hide a line in a chart:** click a name above a chart (*Monitor*, *Cooling health*) and its line is gone; click again and it's back. The name stays, greyed, with an empty square. The scale then fits the lines that are left, so a single one is easier to read.
- **Compare: your own chart** (*Monitor*): pick any lines you want to see together, for example the GPU memory temperature and the GPU fans' rpm. Each unit has its own scale: the first one you picked on the left, the second on the right; hover for the values of the others. Your choice is kept.
- **The Monitor's heading and time ranges stay in view** while the charts scroll.
- **Say yourself what a fan cools** (*Calibration → Your fans*): next to how many fans are on an output and how loud they are, you can now choose *CPU cooler*, *case fans* or *graphics card*. The calibration measures this, and *as measured* stays the default. If it came out wrong, your choice counts: the fan's curve then follows that temperature (case fans: the warmer of CPU and GPU). It is kept when the fans are found again.
- **The logo's fan has seven blades** instead of three, with rounded tips, like a real PC fan: in the program's icon, the window, the taskbar and next to the clock.

### Changed

- **"Switch off when the PC is idle and cool" can always be ticked** (*Fan curves*), also for fans whose standstill at 0 % wasn't measured, and dragging a point down to 0 % switches it on for every fan. Before, the box was greyed out until *Find my fans* had seen the fans stand still. What was measured is still said under the box: fans that kept turning at 0 % in the test may only get slower. The box stays greyed out only while the BIOS controls these fans.

### Fixed

- **Three fans on a graphics card:** many cards have three fans on two outputs (the first and the third are wired together, the middle one has its own), so AuFantic finds "GPU fans (#8, #9)". That is how the card is built, not a mistake. Under *Calibration → Your fans* you could only end up with 2 or 4 fans there: 1, 3 and 5 jumped to the next even number. Now the number you choose is the number that counts.

## [0.2.0] - 01.10.2026

### New

- **AutoFantic is now called AuFantic,** and it has a logo: a fan with three blades. It's the icon of the program, the window and the taskbar; the icon next to the clock shows the same fan in the colour of the state (green: running, grey: paused, orange: cooling down, red: sensor problem, blue: not set up). Only the name changed: the file is still `AutoFantic.exe`, your data stays in `%LocalAppData%\AutoFantic`, and updates and *Start with Windows* work as before.
- **Improve from everyday use** (*Calibration*): AuFantic keeps every minute of how warm CPU and GPU were, at which power and which fan speeds. *Analyse my use* checks the curves against how the PC really ran since the last calibration and suggests better ones. It says what it found in plain words (for example "CPU: 10 °C warmer at 33 W, 6 °C warmer at 78 W than the calibrations expect") and shows each fan's curve now and as suggested. Nothing changes until you click *Use the new curves*; *Back to the calibration's curves* undoes it. It needs about an hour of use in which every fan turned, and it counts until the next calibration. What each fan cools stays as the calibrations measured it: only they set the fans on purpose.
- **Icons in the sidebar,** and a new order: what you look at every day is at the top, *Calibration*, *Log* and *Settings* are at the bottom. *Activity* is now called *Log*.
- **The Monitor leaves out the time the PC was off:** the charts show only the time AuFantic was running, one sitting after the other, with a dashed line where time was left out. The axis still shows the real time of day (every hour in the shorter ranges), with the date under the first label of each day; the longer ranges show days and months. Because less time fills the same width, the lines are also drawn in finer steps. The four charts share one axis, so the same moment is at the same place in each.

### Fixed

- **"These fans keep turning at 0 %" after finding the fans again:** *Find my fans* only tested 100, 60 and 30 %, and running it again threw away what an earlier calibration had measured at 0 %. After that, mainboard fans that do stand still could no longer be switched off. Now *Find my fans* tests 0 % itself and gives the fan up to 20 seconds to come to a standstill, keeps what was measured before at speeds it doesn't test, and works the curves out again. If this happened to you: *Calibration → Find my fans again* while no game is running.
- **No fan is stopped under load:** while a game or a render runs, *Find my fans* skips the 0 % test. The window then says "0 % not tested yet" instead of claiming the fans keep turning.
- **An update no longer adds files:** it replaces `AutoFantic.exe` and whatever else of AuFantic is already next to it. If you keep only the exe (say, on the desktop), the test console, readme, licence and changelog are no longer put there. This applies to updates made by 0.2.0 and later: the update to 0.2.0 itself is still done by your old version.

## [0.1.9] - 01.10.2026

### New

- **AutoFantic installs the PawnIO driver for you.** If it's missing, the window asks once at the start (and the check has an *Install PawnIO* button). On yes it downloads PawnIO's official installer from its GitHub release, checks it against a fixed checksum, installs it without another prompt and starts again. Nothing is installed without your yes.

### Fixed

- **No mainboard fans and CPU at 0 °C without the PawnIO driver:** the set-up check passed anyway when the PC had a water cooler with its own controller (its pump counted as a "mainboard fan"), and a CPU that read 0 °C counted as found. Now the check says that the PawnIO driver is missing and offers to install it.
- **Nothing starts without a CPU temperature:** *Find my fans* and the calibration don't start while the CPU temperature can't be read, and the fan control hands the fans to the BIOS instead of taking 0 °C for a cool CPU.
- **A water cooler's own pump is left alone** (NZXT Kraken "Pump Control", Aquacomputer): it isn't run through the speeds and never gets a curve. A mainboard header that is only named "Pump Fan" is still found by how its fan turns.
- **The fans are found again when the outputs changed**, for example after installing the PawnIO driver (the mainboard's outputs appear) or swapping the graphics card. Until then the BIOS keeps the fans.
- **An old version came back after a restart:** *Start with Windows* kept starting another copy of AutoFantic in another folder. Now it follows the copy you start, and says so in the Activity log. If the other copy is already running, the message names its folder.

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
