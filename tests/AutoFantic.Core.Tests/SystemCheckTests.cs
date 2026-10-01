using AutoFantic.Core.Calibration;
using AutoFantic.Core.Hardware;
using AutoFantic.Core.Simulation;

namespace AutoFantic.Core.Tests;

public class SystemCheckTests
{
    [Fact]
    public void A_supported_pc_passes_every_check()
    {
        using var pc = new SimulatedPc();
        var checks = SystemCheck.Run(pc, KeySensors.Detect(pc.Read()), PawnIo.Installed, otherTools: []);

        Assert.All(checks, c => Assert.Equal(CheckResult.Ok, c.Result));
        Assert.Contains("3 outputs", checks.Single(c => c.Title == "Mainboard fans").Detail);
        Assert.Contains("1 output on", checks.Single(c => c.Title == "Graphics card fans").Detail);
    }

    [Fact]
    public void Another_fan_program_is_a_warning_that_names_it()
    {
        using var pc = new SimulatedPc();
        var checks = SystemCheck.Run(pc, KeySensors.Detect(pc.Read()), PawnIo.Installed, otherTools: ["Fan Control"]);

        var tools = checks.Single(c => c.Title == "Other fan programs");
        Assert.Equal(CheckResult.Warning, tools.Result);
        Assert.Contains("Fan Control", tools.Detail);
    }

    [Fact]
    public void Without_the_driver_a_water_coolers_pump_doesnt_pass_for_the_mainboards_fans()
    {
        using var pc = new PcWithoutDriver();
        var checks = SystemCheck.Run(pc, KeySensors.Detect(pc.Read()), PawnIo.Missing, otherTools: []);

        var mainboard = checks.Single(c => c.Title == "Mainboard fans");
        Assert.Equal(CheckResult.Problem, mainboard.Result);
        Assert.Contains("PawnIO driver is missing", mainboard.Detail);
        Assert.Equal(CheckFix.GetPawnIo, mainboard.Fix);
        Assert.Equal(CheckResult.Ok, checks.Single(c => c.Title == "Graphics card fans").Result);
    }

    [Fact]
    public void A_cpu_that_reads_zero_degrees_is_a_problem_not_a_found_temperature()
    {
        using var pc = new PcWithoutDriver();
        var keys = KeySensors.Detect(pc.Read());

        var withoutDriver = SystemCheck.Run(pc, keys, PawnIo.Missing, otherTools: []).Single(c => c.Title == "Temperatures");
        var withDriver = SystemCheck.Run(pc, keys, PawnIo.Installed, otherTools: []).Single(c => c.Title == "Temperatures");

        Assert.NotNull(keys.CpuTemp); // the sensor is there, it just reads nothing
        Assert.Equal(CheckResult.Problem, withoutDriver.Result);
        Assert.Contains("PawnIO", withoutDriver.Detail);
        Assert.Equal(CheckResult.Problem, withDriver.Result);
        Assert.Contains("can't be read", withDriver.Detail);
    }

    [Fact]
    public void A_driver_installed_while_autofantic_runs_asks_for_a_restart()
    {
        using var pc = new PcWithoutDriver();
        var checks = SystemCheck.Run(pc, KeySensors.Detect(pc.Read()), PawnIo.InstalledSinceStart, otherTools: []);

        var mainboard = checks.Single(c => c.Title == "Mainboard fans");
        Assert.Equal(CheckFix.Restart, mainboard.Fix);
        Assert.Contains("restart AutoFantic", mainboard.Detail);
    }
}

/// <summary>Finding the fans on a PC without the driver, and with a water cooler that has its own controller.</summary>
public class FanDiscoveryTests
{
    [Fact]
    public void Nothing_starts_while_the_cpu_temperature_cant_be_read()
    {
        using var pc = new PcWithoutDriver(cpuTemp: 0);
        var log = new List<string>();

        var found = FanDiscovery.Run(pc, CancellationToken.None, log.Add);

        Assert.Null(found);
        Assert.Empty(pc.Set);
        Assert.Contains(log, line => line.Contains("CPU temperature can't be read"));
    }

    [Fact]
    public void A_water_coolers_own_pump_is_never_touched()
    {
        using var pc = new PcWithoutDriver(cpuTemp: 45);

        var found = FanDiscovery.Run(pc, CancellationToken.None)!;

        Assert.DoesNotContain(PcWithoutDriver.Pump, pc.Set);
        Assert.True(found.Headers.Single(h => h.ControlId == PcWithoutDriver.Pump).IsPump);
        Assert.Equal(["GPU fans (#0, #1)"], found.Groups().Select(g => g.Name));
    }

    [Fact]
    public void The_fans_found_fit_only_the_outputs_they_were_found_on()
    {
        using var pc = new PcWithoutDriver(cpuTemp: 45);
        var found = FanDiscovery.Run(pc, CancellationToken.None)!;
        using var other = new SimulatedPc();

        Assert.True(found.Fits(pc.Channels));
        Assert.False(found.Fits(other.Channels)); // e.g. the driver installed since: the mainboard's outputs are there now

        // found by a version that took the pump for a fan: found again, so it is left alone from now on
        var old = found.WithPumps(new HashSet<int>());
        Assert.False(old.Fits(pc.Channels));
    }
}
