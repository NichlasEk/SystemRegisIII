using SystemRegisIII.Core.Core;
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

var core = new SaturnCore(
    new StubSh2("Master SH-2"),
    new StubSh2("Slave SH-2"),
    new StubBus(),
    new StubMemory(2 * 1024 * 1024),
    new StubVdp1(),
    new StubVdp2(),
    new StubScsp(),
    new StubCdBlock(),
    new NullVideoSink(),
    new NullAudioSink(),
    new NullInputSource(),
    new FixedHostClock(new SaturnCycleBudget(536_931)));

core.Reset();
core.StepFrame();

Console.WriteLine("SystemRegisIII smoke passed.");

internal sealed class StubSh2(string name) : ISh2Cpu
{
    public string Name { get; } = name;
    public Sh2Registers Registers { get; } = new();

    public void Reset()
    {
        Registers.ProgramCounter = 0;
        Registers.StatusRegister = 0;
        Array.Clear(Registers.General);
    }

    public void Step(SaturnCycleBudget budget)
    {
        Registers.ProgramCounter += (uint)(budget.MasterCycles & 0xffff);
    }
}

internal sealed class StubBus : ISaturnBus
{
    public byte ReadByte(uint address) => 0;
    public ushort ReadWord(uint address) => 0;
    public uint ReadLong(uint address) => 0;
    public void WriteByte(uint address, byte value) { }
    public void WriteWord(uint address, ushort value) { }
    public void WriteLong(uint address, uint value) { }
}

internal sealed class StubMemory(int sizeBytes) : IMainMemory
{
    private readonly byte[] _memory = new byte[sizeBytes];

    public int SizeBytes => _memory.Length;
    public Span<byte> Span => _memory;

    public void Clear()
    {
        Array.Clear(_memory);
    }
}

internal sealed class StubVdp1 : StubDevice, IVdp1
{
    public override string Name => "VDP1";
}

internal sealed class StubVdp2 : StubDevice, IVdp2
{
    public override string Name => "VDP2";
}

internal sealed class StubScsp : StubDevice, IScsp
{
    public override string Name => "SCSP";
}

internal sealed class StubCdBlock : StubDevice, ICdBlock
{
    public override string Name => "CD Block";
}

internal abstract class StubDevice : IClockedDevice
{
    public abstract string Name { get; }

    public void Reset() { }

    public void Step(SaturnCycleBudget budget) { }
}

internal sealed class NullVideoSink : IVideoSink
{
    public void Present(VideoFrame frame) { }
}

internal sealed class NullAudioSink : IAudioSink
{
    public void Submit(ReadOnlySpan<float> interleavedStereoSamples) { }
}

internal sealed class NullInputSource : IInputSource
{
    public SaturnInputState Poll() => SaturnInputState.None;
}

internal sealed class FixedHostClock(SaturnCycleBudget frameBudget) : IHostClock
{
    public SaturnCycleBudget GetFrameBudget() => frameBudget;
}
