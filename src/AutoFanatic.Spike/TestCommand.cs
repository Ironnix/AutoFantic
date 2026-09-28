using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AutoFanatic.Core;
using AutoFanatic.Core.Analysis;
using AutoFanatic.Core.Calibration;
using AutoFanatic.Core.Hardware;
using AutoFanatic.Core.Logging;

namespace AutoFanatic.Spike;

/// <summary>
/// "test": AutoFanatic as a menu. Find the fans, calibrate while playing, see the best curves,
/// let AutoFanatic run the fans; the Phase 0 tests are tucked away under "More". Each step says in plain words what to do and saves its result into one
/// folder (runs\ in the repo), where Claude reads it afterwards. Started by double-clicking Start-Test.cmd.
/// </summary>
internal static class TestCommand
{
    private const string Line = "──────────────────────────────────────────────────────────────────────";

    public static int Run(FanSession session, string[] args, bool simulated)
    {
        var options = new Options(args);
        var runs = new RunsFolder(options.Get("--runs") ?? RunsFolder.Default(session));
        using var input = new LineReader();

        Console.WriteLine("Before you start: turn OFF fan control in other fan tools (Armoury Crate, iCUE,");
        Console.WriteLine("Fan Control, Argus, MSI Afterburner's fan curve). Watching temperatures in them is fine.");
        Console.WriteLine();
        Guard.WarnAboutOtherFanTools();

        while (true)
        {
            PrintMenu(runs);
            string? choice = input.Ask("Type a number and press Enter", CtrlC.Reset());
            Console.WriteLine();
            // only one program may drive the fans
            if (choice?.Trim() is "1" or "2" or "4" or "5" or "9" && !simulated && DataFolder.BackgroundRunning())
            {
                Console.WriteLine("AutoFanatic is running in the background (icon next to the clock) and controls the fans.");
                Console.WriteLine("Right-click the icon → Exit first, then choose this again.");
                Console.WriteLine();
                continue;
            }

            switch (choice?.Trim())
            {
                case "1":
                    FindFans(session, runs, input);
                    break;
                case "2":
                    Calibrate(session, runs, input);
                    break;
                case "3":
                    ShowCurves(runs, input);
                    break;
                case "4":
                    UseCurves(session, runs, input);
                    break;
                case "5":
                    if (StartBackground(runs, simulated))
                        return 0;
                    break;
                case "9":
                    More(session, runs, input, simulated);
                    break;
                case "0" or null:
                    Finish(runs, input);
                    return 0;
                default:
                    Console.WriteLine("Please type a number from the menu.");
                    break;
            }
            Console.WriteLine();
        }
    }

    private static void PrintMenu(RunsFolder runs)
    {
        var inventory = runs.Inventory();
        string fans = inventory is null ? "" : $"✓ {inventory.Groups().Count} fan groups";
        var latest = runs.Latest("calibration-*.txt");

        Console.WriteLine(Line);
        Console.WriteLine(" AutoFanatic");
        Console.WriteLine($" Results are saved in {runs.Path}");
        Console.WriteLine(Line);
        Console.WriteLine($"  1  Find my fans            once, about 2 min, PC idle       {fans}");
        Console.WriteLine($"  2  Calibrate               15-30 min, while you play        {(latest is null ? "" : $"✓ {latest.LastWriteTime:dd.MM. HH:mm}")}");
        Console.WriteLine("  3  Show my best curves");
        Console.WriteLine("  4  Use my curves           AutoFanatic runs your fans in this window");
        Console.WriteLine("  5  Run in the background   the AutoFanatic window + icon, this closes");
        Console.WriteLine();
        Console.WriteLine("  9  More (developer tests)");
        Console.WriteLine("  0  Exit");
        Console.WriteLine(Line);
    }

    private static void More(FanSession session, RunsFolder runs, LineReader input, bool simulated)
    {
        Console.WriteLine("""
            MORE (developer tests; not needed for normal use)
              1  Record a gaming session   only watches, then shows how often AutoFanatic could learn
              2  Measure one fan group     the knee of one fan group by hand, under a steady load
              3  Crash test                what happens to the fans if AutoFanatic crashes
              0  Back
            """);
        switch (input.Ask("Type a number and press Enter", CtrlC.Reset())?.Trim())
        {
            case "1":
                RecordSession(session, runs, input);
                break;
            case "2":
                MeasureFans(session, runs, input);
                break;
            case "3":
                CrashTest(session, runs, input, simulated);
                break;
        }
    }

    // ── 1: find the fans ───────────────────────────────────────────────────────────────

    private static bool FindFans(FanSession session, RunsFolder runs, LineReader input)
    {
        Console.WriteLine("""
            FIND MY FANS (about 2 minutes)

            Close games and other heavy programs, so the PC is idle.
            Each fan output speeds up and slows down, one after another, to find out which
            headers really have a fan on them. Empty headers are ignored from then on.
            """);
        if (input.Ask("Press Enter to start (or b + Enter to go back)", CtrlC.Reset()) is null or "b")
            return false;

        var snapshot = session.Read();
        runs.Write("hardware.txt", ListCommand.Build(session, snapshot));
        if (!SummarizeHardware(session, snapshot))
            return false;

        FanInventory? inventory = null;
        Console.WriteLine();
        DiscoverCommand.Run(session, ["--out", runs.File("discover.txt")], CtrlC.Reset(), found => inventory = found);
        Console.WriteLine();
        if (inventory is null)
            return false;

        var setup = runs.ReadSetup();
        string? cooler = input.Ask("How is your CPU cooled? Type w for an AIO water cooler, a for an air cooler", CtrlC.Reset())?.Trim().ToLowerInvariant();
        if (cooler is "w" or "a")
            setup["cooler"] = cooler == "w" ? "AIO water cooler" : "air cooler";

        var pumps = inventory.Headers.Where(h => h.IsPump).Select(h => h.Channel).ToHashSet();
        if (pumps.Count > 0)
        {
            string guess = string.Join(",", pumps);
            string? answer = input.Ask($"#{guess} looks like a pump (fast, barely changes speed). Enter = yes, or type the right #, or n if there is no pump", CtrlC.Reset());
            if (!string.IsNullOrWhiteSpace(answer))
                pumps = answer.Trim() == "n" ? [] : ParseNumbers(answer);
        }
        else if (setup.GetValueOrDefault("cooler") == "AIO water cooler")
        {
            string? answer = input.Ask("Which # is the pump? Type it, or just Enter if you don't know", CtrlC.Reset());
            pumps = ParseNumbers(answer ?? "");
        }
        setup["pump"] = string.Join(",", pumps);
        inventory = inventory.WithPumps(pumps);

        string? names = input.Ask("Optional: which fan is which? e.g. 0=CPU cooler front, 1=CPU cooler rear, 5=case fans (Enter to skip)", CtrlC.Reset());
        if (!string.IsNullOrWhiteSpace(names))
            setup["fans"] = names.Trim();

        runs.WriteSetup(setup);
        inventory.Save(runs.File("fans.json"));

        Console.WriteLine();
        Console.WriteLine("These fans will be used:");
        foreach (var group in inventory.Groups())
            Console.WriteLine($"   {group.Name}");
        Console.WriteLine("Done. Next: 2 Calibrate.");
        return true;
    }

    private static HashSet<int> ParseNumbers(string text) =>
        text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p.TrimStart('#'), out int i) ? i : -1)
            .Where(i => i >= 0)
            .ToHashSet();

    // ── 2: calibrate ───────────────────────────────────────────────────────────────────

    private static void Calibrate(FanSession session, RunsFolder runs, LineReader input)
    {
        if (runs.Inventory() is null)
        {
            Console.WriteLine("First the tool needs to know which headers have a fan on them.");
            Console.WriteLine();
            if (!FindFans(session, runs, input))
                return;
            Console.WriteLine();
        }

        Console.WriteLine("""
            CALIBRATE (15-30 minutes, while you play)

            While you play, AutoFanatic tries 9 combinations of fan speeds and watches how the
            temperatures follow the power: that gives real numbers for your PC, even when the
            game's load jumps around. From that it works out the quietest fan speeds for every
            load, from idle to heavier than your game, so the result works for everything.

            If the PC is idle when you start, it first checks which fans can be switched off
            (up to 2½ minutes, all fans stop). Then it asks you to start your game.
            You'll hear the fans change. If it gets too hot, all fans go to 100 % until it's cool.
            """);

        string? roomText = input.Ask("Room temperature in °C? (a guess is fine; Enter = 22)", CtrlC.Reset());
        if (roomText is null || roomText.Trim() == "b")
            return;
        double room = double.TryParse(roomText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double r) && r is > 5 and < 45 ? r : 22;

        string? target = input.Ask("Temperature limit: 80 = cool and quiet (Enter), 90 = as quiet as possible", CtrlC.Reset());
        if (target is null)
            return;
        string profile = target.Trim() == "90" ? "90" : "80";

        string? loadChoice = input.Ask("Load: Enter = your game (recommended), b = built-in load instead (then don't use the PC)", CtrlC.Reset());
        if (loadChoice is null)
            return;

        string[] args = ["--runs", runs.Path, "--ambient", room.ToString(CultureInfo.InvariantCulture), "--profile", profile];
        if (loadChoice.Trim() == "b")
            args = [.. args, "--builtin-load"];

        Console.WriteLine();
        if (CalibrateCommand.Run(session, args, CtrlC.Reset()) == 0)
            OpenPage(runs);
    }

    private static void ShowCurves(RunsFolder runs, LineReader input)
    {
        if (runs.Latest("calibration-*.txt") is not { } latest)
        {
            Console.WriteLine("No calibration yet: choose 2 Calibrate first.");
            return;
        }

        string? limit = input.Ask("Enter = show them, or type 80 / 90 to work them out again for that limit (no new measuring)", CtrlC.Reset());
        if (limit?.Trim() is "80" or "90")
            CalibrateCommand.RecalculateCommand(["--runs", runs.Path, "--profile", limit.Trim()]);
        else
            Console.Write(File.ReadAllText(latest.FullName));

        OpenPage(runs);
    }

    /// <summary>
    /// Opens runs\calibration.html in the browser. Through Explorer, so the browser starts as the
    /// normal user rather than with this window's admin rights.
    /// </summary>
    private static void OpenPage(RunsFolder runs)
    {
        string page = runs.File("calibration.html");
        if (!File.Exists(page) || Console.IsInputRedirected)
            return;
        try
        {
            using var _ = System.Diagnostics.Process.Start("explorer.exe", $"\"{page}\"");
            Console.WriteLine($"The page opens in your browser: {page}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"Open this page in your browser: {page}");
        }
    }

    // ── 5: background ──────────────────────────────────────────────────────────────────

    /// <summary>Starts AutoFanatic.exe (the icon next to the clock) and ends this menu, so only one program drives the fans.</summary>
    private static bool StartBackground(RunsFolder runs, bool simulated)
    {
        if (!runs.Exists("calibration.json"))
        {
            Console.WriteLine("No calibration yet: choose 2 Calibrate first.");
            return false;
        }

        string exe = Path.Combine(AppContext.BaseDirectory, "AutoFanatic.exe");
        if (!File.Exists(exe))
        {
            Console.WriteLine($"AutoFanatic.exe is missing next to this program ({AppContext.BaseDirectory}): ask Claude to build it.");
            return false;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, Arguments = simulated ? "--simulate --open" : "--open" })?.Dispose();
        Console.WriteLine("""
            AutoFanatic now runs in the background and its window opens. Later you'll find it as
            the round icon next to the clock (maybe under the little arrow): double-click opens
            the window again, right-click shows the fans and pause / exit. This window closes now.
            """);
        return true;
    }

    // ── 4: use the curves ──────────────────────────────────────────────────────────────

    private static void UseCurves(FanSession session, RunsFolder runs, LineReader input)
    {
        if (!runs.Exists("calibration.json"))
        {
            Console.WriteLine("No calibration yet: choose 2 Calibrate first.");
            return;
        }

        Console.WriteLine("""
            USE MY CURVES

            AutoFanatic now runs your fans with your calibrated curves: for games and everything
            else. Fans are off at idle where that was found safe, speed up quickly when it gets
            warmer and slow down gently. Keep this window open (you can minimize it).
            """);
        RunCommand.Run(session, ["--runs", runs.Path], CtrlC.Reset(), stopRequested: () => input.TryTake(TimeSpan.Zero, out _));
    }

    /// <summary>Short verdict on what the tool found. False if there's nothing to control.</summary>
    private static bool SummarizeHardware(FanSession session, Snapshot snapshot)
    {
        var keys = KeySensors.Detect(snapshot);
        var chip = snapshot.Readings.FirstOrDefault(r => r.HardwareType == "SuperIO");
        int boardFans = snapshot.Readings.Count(r => r.HardwareType == "SuperIO" && r.Kind == SensorKind.Control);

        Console.WriteLine();
        Console.WriteLine($"   Mainboard fan chip   {(chip is null ? "NOT FOUND" : chip.Hardware + " ✓")}");
        Console.WriteLine($"   Fan controls         {session.Channels.Count} ({boardFans} on the mainboard, {session.Channels.Count - boardFans} other)");

        var found = new List<string>();
        var missing = new List<string>();
        foreach (var (label, id) in new[]
                 {
                     ("CPU temperature", keys.CpuTemp), ("CPU power", keys.CpuPower), ("CPU load", keys.CpuLoad),
                     ("GPU core", keys.GpuTemp), ("GPU hotspot", keys.GpuHotspot), ("GPU memory", keys.GpuMemory),
                     ("GPU power", keys.GpuPower), ("GPU load", keys.GpuLoad),
                 })
            (id is null ? missing : found).Add(label);
        Console.WriteLine($"   Key sensors          {found.Count} of {found.Count + missing.Count} found" + (missing.Count == 0 ? " ✓" : $", missing: {string.Join(", ", missing)}"));

        if (boardFans == 0)
        {
            Console.WriteLine();
            Console.WriteLine("   The mainboard fans can't be controlled yet. Most likely the PawnIO driver is missing:");
            Console.WriteLine("   install it from https://pawnio.eu (or start the LibreHardwareMonitor app once, it offers");
            Console.WriteLine("   to install it), then run step 1 again. hardware.txt is saved either way.");
        }

        if (session.Channels.Count == 0)
            return false;

        return true;
    }

    // ── Step 2 ─────────────────────────────────────────────────────────────────────────

    private static void RecordSession(FanSession session, RunsFolder runs, LineReader input)
    {
        Console.WriteLine("""
            RECORD A GAMING SESSION (as long as you play; 30-60 minutes is ideal)

            Nothing is changed: the fans stay on the BIOS curve, AutoFanatic only watches and writes
            down temperatures, power and which program is in front, once per second.
            Afterwards it works out how often it could have learned something during your session.
            This also checks that your games (and their anti-cheat) don't mind AutoFanatic's driver.
            """);
        string? game = input.Ask("Which game will you play? (just for the file name; b + Enter to go back)", CtrlC.Reset());
        if (game is null || game.Trim() == "b")
            return;

        string name = $"watch-{RunsFolder.Safe(game, "game")}-{DateTime.Now:yyyyMMdd-HHmm}";
        string csvPath = runs.File(name + ".csv");

        var cancel = CtrlC.Reset();
        var first = session.Read();
        var keys = KeySensors.Detect(first);
        var tick = TimeSpan.FromSeconds(1 / session.TimeScale);
        var started = session.Now;

        Console.WriteLine();
        Console.WriteLine("Recording. Start your game now and play normally.");
        Console.WriteLine("When you're done: come back to this window and press Enter.");
        Console.WriteLine();

        using (var log = new SensorLogWriter(csvPath, first))
        {
            while (!cancel.IsCancellationRequested && !input.TryTake(tick, out _))
            {
                var s = session.Read();
                string? foreground = session.Foreground();
                log.Write(s, foreground);
                Console.Write($"\r   recording {session.Now - started:h\\:mm\\:ss} · CPU {C(s.Value(keys.CpuTemp))} · GPU {C(s.Value(keys.GpuTemp))} · in front: {foreground ?? "?"}".PadRight(90));
            }
        }
        Console.WriteLine();
        Console.WriteLine();

        var length = session.Now - started;
        if (length < TimeSpan.FromMinutes(5))
            Console.WriteLine($"Only {length.TotalMinutes:0.0} minutes recorded. For useful results, play at least 30 minutes next time.");

        try
        {
            var recorded = SensorLogReader.Read(csvPath).ToList();
            var rules = new ExperimentRules();
            string analysis = AnalyzeCommand.Build(csvPath, recorded, LearningOpportunities.Analyze(recorded, rules), rules);
            runs.Write(name + "-analysis.txt", analysis);
            Console.Write(analysis);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            Console.WriteLine($"Could not analyse the recording: {ex.Message}");
        }

        string? complaint = input.Ask("Did the game or its anti-cheat complain about anything? y/n (and Enter)", CtrlC.Reset());
        if (complaint?.Trim().ToLowerInvariant() is "y" or "yes" or "j" or "ja")
        {
            string? what = input.Ask("What did it say?", CtrlC.Reset());
            runs.Note($"Anti-cheat complaint while playing {game}: {what}");
        }
        else if (complaint is not null)
            runs.Note($"Played {game} for {length.TotalMinutes:0} min: no anti-cheat complaint.");

        Console.WriteLine($"Done: {name}.csv and the analysis are saved.");
    }

    private static string C(float? celsius) => celsius is { } c ? $"{c:0} °C" : "–";

    // ── Step 3 ─────────────────────────────────────────────────────────────────────────

    private static void MeasureFans(FanSession session, RunsFolder runs, LineReader input)
    {
        var pumps = runs.PumpChannels();
        var usable = runs.Inventory()?.Usable.Select(h => h.Channel).ToHashSet();
        var candidates = session.Channels.Where(c => !pumps.Contains(c.Index) && (usable is null || usable.Contains(c.Index))).ToList();

        Console.WriteLine("""
            MEASURE ONE FAN GROUP (10-25 minutes each)

            Finds the "knee" of a fan group: the speed beyond which more RPM stops helping.
            The fans go 100 → 80 → 60 → 45 → 30 %, and at every step the tool waits until the
            temperatures have settled. It needs a STEADY load the whole time:
              • GPU fan or case fans: a game standing still in a demanding scene, or a GPU benchmark in a loop
              • CPU fan: a CPU benchmark in a loop (e.g. Cinebench multi-core) or a video render
            Good order: the GPU fan, then all case fans together, then the CPU fan.
            If it gets too hot, all fans go to 100 % until it has cooled down, and the measurement ends.

            Fans you can measure:
            """);
        foreach (var c in candidates)
            Console.WriteLine($"   #{c.Index,-3} {c.Name} ({c.Hardware})");
        if (pumps.Count > 0)
            Console.WriteLine($"   (#{string.Join(", #", pumps)}: pump, never measured)");
        Console.WriteLine();

        IReadOnlyList<FanChannel> channels;
        while (true)
        {
            string? answer = input.Ask("Which fan(s)? Type the # (several together like 1,2), or b to go back", CtrlC.Reset());
            if (answer is null || answer.Trim() is "b" or "")
                return;
            try
            {
                channels = Options.ParseChannels(session, answer);
                if (channels.FirstOrDefault(c => pumps.Contains(c.Index)) is { } pump)
                {
                    Console.WriteLine($"#{pump.Index} is the pump: it is never measured.");
                    continue;
                }
                break;
            }
            catch (UsageException ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        string? roomText = input.Ask("Room temperature in °C? (a guess is fine; Enter = 22)", CtrlC.Reset());
        double room = double.TryParse(roomText?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double r) && r is > 5 and < 45 ? r : 22;

        if (input.Ask("Start the load now. Press Enter when it's running (b + Enter to go back)", CtrlC.Reset()) is null or "b")
            return;

        string ids = string.Join(",", channels.Select(c => c.Index));
        string name = $"sweep-{ids.Replace(',', '+')}-{DateTime.Now:yyyyMMdd-HHmm}";
        Console.WriteLine();
        SweepCommand.Run(session,
            [ids, "--ambient", room.ToString(CultureInfo.InvariantCulture), "--csv", runs.File(name + ".csv"), "--out", runs.File(name + ".txt")],
            CtrlC.Reset());

        string? load = input.Ask("What load did you use? (e.g. Cyberpunk standing still, Cinebench; Enter to skip)", CtrlC.Reset());
        if (!string.IsNullOrWhiteSpace(load))
            runs.Note($"{name}: fans #{ids}, room {room:0} °C, load: {load.Trim()}");
    }

    // ── Step 4 ─────────────────────────────────────────────────────────────────────────

    private static void CrashTest(FanSession session, RunsFolder runs, LineReader input, bool simulated)
    {
        Console.WriteLine("""
            CRASH TEST (about 1 minute)

            What happens to the fans if AutoFanatic crashes? A helper program sets a case fan and
            the GPU fan to 100 %. Then it is force-closed, exactly like Task Manager → End task,
            and the tool checks whether the fans go back to normal, and whether "restore" helps.

            If a fan stays at full speed afterwards, restart the PC when it suits you. That's loud
            but harmless, and finding it out is the point of this test.
            """);

        if (simulated)
        {
            Console.WriteLine("This step only works on the real PC: a simulated PC can't be crashed from outside.");
            return;
        }

        var pumps = runs.PumpChannels();
        bool IsGpu(FanChannel c) => c.Id.Contains("gpu", StringComparison.OrdinalIgnoreCase);
        var usable = runs.Inventory()?.Usable.Select(h => h.Channel).ToHashSet();
        var board = session.Channels.Where(c => !IsGpu(c) && !pumps.Contains(c.Index) && (usable is null || usable.Contains(c.Index))).ToList();
        var suggestion = board.FirstOrDefault(c => !c.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)) ?? board.FirstOrDefault();

        var targets = new List<FanChannel>();
        if (board.Count > 0)
        {
            string? answer = input.Ask($"Which # is a case fan? (Enter = #{suggestion!.Index}; b to go back)", CtrlC.Reset());
            if (answer is null || answer.Trim() == "b")
                return;
            var chosen = string.IsNullOrWhiteSpace(answer)
                ? suggestion
                : session.Channels.FirstOrDefault(c => c.Index.ToString(CultureInfo.InvariantCulture) == answer.Trim().TrimStart('#'));
            if (chosen is null || pumps.Contains(chosen.Index))
            {
                Console.WriteLine("That's not a fan channel the test can use.");
                return;
            }
            targets.Add(chosen);
        }
        if (session.Channels.FirstOrDefault(IsGpu) is { } gpu)
            targets.Add(gpu);
        if (targets.Count == 0)
        {
            Console.WriteLine("No fan channels found, nothing to test.");
            return;
        }

        if (input.Ask($"Testing {string.Join(" and ", targets)}. Press Enter to start", CtrlC.Reset()) is null)
            return;

        var cancel = CtrlC.Reset();
        var helpers = new List<Process>();
        var report = new StringBuilder($"AutoFanatic crash test · {DateTime.Now:yyyy-MM-dd HH:mm}\n\n");
        try
        {
            var before = session.Read();

            Console.WriteLine("   helper sets the fans to 100 % …");
            foreach (var target in targets)
                helpers.Add(StartHelper(target));
            if (cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(15)))
                return;
            var during = session.Read();

            Console.WriteLine("   force-closing the helper (like End task) …");
            foreach (var helper in helpers)
                KillQuietly(helper);
            if (cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                return;
            var afterCrash = session.Read();

            Console.WriteLine("   trying \"restore\" …");
            foreach (var target in targets)
                session.ForceRestore(target);
            if (cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(15)))
                return;
            var afterRestore = session.Read();

            report.AppendLine("fan                                      before  helper 100%  after crash  after restore  result");
            foreach (var target in targets)
                report.AppendLine(Verdict(target, runs.Inventory()?.Headers.FirstOrDefault(h => h.ControlId == target.Id)?.RpmSensorId, before, during, afterCrash, afterRestore));
        }
        finally
        {
            foreach (var helper in helpers)
            {
                KillQuietly(helper);
                helper.Dispose();
            }
        }

        report.AppendLine();
        report.AppendLine("\"back on its own\": the BIOS/driver took over after the crash, nothing to do.");
        report.AppendLine("\"back after restore\": a crash leaves it stuck, but a restart of AutoFanatic can fix it.");
        report.AppendLine("\"STUCK\": only a PC restart brings the BIOS curve back; the watchdog must store the BIOS settings.");

        Console.WriteLine();
        Console.Write(report);
        runs.Write("crash-test.txt", report.ToString());

        if (report.ToString().Contains("STUCK", StringComparison.Ordinal))
            Console.WriteLine("\nA fan is stuck at full speed: restart the PC when it suits you (loud, but harmless).");
        Console.WriteLine("Done: crash-test.txt is saved.");
    }

    /// <summary>A second copy of this tool holding one fan at 100 %; its output is discarded.</summary>
    private static Process StartHelper(FanChannel channel)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "set", channel.Index.ToString(CultureInfo.InvariantCulture), "100", "--seconds", "300" })
            start.ArgumentList.Add(arg);

        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the helper.");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // already gone
        }
    }

    /// <summary>One line: this channel's own RPM sensor (from fans.json), through the four moments of the test.</summary>
    private static string Verdict(FanChannel channel, string? rpmSensorId, Snapshot before, Snapshot during, Snapshot afterCrash, Snapshot afterRestore)
    {
        // Both fans change at the same moment, so "the sensor that changed most" would pick the same
        // one for both: only the sensor discover tied to this channel tells them apart.
        var fan = rpmSensorId is null
            ? default
            : (Id: rpmSensorId, Change: (during.Value(rpmSensorId) ?? 0) - (before.Value(rpmSensorId) ?? 0));

        string label = channel.ToString();
        if (fan.Id is null || fan.Change < SetCommand.ReactionRpm)
        {
            float? percentNow = afterCrash.Value(channel.Id);
            return $"{label,-40} no RPM reaction seen; control reads {percentNow:0} % after the crash";
        }

        float b = before.Value(fan.Id) ?? 0, d = during.Value(fan.Id) ?? 0, c = afterCrash.Value(fan.Id) ?? 0, r = afterRestore.Value(fan.Id) ?? 0;
        bool Normal(float rpm) => Math.Abs(rpm - b) <= Math.Max(250, b * 0.2);

        string result = Normal(c) ? "back on its own"
            : Normal(r) ? "back after restore"
            : "STUCK until restart";
        return $"{label,-40} {b,6:0}  {d,11:0}  {c,11:0}  {r,13:0}  {result}";
    }

    // ── Results and exit ───────────────────────────────────────────────────────────────

    private static void ShowResults(RunsFolder runs)
    {
        var files = runs.Files();
        if (files.Count == 0)
        {
            Console.WriteLine("Nothing saved yet.");
            return;
        }

        Console.WriteLine($"Saved in {runs.Path}:");
        foreach (var file in files)
            Console.WriteLine($"   {file.LastWriteTime:yyyy-MM-dd HH:mm}  {file.Name}");
        Console.WriteLine();
        Console.WriteLine("When you're done, tell Claude in VS Code \"the test results are ready\". It reads this folder itself.");
    }

    private static void Finish(RunsFolder runs, LineReader input)
    {
        string? odd = input.Ask("Anything odd you noticed (noise, fans pulsing, error messages)? Type it, or just Enter", CtrlC.Reset());
        if (!string.IsNullOrWhiteSpace(odd))
            runs.Note(odd.Trim());

        Console.WriteLine();
        ShowResults(runs);
    }
}

/// <summary>The folder the test writes into, plus the small setup file (cooler, pump) and the notes.</summary>
internal sealed class RunsFolder
{
    public RunsFolder(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>
    /// runs\ in the repo (found by walking up from the exe to AutoFanatic.sln), else runs\ next to
    /// the exe. A simulated PC writes into runs\sim\, so its made-up data never mixes with real results.
    /// </summary>
    public static string Default(FanSession session) => DataFolder.Default(session is Core.Simulation.SimulatedPc);

    public static string Default() => DataFolder.Default();

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public bool Exists(string name) => System.IO.File.Exists(File(name));

    public int Count(string pattern) => Directory.GetFiles(Path, pattern).Length;

    public FileInfo? Latest(string pattern) =>
        new DirectoryInfo(Path).GetFiles(pattern).OrderBy(f => f.LastWriteTime).LastOrDefault();

    /// <summary>What "Find my fans" found (fans.json); null before it ran.</summary>
    public FanInventory? Inventory() => FanInventory.Load(File("fans.json"));

    public List<FileInfo> Files() =>
        new DirectoryInfo(Path).GetFiles().OrderBy(f => f.LastWriteTime).ToList();

    public void Write(string name, string text) => System.IO.File.WriteAllText(File(name), text, Encoding.UTF8);

    public void Note(string text) =>
        System.IO.File.AppendAllText(File("notes.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm}  {text}{Environment.NewLine}", Encoding.UTF8);

    public Dictionary<string, string> ReadSetup()
    {
        var setup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Exists("setup.txt"))
            return setup;
        foreach (var line in System.IO.File.ReadAllLines(File("setup.txt")))
        {
            int eq = line.IndexOf('=');
            if (eq > 0)
                setup[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return setup;
    }

    public void WriteSetup(Dictionary<string, string> setup) =>
        System.IO.File.WriteAllLines(File("setup.txt"), setup.Select(kv => $"{kv.Key} = {kv.Value}"), Encoding.UTF8);

    /// <summary>Channel numbers marked as pump in setup.txt.</summary>
    public HashSet<int> PumpChannels() =>
        ReadSetup().GetValueOrDefault("pump", "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p.TrimStart('#'), out int i) ? i : -1)
            .Where(i => i >= 0)
            .ToHashSet();

    /// <summary>Something usable in a file name.</summary>
    public static string Safe(string text, string fallback)
    {
        var chars = text.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        string safe = new string(chars).Trim('-');
        return safe.Length == 0 ? fallback : safe.Length > 30 ? safe[..30] : safe;
    }
}

/// <summary>
/// All console input goes through one reader thread, so a step can wait for "Enter" while it keeps
/// sampling, and no two places ever compete for the same line.
/// </summary>
internal sealed class LineReader : IDisposable
{
    private readonly BlockingCollection<string?> _lines = [];

    public LineReader()
    {
        var thread = new Thread(ReadLoop) { IsBackground = true, Name = "console input" };
        thread.Start();
    }

    /// <summary>Prints the prompt and waits for a line. Null on Ctrl+C or when the input ends.</summary>
    public string? Ask(string prompt, CancellationToken cancel)
    {
        Console.Write(prompt + ": ");
        try
        {
            string? line = _lines.Take(cancel);
            if (Console.IsInputRedirected)
                Console.WriteLine(line); // echo, so a scripted run reads like an interactive one
            return line;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            return null;
        }
        catch (InvalidOperationException)
        {
            return null; // input ended
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for a line (e.g. "Enter to stop").</summary>
    public bool TryTake(TimeSpan timeout, out string? line)
    {
        if (_lines.IsCompleted)
        {
            line = null;
            return true; // input ended: treat like Enter
        }
        try
        {
            return _lines.TryTake(out line, timeout);
        }
        catch (InvalidOperationException)
        {
            line = null;
            return true; // input ended: treat like Enter
        }
    }

    public void Dispose() => _lines.Dispose();

    private void ReadLoop()
    {
        while (true)
        {
            string? line;
            try
            {
                line = Console.ReadLine();
            }
            catch
            {
                line = null;
            }

            if (line is null)
            {
                if (Console.IsInputRedirected)
                {
                    _lines.CompleteAdding();
                    return;
                }
                Thread.Sleep(100); // Ctrl+C in an interactive console: keep listening
                continue;
            }

            try
            {
                _lines.Add(line);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }
}
