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

VerifyPageMappedBus();
VerifySaturnSystemMap();

Console.WriteLine("SystemRegisIII smoke passed.");

static void VerifyPageMappedBus()
{
    var rom = new RomDevice("Test ROM", [0x12, 0x34, 0x56, 0x78]);
    var ram = new ByteArrayMemory("Test RAM", 0x2000);
    var bus = new SaturnAddressMapBuilder()
        .Map(0x0000_0000, 0x0000_0003, rom)
        .Map(0x0600_0FF0, 0x0600_1FFF, ram)
        .Map(0x6000_0FF0, 0x6000_1FFF, ram)
        .Build();

    Require(bus.ReadLong(0x0000_0000) == 0x1234_5678, "ROM long read failed.");
    Require(bus.ReadWord(0x2000_0000) == 0x1234, "SH-2 BIOS alias read failed.");

    bus.WriteWord(0x6000_0FFE, 0xCAFE);
    Require(bus.ReadWord(0x6000_0FFE) == 0xCAFE, "Partial page RAM write failed.");
    bus.WriteLong(0x4600_0FF0, 0x1122_3344);
    Require(bus.ReadLong(0x0600_0FF0) == 0x1122_3344, "SH-2 high RAM cache-through alias failed.");

    try
    {
        _ = new SaturnAddressMapBuilder()
            .Map(0x0000_0000, 0x0000_00FF, rom)
            .Map(0x0000_0080, 0x0000_00FF, ram)
            .Build();
        throw new InvalidOperationException("Overlapping bus regions were accepted.");
    }
    catch (InvalidOperationException)
    {
    }
}

static void VerifySaturnSystemMap()
{
    var bios = new BiosImage("test-bios", [0x20, 0x00, 0x02, 0x00, 0x06, 0x00, 0x20, 0x00]);
    var systemMap = SaturnSystemMap.CreateBringup(bios);

    Require(systemMap.Bus.ReadLong(0x0000_0000) == 0x2000_0200, "Bringup BIOS mapping failed.");
    systemMap.Bus.WriteLong(0x6000_0000, 0xDEAD_BEEF);
    Require(systemMap.Bus.ReadLong(0x0600_0000) == 0xDEAD_BEEF, "High RAM alias mapping failed.");
    Require(
        systemMap.Bus.TryResolve(0x0010_0000, out var region, out _) &&
        region.Device.Name == "SMPC Registers",
        "SMPC stub mapping failed.");
    systemMap.Bus.WriteByte(0x0010_0000, 0x80);
    Require(systemMap.Stubs.Any(static stub => stub.Name == "SMPC Registers" && stub.WriteCount == 1), "Stub counters failed.");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class StubSh2(string name) : ISh2Cpu
{
    public string Name { get; } = name;
    public Sh2Registers Registers { get; } = new();

    public void Reset()
    {
        Registers.ProgramCounter = 0;
        Registers.StatusRegister = 0;
        Registers.ProcedureRegister = 0;
        Registers.GlobalBaseRegister = 0;
        Registers.VectorBaseRegister = 0;
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
