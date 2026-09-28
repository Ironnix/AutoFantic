using System.Diagnostics;
using System.Runtime.Intrinsics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AutoFanatic.Core.Load;

/// <summary>
/// The built-in load for the quick calibration ("no stress-test tools needed"): the CPU through
/// worker threads doing vector math, the GPU through a small DirectX compute shader. Both stay
/// steady, so power stays steady, which a real game can't promise (see the CS2 log).
///
/// The CPU share is a number of fully busy threads, not threads that pause in between: Windows
/// counts CPU time per timer tick, and short pauses make both the load and its measurement uneven.
/// </summary>
public sealed class TestLoad : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Thread> _threads = [];

    /// <param name="cpuShare">0–1: share of the logical processors kept busy (0 = no CPU load).</param>
    /// <param name="gpuShare">0–1: how busy the GPU is (0 = no GPU load).</param>
    public TestLoad(double cpuShare, double gpuShare)
    {
        if (cpuShare > 0)
        {
            int workers = Math.Max(1, (int)Math.Round(Environment.ProcessorCount * Math.Clamp(cpuShare, 0, 1)));
            for (int i = 0; i < workers; i++)
                Start($"cpu load {i}", () => BurnCpu(_stop.Token), ThreadPriority.BelowNormal);
        }

        if (gpuShare > 0)
            Start("gpu load", () => RunGpu(Math.Clamp(gpuShare, 0.05, 1), _stop.Token), ThreadPriority.AboveNormal);
    }

    /// <summary>Name of the graphics card under load, once it runs.</summary>
    public string? GpuName { get; private set; }

    /// <summary>Why the GPU load couldn't start (no DirectX 11 device, shader error …); null if fine.</summary>
    public string? GpuError { get; private set; }

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var thread in _threads)
            thread.Join(TimeSpan.FromSeconds(5));
        _stop.Dispose();
    }

    private void Start(string name, ThreadStart work, ThreadPriority priority)
    {
        var thread = new Thread(work) { IsBackground = true, Name = name, Priority = priority };
        _threads.Add(thread);
        thread.Start();
    }

    private static void BurnCpu(CancellationToken stop)
    {
        var a = Vector256.Create(1.0001f);
        var b = Vector256.Create(0.0001f);
        var v = Vector256.Create(1f);

        while (!stop.IsCancellationRequested)
        {
            for (int i = 0; i < 4096; i++)
                v = v * a + b;
            if (v[0] > 1e30f)
                v = Vector256.Create(1f); // keep it finite, and keep the JIT from dropping the loop
        }
    }

    // A pure arithmetic loop per thread; each dispatch is short (tens of ms), far below the
    // 2-second limit after which Windows would reset the graphics driver.
    private const string Shader = """
        RWStructuredBuffer<float4> data : register(u0);

        [numthreads(256, 1, 1)]
        void burn(uint3 id : SV_DispatchThreadID)
        {
            float4 v = data[id.x] + float4(1, 2, 3, 4);
            float4 a = float4(1.0001, 0.9999, 1.0002, 0.9998);
            float4 b = float4(0.0001, 0.0002, 0.0003, 0.0004);
            [loop]
            for (int i = 0; i < 1024; i++)
            {
                v = v * a + b;
                v = v * a - b;
            }
            data[id.x] = v;
        }
        """;

    private const uint Elements = 1 << 20;

    private void RunGpu(double share, CancellationToken stop)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            using var adapter = PickAdapter(factory);
            GpuName = adapter.Description1.Description;

            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None, [FeatureLevel.Level_11_0], out ID3D11Device? created, out ID3D11DeviceContext? createdContext).CheckError();
            using var device = created!;
            using var context = createdContext!;

            var bytecode = Compiler.Compile(Shader, "burn", "autofanatic-load.hlsl", "cs_5_0");
            using var shader = device.CreateComputeShader(bytecode.Span);
            using var buffer = device.CreateBuffer(new BufferDescription(
                Elements * 16, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16));
            using var view = device.CreateUnorderedAccessView(buffer, new UnorderedAccessViewDescription(buffer, Format.Unknown, 0, Elements));
            using var done = device.CreateQuery(new QueryDescription(QueryType.Event));

            context.CSSetShader(shader);
            context.CSSetUnorderedAccessView(0, view);

            var watch = new Stopwatch();
            while (!stop.IsCancellationRequested)
            {
                watch.Restart();
                context.Dispatch(Elements / 256, 1, 1);
                context.End(done);
                context.Flush();
                while (!context.IsDataAvailable(done) && !stop.IsCancellationRequested)
                    Thread.Yield();

                if (share < 1)
                {
                    var busy = watch.Elapsed;
                    var rest = busy * ((1 - share) / share);
                    stop.WaitHandle.WaitOne(rest);
                }
            }
        }
        catch (Exception ex)
        {
            GpuError = ex.Message;
        }
    }

    // the card with the most video memory: the discrete GPU, not the one built into the CPU
    private static IDXGIAdapter1 PickAdapter(IDXGIFactory1 factory)
    {
        IDXGIAdapter1? best = null;
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success; i++)
        {
            var description = adapter!.Description1;
            bool software = (description.Flags & AdapterFlags.Software) != 0;
            if (!software && (best is null || Memory(description) > Memory(best.Description1)))
            {
                best?.Dispose();
                best = adapter;
            }
            else
            {
                adapter.Dispose();
            }
        }
        return best ?? throw new InvalidOperationException("No graphics card found for the GPU load.");
    }

    // 64-bit on purpose: the default conversion is 32-bit and overflows on cards with more than 4 GB
    private static ulong Memory(AdapterDescription1 description) => (ulong)(nuint)description.DedicatedVideoMemory;
}
