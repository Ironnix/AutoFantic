using AutoFantic.Core.Hardware;

namespace AutoFantic.Core.Tests;

public class KeySensorsTests
{
    private static SensorReading R(string id, string hardware, string type, SensorKind kind, string name, float value = 50) =>
        new(id, hardware, type, kind, name, value);

    [Fact]
    public void Picks_the_discrete_nvidia_card_over_the_integrated_radeon()
    {
        var snapshot = new Snapshot(DateTimeOffset.Now,
        [
            R("/amdcpu/0/temperature/2", "AMD Ryzen 7 7800X3D", "Cpu", SensorKind.Temperature, "Core (Tctl/Tdie)"),
            R("/amdcpu/0/power/0", "AMD Ryzen 7 7800X3D", "Cpu", SensorKind.Power, "Package"),
            R("/gpu-amd/0/temperature/0", "AMD Radeon(TM) Graphics", "GpuAmd", SensorKind.Temperature, "GPU Core"),
            R("/gpu-nvidia/0/temperature/0", "NVIDIA GeForce RTX 4080", "GpuNvidia", SensorKind.Temperature, "GPU Core"),
            R("/gpu-nvidia/0/temperature/2", "NVIDIA GeForce RTX 4080", "GpuNvidia", SensorKind.Temperature, "GPU Hot Spot"),
            R("/gpu-nvidia/0/temperature/3", "NVIDIA GeForce RTX 4080", "GpuNvidia", SensorKind.Temperature, "GPU Memory Junction"),
            R("/gpu-nvidia/0/power/0", "NVIDIA GeForce RTX 4080", "GpuNvidia", SensorKind.Power, "GPU Package"),
        ]);

        var keys = KeySensors.Detect(snapshot);

        Assert.Equal("/amdcpu/0/temperature/2", keys.CpuTemp);
        Assert.Equal("/amdcpu/0/power/0", keys.CpuPower);
        Assert.Equal("/gpu-nvidia/0/temperature/0", keys.GpuTemp);
        Assert.Equal("/gpu-nvidia/0/temperature/2", keys.GpuHotspot);
        Assert.Equal("/gpu-nvidia/0/temperature/3", keys.GpuMemory);
        Assert.Equal("/gpu-nvidia/0/power/0", keys.GpuPower);
    }

    [Fact]
    public void Picks_the_amd_card_with_a_hotspot_and_intel_package_temperature()
    {
        var snapshot = new Snapshot(DateTimeOffset.Now,
        [
            R("/intelcpu/0/temperature/0", "Intel Core i7-14700K", "Cpu", SensorKind.Temperature, "Core Max"),
            R("/intelcpu/0/temperature/1", "Intel Core i7-14700K", "Cpu", SensorKind.Temperature, "CPU Package"),
            R("/gpu-amd/0/temperature/0", "AMD Radeon(TM) Graphics", "GpuAmd", SensorKind.Temperature, "GPU Core"),
            R("/gpu-amd/1/temperature/0", "AMD Radeon RX 7900 XTX", "GpuAmd", SensorKind.Temperature, "GPU Core"),
            R("/gpu-amd/1/temperature/1", "AMD Radeon RX 7900 XTX", "GpuAmd", SensorKind.Temperature, "GPU Hot Spot"),
            R("/gpu-amd/1/temperature/2", "AMD Radeon RX 7900 XTX", "GpuAmd", SensorKind.Temperature, "GPU Memory"),
        ]);

        var keys = KeySensors.Detect(snapshot);

        Assert.Equal("/intelcpu/0/temperature/1", keys.CpuTemp);
        Assert.Equal("/gpu-amd/1/temperature/0", keys.GpuTemp);
        Assert.Equal("/gpu-amd/1/temperature/1", keys.GpuHotspot);
        Assert.Equal("/gpu-amd/1/temperature/2", keys.GpuMemory);
        Assert.Null(keys.GpuPower);
    }
}
