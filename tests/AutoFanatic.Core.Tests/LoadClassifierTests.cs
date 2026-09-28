using AutoFanatic.Core.Analysis;

namespace AutoFanatic.Core.Tests;

public class LoadClassifierTests
{
    [Theory]
    [InlineData(5, 3, LoadClass.Idle)]
    [InlineData(20, 5, LoadClass.Light)]
    [InlineData(10, 40, LoadClass.Light)]
    [InlineData(35, 97, LoadClass.GpuHeavy)]
    [InlineData(90, 20, LoadClass.CpuHeavy)]
    [InlineData(70, 95, LoadClass.Combined)]
    public void Classifies_by_cpu_and_gpu_load(double cpu, double gpu, LoadClass expected) =>
        Assert.Equal(expected, LoadClassifier.Classify(cpu, gpu));

    [Fact]
    public void Unknown_without_both_loads()
    {
        Assert.Null(LoadClassifier.Classify(null, 50));
        Assert.Null(LoadClassifier.Classify(50, null));
    }
}
