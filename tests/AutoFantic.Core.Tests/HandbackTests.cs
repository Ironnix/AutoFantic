using AutoFantic.Core.Hardware;
using AutoFantic.Core.Logging;
using AutoFantic.Core.Simulation;

namespace AutoFantic.Core.Tests;

public sealed class HandbackTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "autofantic-handback-" + Guid.NewGuid().ToString("N"));

    public HandbackTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>Shaped like the library's Nuvoton chip: what it remembers of the BIOS, per fan output.</summary>
    private sealed class Nct677X
    {
#pragma warning disable CS0414, IDE0044 // read and written by reflection, like the real chip's
        private byte[] _initialFanControlMode = new byte[7];
        private byte[] _initialFanPwmCommand = new byte[7];
        private bool[] _restoreDefaultFanControlRequired = new bool[7];
        private byte[] _fanRpmRegister = new byte[7]; // not part of the memory
#pragma warning restore CS0414, IDE0044

        public void TakeOver(int output, byte mode, byte pwm)
        {
            _initialFanControlMode[output] = mode;
            _initialFanPwmCommand[output] = pwm;
            _restoreDefaultFanControlRequired[output] = true;
            _fanRpmRegister[output] = 99;
        }

        public (byte Mode, byte Pwm, bool Required, byte Rpm) Memory(int output) =>
            (_initialFanControlMode[output], _initialFanPwmCommand[output], _restoreDefaultFanControlRequired[output], _fanRpmRegister[output]);
    }

    private sealed class IT87XX
    {
#pragma warning disable CS0414, IDE0044
        private byte[] _initialFanPwmControl = new byte[7];
        private bool[] _restoreDefaultFanPwmControlRequired = new bool[7];
#pragma warning restore CS0414, IDE0044
    }

    [Fact]
    public void A_fan_chips_memory_of_the_bios_survives_in_the_file_and_goes_back_into_a_fresh_chip()
    {
        var running = new Nct677X();
        running.TakeOver(0, mode: 0, pwm: 110); // CPU fan: BIOS mode, 110/255
        running.TakeOver(5, mode: 0, pwm: 80);

        var state = ChipMemory.Capture("/lpc/nct6686d/0", running)!;
        new HandbackFile(1234, DateTimeOffset.Now, DateTimeOffset.Now, ["/lpc/nct6686d/0/control/0"], [state]).Save(Path.Combine(_folder, "f.json"));
        var loaded = HandbackFile.Load(Path.Combine(_folder, "f.json"))!;

        var fresh = new Nct677X(); // what a new process sees: it remembers nothing
        Assert.True(ChipMemory.Inject(fresh, loaded.Chips[0]));

        Assert.Equal((0, 110, true, 0), fresh.Memory(0));
        Assert.Equal((0, 80, true, 0), fresh.Memory(5));
        Assert.Equal((0, 0, false, 0), fresh.Memory(1));  // never taken over: nothing to restore
        Assert.DoesNotContain("_fanRpmRegister", state.Fields.Keys);
    }

    [Fact]
    public void Memory_is_only_put_back_into_the_same_kind_of_chip()
    {
        var state = ChipMemory.Capture("/lpc/nct6686d/0", new Nct677X())!;

        Assert.False(ChipMemory.Inject(new IT87XX(), state));
        Assert.Null(ChipMemory.Capture("/gpu", new object())); // nothing it knows how to read
    }

    [Fact]
    public void The_file_exists_only_while_fans_are_driven()
    {
        using var pc = new SimulatedPc();
        string path = Handback.PathIn(_folder);
        pc.HandbackPath = path;

        pc.SetPercent(pc.Channels[0], 50);
        pc.SetPercent(pc.Channels[3], 60);
        pc.SetPercent(pc.Channels[0], 55); // no new fan: nothing to write

        var file = HandbackFile.Load(path)!;
        Assert.Equal(["/sim/gpu/control/0", "/sim/superio/control/0"], file.Channels.Order());
        Assert.Equal(Environment.ProcessId, file.ProcessId);
        Assert.True(file.OwnerRunning());

        pc.RestoreAll();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void After_a_crash_the_next_start_hands_the_fans_back_and_says_so()
    {
        // a process that no longer exists left its file behind
        new HandbackFile(2_000_000_000, DateTimeOffset.Now.AddMinutes(-30), DateTimeOffset.Now.AddMinutes(-20), ["/sim/superio/control/1", "/sim/gone/control/9"], [])
            .Save(Path.Combine(_folder, HandbackFile.NameFor(2_000_000_000)));

        using var pc = new SimulatedPc();
        pc.SetPercent(pc.Channels[1], 100); // stuck where the crashed run left it
        var log = ActivityLog.InMemoryOnly();

        var entry = Handback.RecoverIfNeeded(_folder, pc, log, "AutoFantic at start");

        Assert.NotNull(entry);
        Assert.Equal(LogKind.Watchdog, entry.Kind);
        Assert.Contains("Case Fans: back to BIOS", entry.Text);
        Assert.Contains("/sim/gone/control/9: not found", entry.Text);
        Assert.False(pc.Channels[1].IsSoftwareControlled);
        Assert.False(Handback.AnyIn(_folder));
        Assert.Null(Handback.RecoverIfNeeded(_folder, pc, log, "again")); // done: nothing left to do
    }

    [Fact]
    public void Fans_of_a_process_that_still_runs_are_left_alone()
    {
        HandbackFile.ForThisProcess(["/sim/superio/control/0"], [], DateTimeOffset.Now).Save(Handback.PathIn(_folder));
        using var pc = new SimulatedPc();

        Assert.Null(Handback.RecoverIfNeeded(_folder, pc, ActivityLog.InMemoryOnly(), "the watchdog"));
        Assert.True(File.Exists(Handback.PathIn(_folder)));
    }
}
