using AutoFantic.Core.Hardware;
using AutoFantic.Core.Simulation;

namespace AutoFantic.Core.Tests;

public class SystemCheckTests
{
    [Fact]
    public void A_supported_pc_passes_every_check()
    {
        using var pc = new SimulatedPc();
        var checks = SystemCheck.Run(pc, KeySensors.Detect(pc.Read()), pawnIo: true, otherTools: []);

        Assert.All(checks, c => Assert.Equal(CheckResult.Ok, c.Result));
        Assert.Contains("3 outputs", checks.Single(c => c.Title == "Mainboard fans").Detail);
        Assert.Contains("1 output on", checks.Single(c => c.Title == "Graphics card fans").Detail);
    }

    [Fact]
    public void Another_fan_program_is_a_warning_that_names_it()
    {
        using var pc = new SimulatedPc();
        var checks = SystemCheck.Run(pc, KeySensors.Detect(pc.Read()), pawnIo: true, otherTools: ["Fan Control"]);

        var tools = checks.Single(c => c.Title == "Other fan programs");
        Assert.Equal(CheckResult.Warning, tools.Result);
        Assert.Contains("Fan Control", tools.Detail);
    }
}
