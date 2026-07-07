using SystemRegisIII.Core.Core;
using SystemRegisIII.Core.Core.Bus;
using SystemRegisIII.Core.Core.CdBlock;
using SystemRegisIII.Core.Core.Cpu.Sh2;
using SystemRegisIII.Core.Core.Memory;
using SystemRegisIII.Core.Core.Scu;
using SystemRegisIII.Core.Core.Smpc;
using SystemRegisIII.Core.Core.Scsp;
using SystemRegisIII.Core.Core.Vdp1;
using SystemRegisIII.Core.Core.Vdp2;
using SystemRegisIII.Core.Host.Audio;
using SystemRegisIII.Core.Host.Input;
using SystemRegisIII.Core.Host.Timing;
using SystemRegisIII.Core.Host.Video;

var masterSh2 = new StubSh2("Master SH-2");
var slaveSh2 = new StubSh2("Slave SH-2");
var core = new SaturnCore(
    masterSh2,
    slaveSh2,
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
Require(masterSh2.StepCount > 1, "Master SH-2 was not frame-sliced.");
Require(slaveSh2.StepCount == masterSh2.StepCount, "Slave SH-2 was not interleaved with master.");
Require(masterSh2.TotalCycles == 536_931, "Master SH-2 did not receive the full frame budget.");
Require(slaveSh2.TotalCycles == 536_931, "Slave SH-2 did not receive the full frame budget.");

VerifyPageMappedBus();
VerifySaturnSystemMap();
VerifySh2InternalRegisterBus();
VerifySh2InterruptEntry();
VerifySh2IndexedMoveDecoding();
VerifySh2BiosBringupInstructions();
VerifySh2BranchAndExceptionInstructions();

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
    Require(
        systemMap.Bus.TryResolve(0x2589_0018, out region, out _) &&
        region.Device.Name == "CD Block Register Mirror",
        "CD Block register mirror stub mapping failed.");
    Require(
        systemMap.Bus.TryResolve(0x24FF_FFFF, out region, out _) &&
        region.Device.Name == "A-Bus Probe Area",
        "A-Bus probe area mapping failed.");
    Require(systemMap.Bus.ReadWord(0x2589_0018) == 0x0043, "CD Block ID word 0 failed.");
    Require(systemMap.Bus.ReadWord(0x2589_001C) == 0x4442, "CD Block ID word 1 failed.");
    Require(systemMap.Bus.ReadWord(0x2589_0020) == 0x4C4F, "CD Block ID word 2 failed.");
    Require(systemMap.Bus.ReadWord(0x2589_0024) == 0x434B, "CD Block ID word 3 failed.");
    systemMap.Bus.WriteWord(0x2589_0008, 0x0BE1);
    Require(systemMap.Bus.ReadWord(0x2589_0008) == 0x0001, "CD Block HIRQ CMOK bit failed.");
    Require(systemMap.Bus.ReadWord(0x2589_0018) == 0x2000, "CD Block status word 0 failed.");
    Require(systemMap.Bus.ReadWord(0x2589_001C) == 0x0000, "CD Block status word 1 failed.");
    systemMap.Bus.WriteWord(0x2589_0008, 0x0000);
    Require(systemMap.Bus.ReadWord(0x2589_0008) == 0x0000, "CD Block HIRQ clear failed.");
    systemMap.Bus.WriteWord(0x2589_0018, 0x0100);
    systemMap.Bus.WriteWord(0x2589_001C, 0x0000);
    systemMap.Bus.WriteWord(0x2589_0020, 0x0000);
    systemMap.Bus.WriteWord(0x2589_0024, 0x0000);
    Require(systemMap.Bus.ReadWord(0x2589_0008) == 0x0001, "CD Block command completion HIRQ failed.");
    Require(systemMap.Bus.ReadWord(0x2589_0018) == 0x0700, "CD Block hardware info status failed.");
    Require(systemMap.Bus.ReadWord(0x2589_001C) == 0x0201, "CD Block hardware info flags failed.");
    Require(systemMap.Bus.ReadWord(0x2589_0020) == 0x0000, "CD Block MPEG version failed.");
    Require(systemMap.Bus.ReadWord(0x2589_0024) == 0x0400, "CD Block drive info failed.");
    var cdRegisters = systemMap.Stubs.OfType<CdBlockRegisterBusDevice>().Single();
    Require(cdRegisters.LastCommandCr1 == 0x0100, "CD Block CR1 command latch failed.");
    Require(cdRegisters.LastCommandCode == 0x01, "CD Block command code latch failed.");
    systemMap.Bus.WriteWord(0x2589_0018, 0x0000);
    systemMap.Bus.WriteWord(0x2589_001C, 0x0000);
    systemMap.Bus.WriteWord(0x2589_0020, 0x0000);
    systemMap.Bus.WriteWord(0x2589_0024, 0x0000);
    Require(systemMap.Bus.ReadWord(0x2589_0018) == 0x0700, "CD Block current status failed.");
    Require(cdRegisters.LastCommandCode == 0x00, "CD Block current status command latch failed.");
    systemMap.Bus.WriteLong(0x25A0_0000, 0x0000_A000);
    Require(systemMap.Bus.ReadLong(0x25A0_0000) == 0x0000_A000, "SCSP register write-back failed.");
    systemMap.Bus.WriteByte(0x0010_0000, 0x80);
    Require(systemMap.Stubs.Any(static stub => stub.Name == "SMPC Registers" && stub.WriteCount == 1), "Stub counters failed.");
    systemMap.Bus.WriteByte(0x0010_001F, 0x02);
    var smpcRegisters = systemMap.Stubs.OfType<SmpcRegisterBusDevice>().Single();
    Require(smpcRegisters.LastCommand == 0x02, "SMPC command latch failed.");
    Require(smpcRegisters.SlaveSh2Enabled, "SMPC SSHON command failed.");
    systemMap.Bus.WriteByte(0x0010_001F, 0x10);
    Require(smpcRegisters.TryConsumeInterrupt(), "SMPC INTBACK interrupt latch failed.");
    var scuRegisters = systemMap.Stubs.OfType<ScuRegisterBusDevice>().Single();
    scuRegisters.RaiseVBlankIn();
    Require(!scuRegisters.HasPendingVBlankIn, "SCU VBlank interrupt ignored reset mask failed.");
    scuRegisters.RaiseVBlankOut();
    Require(!scuRegisters.HasPendingVBlankOut, "SCU VBlank-OUT interrupt ignored reset mask failed.");
    scuRegisters.RaiseSmpc();
    Require(!scuRegisters.HasPendingSmpc, "SCU SMPC interrupt ignored reset mask failed.");
    systemMap.Bus.WriteLong(0x25FE_00A0, 0xFFFF_FFFC);
    Require(systemMap.Bus.ReadLong(0x25FE_00A0) == 0xFFFF_FFFC, "SCU interrupt mask latch failed.");
    Require(scuRegisters.HasPendingVBlankIn, "SCU VBlank interrupt pending failed.");
    Require(scuRegisters.HasPendingVBlankOut, "SCU VBlank-OUT interrupt pending failed.");
    systemMap.Bus.WriteLong(0x25FE_00A0, 0xFFFF_FF7C);
    Require(scuRegisters.HasPendingSmpc, "SCU SMPC interrupt pending failed.");
    systemMap.Bus.WriteLong(0x25FE_00A4, 0xFFFF_FF7C);
    Require(systemMap.Bus.ReadLong(0x25FE_00A4) == 0x0000_0000, "SCU interrupt status clear failed.");

    var simulatedMap = SaturnSystemMap.CreateBringup(
        bios,
        new SaturnBringupOptions { SimulateSlaveReady = true });
    simulatedMap.Bus.WriteLong(0x4600_0240, 0);
    Require(simulatedMap.Bus.ReadLong(0x0600_0240) == 0x3252_4459, "Slave-ready bringup overlay failed.");
}

static void VerifySh2InternalRegisterBus()
{
    var ram = new ByteArrayMemory("Shared RAM", 0x1000);
    var externalBus = new SaturnAddressMapBuilder()
        .Map(0x0600_0000, 0x0600_0FFF, ram)
        .Build();
    var masterBus = new Sh2InternalRegisterBus(externalBus, Sh2CpuRole.Master);
    var slaveBus = new Sh2InternalRegisterBus(externalBus, Sh2CpuRole.Slave);

    Require(masterBus.ReadLong(0xFFFF_FFE0) == 0, "Master SH-2 CPU-id register failed.");
    Require(slaveBus.ReadLong(0xFFFF_FFE0) == 0x2000_0000, "Slave SH-2 CPU-id register failed.");

    masterBus.WriteLong(0x0600_0000, 0x1122_3344);
    Require(slaveBus.ReadLong(0x0600_0000) == 0x1122_3344, "SH-2 internal bus did not share external RAM.");

    masterBus.WriteByte(0xFFFF_FE92, 0x40);
    Require(masterBus.ReadByte(0xFFFF_FE92) == 0x40, "SH-2 internal latch failed.");
    Require(slaveBus.ReadByte(0xFFFF_FE92) == 0, "SH-2 internal latch leaked across CPUs.");
}

static void VerifySh2InterruptEntry()
{
    var ram = new ByteArrayMemory("RAM", 0x2000);
    var bus = new SaturnAddressMapBuilder()
        .Map(0x0000_0000, 0x0000_1FFF, ram)
        .Build();
    bus.WriteLong(0x0000_0000, 0x0000_0100);
    bus.WriteLong(0x0000_0004, 0x0000_0000);
    bus.WriteWord(0x0000_0100, 0x0009);
    bus.WriteLong(0x40 * 4, 0x0000_0800);

    var cpu = new Sh2Cpu("IRQ SH-2", bus, 0x0000_0000);
    cpu.Reset();
    cpu.Registers.General[15] = 0x0000_1000;
    cpu.StepInstruction();
    Require(cpu.Registers.ProgramCounter == 0x0000_0102, "SH-2 interrupt setup failed.");
    Require(cpu.RequestInterrupt(15, 0x40), "SH-2 interrupt request was not accepted.");
    Require(cpu.Registers.ProgramCounter == 0x0000_0800, "SH-2 interrupt vector failed.");
    Require(cpu.Registers.General[15] == 0x0000_0FF8, "SH-2 interrupt stack pointer failed.");
    Require(bus.ReadLong(0x0000_0FF8) == 0x0000_0102, "SH-2 interrupt stacked PC failed.");
    Require(bus.ReadLong(0x0000_0FFC) == 0x0000_0000, "SH-2 interrupt stacked SR failed.");
    Require(cpu.Registers.InterruptLevelMask == 15, "SH-2 interrupt level mask failed.");
    Require(!cpu.RequestInterrupt(14, 0x40), "SH-2 interrupt mask accepted lower priority.");
}

static void VerifySh2IndexedMoveDecoding()
{
    var code = new ByteArrayMemory("Code RAM", 0x100);
    var data = new ByteArrayMemory("Data RAM", 0x100);
    WriteLong(code, 0x00, 0x0000_0008);
    WriteLong(code, 0x04, 0);
    WriteWord(code, 0x08, 0x0000);

    var bus = new SaturnAddressMapBuilder()
        .Map(0x0000_0000, 0x0000_00FF, code)
        .Map(0x0600_0000, 0x0600_00FF, data)
        .Build();
    var cpu = new Sh2Cpu("Test SH-2", bus, 0x0000_0000);
    cpu.Reset();
    cpu.StepInstruction();
    Require(cpu.FirstUnimplementedOpcode == 0x0000, "SH-2 opcode 0x0000 was decoded as a valid indexed move.");

    WriteWord(code, 0x08, 0x0165);
    cpu.Reset();
    cpu.Registers.General[0] = 4;
    cpu.Registers.General[1] = 0x0600_0000;
    cpu.Registers.General[6] = 0xCAFE;
    cpu.StepInstruction();
    Require(ReadWord(data, 4) == 0xCAFE, "SH-2 MOV.W Rm,@(R0,Rn) failed.");
}

static void VerifySh2BiosBringupInstructions()
{
    var code = new ByteArrayMemory("Code RAM", 0x100);
    var data = new ByteArrayMemory("Data RAM", 0x100);
    var bus = new SaturnAddressMapBuilder()
        .Map(0x0000_0000, 0x0000_00FF, code)
        .Map(0x0600_0000, 0x0600_00FF, data)
        .Build();
    var cpu = new Sh2Cpu("Test SH-2", bus, 0x0000_0000);

    WriteLong(code, 0x00, 0x0000_0008);
    WriteLong(code, 0x04, 0);
    WriteLong(data, 0x08, 0x1122_3344);
    WriteWord(code, 0x08, 0x5122);
    cpu.Reset();
    cpu.Registers.General[2] = 0x0600_0000;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0x1122_3344, "SH-2 MOV.L @(disp,Rm),Rn failed.");

    WriteWord(code, 0x08, 0x9300);
    WriteWord(code, 0x0C, 0xFF80);
    cpu.Reset();
    cpu.StepInstruction();
    Require(cpu.Registers.General[3] == 0xFFFF_FF80, "SH-2 MOV.W @(disp,PC),Rn failed.");

    WriteWord(code, 0x08, 0x8013);
    cpu.Reset();
    cpu.Registers.General[0] = 0xAB;
    cpu.Registers.General[1] = 0x0600_0000;
    cpu.StepInstruction();
    Require(data.ReadByte(3) == 0xAB, "SH-2 MOV.B R0,@(disp,Rn) failed.");

    WriteWord(data, 4, 0x8001);
    WriteWord(code, 0x08, 0x8512);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0600_0000;
    cpu.StepInstruction();
    Require(cpu.Registers.General[0] == 0xFFFF_8001, "SH-2 MOV.W @(disp,Rn),R0 failed.");

    WriteWord(code, 0x08, 0x2126);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0600_0008;
    cpu.Registers.General[2] = 0x5566_7788;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0x0600_0004, "SH-2 MOV.L Rm,@-Rn did not predecrement.");
    Require(ReadLong(data, 4) == 0x5566_7788, "SH-2 MOV.L Rm,@-Rn failed.");

    WriteWord(code, 0x08, 0x4122);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0600_0008;
    cpu.Registers.ProcedureRegister = 0xCAFEBABE;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0x0600_0004, "SH-2 STS.L PR,@-Rn did not predecrement.");
    Require(ReadLong(data, 4) == 0xCAFE_BABE, "SH-2 STS.L PR,@-Rn failed.");

    WriteWord(code, 0x08, 0x4113);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0600_0008;
    cpu.Registers.GlobalBaseRegister = 0x0602_0000;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0x0600_0004, "SH-2 STC.L GBR,@-Rn did not predecrement.");
    Require(ReadLong(data, 4) == 0x0602_0000, "SH-2 STC.L GBR,@-Rn failed.");

    WriteWord(code, 0x08, 0x3122);
    cpu.Reset();
    cpu.Registers.General[1] = 0x8000_0000;
    cpu.Registers.General[2] = 0x7FFF_FFFF;
    cpu.StepInstruction();
    Require(cpu.Registers.T, "SH-2 CMP/HS Rm,Rn failed unsigned comparison.");

    WriteWord(code, 0x08, 0x3126);
    cpu.Reset();
    cpu.Registers.General[1] = 0x8000_0000;
    cpu.Registers.General[2] = 0x7FFF_FFFF;
    cpu.StepInstruction();
    Require(cpu.Registers.T, "SH-2 CMP/HI Rm,Rn failed unsigned comparison.");

    WriteWord(code, 0x08, 0x3127);
    cpu.Reset();
    cpu.Registers.General[1] = 1;
    cpu.Registers.General[2] = 0xFFFF_FFFF;
    cpu.StepInstruction();
    Require(cpu.Registers.T, "SH-2 CMP/GT Rm,Rn failed signed comparison.");

    WriteWord(code, 0x08, 0x3123);
    cpu.Reset();
    cpu.Registers.General[1] = 0xFFFF_FFFF;
    cpu.Registers.General[2] = 0xFFFF_FFFE;
    cpu.StepInstruction();
    Require(cpu.Registers.T, "SH-2 CMP/GE Rm,Rn failed signed comparison.");

    WriteWord(code, 0x08, 0x4119);
    cpu.Reset();
    cpu.Registers.General[1] = 0x8123_4567;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0x0081_2345, "SH-2 SHLR8 failed.");

    WriteWord(code, 0x08, 0x4129);
    cpu.Reset();
    cpu.Registers.General[1] = 0x8123_4567;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0x0000_8123, "SH-2 SHLR16 failed.");

    WriteWord(code, 0x08, 0x3128);
    cpu.Reset();
    cpu.Registers.General[1] = 0x1000_0000;
    cpu.Registers.General[2] = 0x0000_0001;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0x0FFF_FFFF, "SH-2 SUB Rm,Rn failed.");

    WriteWord(code, 0x08, 0x4109);
    cpu.Reset();
    cpu.Registers.General[1] = 0x8000_0004;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0x2000_0001, "SH-2 SHLR2 failed.");

    WriteWord(code, 0x08, 0x4101);
    cpu.Reset();
    cpu.Registers.General[1] = 0x8000_0001;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0x4000_0000, "SH-2 SHLR failed.");
    Require(cpu.Registers.T, "SH-2 SHLR did not move bit 0 into T.");

    WriteWord(code, 0x08, 0x6127);
    cpu.Reset();
    cpu.Registers.General[2] = 0x00FF_00FF;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0xFF00_FF00, "SH-2 NOT Rm,Rn failed.");

    data.WriteByte(0x20, 0x01);
    WriteWord(code, 0x08, 0xCF80);
    cpu.Reset();
    cpu.Registers.GlobalBaseRegister = 0x0600_0010;
    cpu.Registers.General[0] = 0x10;
    cpu.StepInstruction();
    Require(data.ReadByte(0x20) == 0x81, "SH-2 OR.B #imm,@(R0,GBR) failed.");

    WriteWord(code, 0x08, 0xCC80);
    cpu.Reset();
    cpu.Registers.GlobalBaseRegister = 0x0600_0010;
    cpu.Registers.General[0] = 0x10;
    cpu.StepInstruction();
    Require(!cpu.Registers.T, "SH-2 TST.B #imm,@(R0,GBR) failed set-bit test.");
}

static void VerifySh2BranchAndExceptionInstructions()
{
    var code = new ByteArrayMemory("Code RAM", 0x100);
    var stack = new ByteArrayMemory("Stack RAM", 0x100);
    var bus = new SaturnAddressMapBuilder()
        .Map(0x0000_0000, 0x0000_00FF, code)
        .Map(0x0600_0000, 0x0600_00FF, stack)
        .Build();
    var cpu = new Sh2Cpu("Test SH-2", bus, 0x0000_0000);

    WriteLong(code, 0x00, 0x0000_0008);
    WriteLong(code, 0x04, 0);
    WriteWord(code, 0x08, 0x8D01);
    WriteWord(code, 0x0A, 0xE101);
    WriteWord(code, 0x0C, 0xE102);
    WriteWord(code, 0x0E, 0xE103);
    cpu.Reset();
    cpu.Registers.T = true;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 1, "SH-2 BT/S did not execute delay slot.");
    Require(cpu.Registers.ProgramCounter == 0x0000_000E, "SH-2 BT/S did not branch when T=true.");

    cpu.Reset();
    cpu.Registers.T = false;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 1, "SH-2 BT/S did not execute fallthrough delay slot.");
    Require(cpu.Registers.ProgramCounter == 0x0000_000C, "SH-2 BT/S branched when T=false.");

    WriteWord(code, 0x08, 0x002B);
    WriteWord(code, 0x0A, 0xE201);
    WriteLong(stack, 0x10, 0x0000_0040);
    WriteLong(stack, 0x14, 0x0000_00F0);
    cpu.Reset();
    cpu.Registers.General[15] = 0x0600_0010;
    cpu.StepInstruction();
    Require(cpu.Registers.General[2] == 1, "SH-2 RTE did not execute delay slot.");
    Require(cpu.Registers.General[15] == 0x0600_0018, "SH-2 RTE did not pop PC/SR.");
    Require(cpu.Registers.ProgramCounter == 0x0000_0040, "SH-2 RTE did not restore PC.");
    Require(cpu.Registers.StatusRegister == 0x0000_00F0, "SH-2 RTE did not restore SR.");

    WriteWord(code, 0x08, 0x4127);
    WriteLong(stack, 0x20, 0x1234_5678);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0600_0020;
    cpu.StepInstruction();
    Require(cpu.Registers.VectorBaseRegister == 0x1234_5678, "SH-2 LDC.L @Rn+,VBR failed.");
    Require(cpu.Registers.General[1] == 0x0600_0024, "SH-2 LDC.L @Rn+,VBR did not postincrement.");
}

static ushort ReadWord(ByteArrayMemory memory, uint offset) =>
    (ushort)((memory.ReadByte(offset) << 8) | memory.ReadByte(offset + 1));

static uint ReadLong(ByteArrayMemory memory, uint offset) =>
    ((uint)ReadWord(memory, offset) << 16) | ReadWord(memory, offset + 2);

static void WriteWord(ByteArrayMemory memory, uint offset, ushort value)
{
    memory.WriteByte(offset, (byte)(value >> 8));
    memory.WriteByte(offset + 1, (byte)value);
}

static void WriteLong(ByteArrayMemory memory, uint offset, uint value)
{
    WriteWord(memory, offset, (ushort)(value >> 16));
    WriteWord(memory, offset + 2, (ushort)value);
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
    public int StepCount { get; private set; }
    public long TotalCycles { get; private set; }

    public void Reset()
    {
        Registers.ProgramCounter = 0;
        Registers.StatusRegister = 0;
        Registers.ProcedureRegister = 0;
        Registers.GlobalBaseRegister = 0;
        Registers.VectorBaseRegister = 0;
        Array.Clear(Registers.General);
        StepCount = 0;
        TotalCycles = 0;
    }

    public void Step(SaturnCycleBudget budget)
    {
        var cycles = budget.MasterCycles + budget.SlaveCycles;
        StepCount++;
        TotalCycles += cycles;
        Registers.ProgramCounter += (uint)(cycles & 0xffff);
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
