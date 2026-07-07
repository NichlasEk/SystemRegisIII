using SystemRegisIII.Core.Core.Bus;
using SystemRegisIII.Core.Core.CdBlock;
using SystemRegisIII.Core.Core.Cpu.Sh2;
using SystemRegisIII.Core.Core.Memory;
using SystemRegisIII.Core.Core.Scsp;
using SystemRegisIII.Core.Core.Vdp1;
using SystemRegisIII.Core.Core.Vdp2;
using SystemRegisIII.Core.Host.Audio;
using SystemRegisIII.Core.Host.Input;
using SystemRegisIII.Core.Host.Timing;
using SystemRegisIII.Core.Host.Video;

namespace SystemRegisIII.Core.Core;

public sealed class SaturnCore
{
    private const long CpuSliceCycles = 1024;

    private readonly IReadOnlyList<IClockedDevice> _devices;

    public SaturnCore(
        ISh2Cpu masterSh2,
        ISh2Cpu slaveSh2,
        ISaturnBus bus,
        IMainMemory memory,
        IVdp1 vdp1,
        IVdp2 vdp2,
        IScsp scsp,
        ICdBlock cdBlock,
        IVideoSink video,
        IAudioSink audio,
        IInputSource input,
        IHostClock clock)
    {
        MasterSh2 = masterSh2;
        SlaveSh2 = slaveSh2;
        Bus = bus;
        Memory = memory;
        Vdp1 = vdp1;
        Vdp2 = vdp2;
        Scsp = scsp;
        CdBlock = cdBlock;
        Video = video;
        Audio = audio;
        Input = input;
        Clock = clock;

        _devices =
        [
            Vdp1,
            Vdp2,
            Scsp,
            CdBlock,
        ];
    }

    public ISh2Cpu MasterSh2 { get; }
    public ISh2Cpu SlaveSh2 { get; }
    public ISaturnBus Bus { get; }
    public IMainMemory Memory { get; }
    public IVdp1 Vdp1 { get; }
    public IVdp2 Vdp2 { get; }
    public IScsp Scsp { get; }
    public ICdBlock CdBlock { get; }
    public IVideoSink Video { get; }
    public IAudioSink Audio { get; }
    public IInputSource Input { get; }
    public IHostClock Clock { get; }

    public void Reset()
    {
        Memory.Clear();

        foreach (var device in _devices)
        {
            device.Reset();
        }
    }

    public void StepFrame()
    {
        var budget = Clock.GetFrameBudget();
        StepCpus(budget);

        foreach (var device in _devices)
        {
            device.Step(budget);
        }
    }

    private void StepCpus(SaturnCycleBudget frameBudget)
    {
        var remaining = frameBudget;
        while (remaining.HasCycles)
        {
            var slice = remaining.TakeSlice(CpuSliceCycles);
            if (slice.MasterCycles > 0)
            {
                MasterSh2.Step(new SaturnCycleBudget(slice.MasterCycles, 0));
            }

            if (slice.SlaveCycles > 0)
            {
                SlaveSh2.Step(new SaturnCycleBudget(0, slice.SlaveCycles));
            }

            remaining = remaining.Subtract(slice);
        }
    }
}
