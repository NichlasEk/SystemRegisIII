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
VerifyVdp1CommandDecode();
VerifyVdp1SoftwareRenderer();
VerifyVdp2BackScreenRenderer();
VerifyVdp2TilemapRenderer();
VerifySh2InternalRegisterBus();
VerifySh2InterruptEntry();
VerifySh2SleepAndInterruptWake();
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
    Require(bus.ReadWord(0x8600_0FF0) == 0x1122, "SH-2 high area RAM alias failed.");

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
    systemMap.Bus.WriteLong(0x0C00_0000, 0xFEED_FACE);
    Require(systemMap.Bus.ReadLong(0x0600_0000) == 0xFEED_FACE, "High RAM 0x0C mirror mapping failed.");
    Require(
        systemMap.Bus.TryResolve(0x0010_0000, out var region, out _) &&
        region.Device.Name == "SMPC Registers",
        "SMPC stub mapping failed.");
    Require(
        systemMap.Bus.TryResolve(0x2018_0000, out region, out _) &&
        region.Device.Name == "Backup RAM / Cartridge Area",
        "Backup RAM cache-through mapping failed.");
    Require(
        systemMap.Bus.TryResolve(0x2589_0018, out region, out _) &&
        region.Device.Name == "CD Block Register Mirror",
        "CD Block register mirror stub mapping failed.");
    Require(
        systemMap.Bus.TryResolve(0x24FF_FFFF, out region, out _) &&
        region.Device.Name == "A-Bus Probe Area",
        "A-Bus probe area mapping failed.");
    Require(
        systemMap.Bus.TryResolve(0x25B0_0400, out region, out _) &&
        region.Device.Name == "SCSP Area",
        "SCSP register mapping failed.");
    Require(
        systemMap.Bus.TryResolve(0x25C0_0000, out region, out _) &&
        region.Device.Name == "VDP1 Area",
        "VDP1 VRAM/framebuffer mapping failed.");
    Require(
        systemMap.Bus.TryResolve(0x25D0_0010, out region, out _) &&
        region.Device.Name == "VDP1 Registers",
        "VDP1 register mapping failed.");
    Require(systemMap.Bus.ReadWord(0x25D0_0010) == 0x0002, "VDP1 EDSR transfer-end status failed.");
    Require(
        systemMap.Bus.TryResolve(0x25E0_0000, out region, out _) &&
        region.Device.Name == "VDP2 VRAM",
        "VDP2 VRAM mapping failed.");
    Require(
        systemMap.Bus.TryResolve(0x25F0_0000, out region, out _) &&
        region.Device.Name == "VDP2 CRAM",
        "VDP2 CRAM mapping failed.");
    Require(
        systemMap.Bus.TryResolve(0x25F8_0000, out region, out _) &&
        region.Device.Name == "VDP2 Registers",
        "VDP2 register mapping failed.");
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
    Require(systemMap.Bus.ReadWord(0x2589_001C) == 0x0002, "CD Block hardware info flags failed.");
    Require(systemMap.Bus.ReadWord(0x2589_0020) == 0x0000, "CD Block MPEG version failed.");
    Require(systemMap.Bus.ReadWord(0x2589_0024) == 0x0600, "CD Block drive info failed.");
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

    var scspAckMap = SaturnSystemMap.CreateBringup(
        bios,
        new SaturnBringupOptions { SimulateScspCommandAck = true });
    scspAckMap.Bus.WriteLong(0x25A0_0700, 0x0800_0000);
    Require(scspAckMap.Bus.ReadByte(0x25A0_0700) == 0, "SCSP command-ack simulation failed.");
    Require(scspAckMap.Bus.ReadByte(0x25A0_0701) == 0, "SCSP command payload write-back failed.");
    systemMap.Bus.WriteWord(0x2018_0000, 0xBEEF);
    Require(systemMap.Bus.ReadWord(0x0018_0000) == 0xBEEF, "Backup RAM cache-through write-back failed.");
    systemMap.Bus.WriteByte(0x0010_0000, 0x80);
    Require(systemMap.Stubs.Any(static stub => stub.Name == "SMPC Registers" && stub.WriteCount == 1), "Stub counters failed.");
    systemMap.Bus.WriteByte(0x0010_001F, 0x02);
    var smpcRegisters = systemMap.Stubs.OfType<SmpcRegisterBusDevice>().Single();
    Require(smpcRegisters.LastCommand == 0x02, "SMPC command latch failed.");
    Require(smpcRegisters.SlaveSh2Enabled, "SMPC SSHON command failed.");
    systemMap.Bus.WriteByte(0x0010_0063, 0x01);
    systemMap.Bus.WriteByte(0x0010_001F, 0x0E);
    Require(systemMap.Bus.ReadByte(0x0010_0063) == 0x01, "SMPC command did not assert its busy flag.");
    Require(systemMap.Bus.ReadByte(0x0010_0063) == 0x01, "SMPC busy flag cleared too early.");
    Require(systemMap.Bus.ReadByte(0x0010_0063) == 0x00, "SMPC busy flag did not clear after command latency.");
    systemMap.Bus.WriteByte(0x0010_0001, 0x01);
    systemMap.Bus.WriteByte(0x0010_0003, 0x00);
    systemMap.Bus.WriteByte(0x0010_0005, 0xF0);
    systemMap.Bus.WriteByte(0x0010_001F, 0x10);
    Require(smpcRegisters.TryConsumeInterrupt(), "SMPC INTBACK interrupt latch failed.");
    Require(systemMap.Bus.ReadByte(0x0010_0061) == 0x40, "SMPC INTBACK status register failed.");
    Require(systemMap.Bus.ReadByte(0x0010_0021) == 0xC0, "SMPC INTBACK system status 0 failed.");
    Require(systemMap.Bus.ReadByte(0x0010_0023) == 0x19, "SMPC INTBACK RTC century failed.");
    Require(systemMap.Bus.ReadByte(0x0010_0025) == 0x96, "SMPC INTBACK RTC year failed.");
    Require(systemMap.Bus.ReadByte(0x0010_0027) == 0x11, "SMPC INTBACK RTC weekday/month failed.");
    Require(systemMap.Bus.ReadByte(0x0010_0029) == 0x01, "SMPC INTBACK RTC day failed.");
    Require(systemMap.Bus.ReadByte(0x0010_002B) == 0x12, "SMPC INTBACK RTC hour failed.");
    Require(systemMap.Bus.ReadByte(0x0010_002D) == 0x00, "SMPC INTBACK RTC minute failed.");
    Require(systemMap.Bus.ReadByte(0x0010_002F) == 0x00, "SMPC INTBACK RTC second failed.");
    Require(systemMap.Bus.ReadByte(0x0010_0033) == 0x01, "SMPC INTBACK area code failed.");
    systemMap.Bus.WriteByte(0x0010_0001, 0x20);
    systemMap.Bus.WriteByte(0x0010_0003, 0x26);
    systemMap.Bus.WriteByte(0x0010_0005, 0x38);
    systemMap.Bus.WriteByte(0x0010_0007, 0x07);
    systemMap.Bus.WriteByte(0x0010_0009, 0x08);
    systemMap.Bus.WriteByte(0x0010_000B, 0x09);
    systemMap.Bus.WriteByte(0x0010_000D, 0x10);
    systemMap.Bus.WriteByte(0x0010_001F, 0x16);
    systemMap.Bus.WriteByte(0x0010_0001, 0x01);
    systemMap.Bus.WriteByte(0x0010_0003, 0x00);
    systemMap.Bus.WriteByte(0x0010_0005, 0xF0);
    systemMap.Bus.WriteByte(0x0010_001F, 0x10);
    Require(systemMap.Bus.ReadByte(0x0010_0023) == 0x20, "SMPC SETTIME RTC century failed.");
    Require(systemMap.Bus.ReadByte(0x0010_0025) == 0x26, "SMPC SETTIME RTC year failed.");
    Require(systemMap.Bus.ReadByte(0x0010_0027) == 0x38, "SMPC SETTIME RTC weekday/month failed.");
    Require(systemMap.Bus.ReadByte(0x0010_0029) == 0x07, "SMPC SETTIME RTC day failed.");
    systemMap.Bus.WriteByte(0x0010_0001, 0x00);
    systemMap.Bus.WriteByte(0x0010_0003, 0x08);
    systemMap.Bus.WriteByte(0x0010_0005, 0xF0);
    systemMap.Bus.WriteByte(0x0010_001F, 0x10);
    Require(systemMap.Bus.ReadByte(0x0010_0061) == 0xC0, "SMPC INTBACK peripheral status failed.");
    Require(systemMap.Bus.ReadByte(0x0010_0021) == 0xF1, "SMPC INTBACK port 1 digital pad ID failed.");
    Require(systemMap.Bus.ReadByte(0x0010_0023) == 0x02, "SMPC INTBACK port 1 digital pad size failed.");
    Require(systemMap.Bus.ReadByte(0x0010_0025) == 0xFF, "SMPC INTBACK port 1 digital pad data 1 failed.");
    Require(systemMap.Bus.ReadByte(0x0010_0027) == 0xFF, "SMPC INTBACK port 1 digital pad data 2 failed.");
    Require(systemMap.Bus.ReadByte(0x0010_0029) == 0xF0, "SMPC INTBACK port 2 status failed.");
    var pressedPadMap = SaturnSystemMap.CreateBringup(
        bios,
        new SaturnBringupOptions { DigitalPadState = SaturnInputState.Start | SaturnInputState.A });
    pressedPadMap.Bus.WriteByte(0x0010_0003, 0x08);
    pressedPadMap.Bus.WriteByte(0x0010_0005, 0xF0);
    pressedPadMap.Bus.WriteByte(0x0010_001F, 0x10);
    Require(pressedPadMap.Bus.ReadByte(0x0010_0021) == 0xF1, "SMPC pressed pad ID failed.");
    Require(pressedPadMap.Bus.ReadByte(0x0010_0023) == 0x02, "SMPC pressed pad size failed.");
    Require(pressedPadMap.Bus.ReadByte(0x0010_0025) == 0xCF, "SMPC pressed pad data 1 failed.");
    Require(pressedPadMap.Bus.ReadByte(0x0010_0027) == 0xFF, "SMPC pressed pad data 2 failed.");
    var rawPadMap = SaturnSystemMap.CreateBringup(
        bios,
        new SaturnBringupOptions { DigitalPadPeripheralData = [0xF1, 0x02, 0xBF, 0x7F] });
    rawPadMap.Bus.WriteByte(0x0010_0003, 0x08);
    rawPadMap.Bus.WriteByte(0x0010_0005, 0xF0);
    rawPadMap.Bus.WriteByte(0x0010_001F, 0x10);
    Require(rawPadMap.Bus.ReadByte(0x0010_0025) == 0xBF, "SMPC raw pad data 1 failed.");
    Require(rawPadMap.Bus.ReadByte(0x0010_0027) == 0x7F, "SMPC raw pad data 2 failed.");
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

    var discPath = Path.GetTempFileName();
    try
    {
        File.WriteAllBytes(discPath, Enumerable.Range(0, RawDiscImage.DefaultSectorSize * 2).Select(static value => (byte)value).ToArray());
        using var discImage = new RawDiscImage(discPath);
        Require(discImage.SectorCount == 2, "Raw disc sector count failed.");
        Span<byte> sector = stackalloc byte[RawDiscImage.DefaultSectorSize];
        Require(discImage.ReadSector(1, sector) == RawDiscImage.DefaultSectorSize, "Raw disc sector read length failed.");
        Require(sector[0] == 0 && sector[1] == 1 && sector[255] == 0xFF, "Raw disc sector data failed.");

        var discMap = SaturnSystemMap.CreateBringup(
            bios,
            new SaturnBringupOptions { DiscImage = discImage });
        var mountedCdRegisters = discMap.Stubs.OfType<CdBlockRegisterBusDevice>().Single();
        Require(mountedCdRegisters.HasDisc, "CD Block mounted-disc flag failed.");
        discMap.Bus.WriteWord(0x2589_0018, 0x0000);
        discMap.Bus.WriteWord(0x2589_001C, 0x0000);
        discMap.Bus.WriteWord(0x2589_0020, 0x0000);
        discMap.Bus.WriteWord(0x2589_0024, 0x0000);
        Require(discMap.Bus.ReadWord(0x2589_0018) == 0x0280, "CD Block mounted-disc current status failed.");
        Require(discMap.Bus.ReadWord(0x2589_001C) == 0x4101, "CD Block mounted-disc track status failed.");
        Require(discMap.Bus.ReadWord(0x2589_0020) == 0x0100, "CD Block mounted-disc index/FAD status failed.");
        Require(discMap.Bus.ReadWord(0x2589_0024) == 0x0096, "CD Block mounted-disc FAD status failed.");
        discMap.Bus.WriteWord(0x2589_0008, 0xFFFE);
        Require(discMap.Bus.ReadWord(0x2589_0018) == 0x2280, "CD Block mounted-disc periodic status failed.");
        Require(discMap.Bus.ReadWord(0x2589_0008) == 0x4658, "CD Block mounted-disc HIRQ status-ready failed.");
        discMap.Bus.WriteWord(0x2589_0018, 0x0200);
        discMap.Bus.WriteWord(0x2589_001C, 0x0000);
        discMap.Bus.WriteWord(0x2589_0020, 0x0000);
        discMap.Bus.WriteWord(0x2589_0024, 0x0000);
        Require(mountedCdRegisters.LastCommandCode == 0x02, "CD Block TOC command latch failed.");
        Require(discMap.Bus.ReadWord(0x2589_0018) == 0x4080, "CD Block TOC DTREQ status failed.");
        Require(discMap.Bus.ReadWord(0x2589_001C) == 0x00CC, "CD Block TOC transfer length failed.");
        Require((discMap.Bus.ReadWord(0x2589_0008) & 0x0003) == 0x0003, "CD Block TOC DRDY HIRQ failed.");
        Require(discMap.Bus.ReadWord(0x2589_0000) == 0x4100, "CD Block TOC first track control/FAD high failed.");
        Require(discMap.Bus.ReadWord(0x2589_0000) == 0x0096, "CD Block TOC first track FAD low failed.");
        Require(discMap.Bus.ReadWord(0x2589_0000) == 0xFFFF, "CD Block TOC empty track marker failed.");
        Require(discMap.Bus.ReadWord(0x2589_0000) == 0xFFFF, "CD Block TOC empty track marker low failed.");
        for (var index = 0; index < 194; index++)
        {
            discMap.Bus.ReadWord(0x2589_0000);
        }

        Require(discMap.Bus.ReadWord(0x2589_0000) == 0x4101, "CD Block TOC A0 point failed.");
        Require(discMap.Bus.ReadWord(0x2589_0000) == 0x0000, "CD Block TOC A0 payload failed.");
        Require(discMap.Bus.ReadWord(0x2589_0000) == 0x4101, "CD Block TOC A1 point failed.");
        Require(discMap.Bus.ReadWord(0x2589_0000) == 0x0000, "CD Block TOC A1 payload failed.");
        Require(discMap.Bus.ReadWord(0x2589_0000) == 0x4100, "CD Block TOC leadout control/FAD high failed.");
        Require(discMap.Bus.ReadWord(0x2589_0000) == 0x0098, "CD Block TOC leadout FAD low failed.");
        Require(discMap.Bus.ReadWord(0x2589_0000) == 0x0000, "CD Block TOC FIFO exhausted failed.");
        discMap.Bus.WriteWord(0x2589_0018, 0x0600);
        discMap.Bus.WriteWord(0x2589_001C, 0x0000);
        discMap.Bus.WriteWord(0x2589_0020, 0x0000);
        discMap.Bus.WriteWord(0x2589_0024, 0x0000);
        Require(mountedCdRegisters.LastCommandCode == 0x06, "CD Block end-transfer command latch failed.");
        Require(discMap.Bus.ReadWord(0x2589_001C) == 0x00CC, "CD Block end-transfer count failed.");
        Require((discMap.Bus.ReadWord(0x2589_0008) & 0x0081) == 0x0081, "CD Block end-transfer EHST HIRQ failed.");
        discMap.Bus.WriteWord(0x2589_0018, 0x0300);
        discMap.Bus.WriteWord(0x2589_001C, 0x0000);
        discMap.Bus.WriteWord(0x2589_0020, 0x0000);
        discMap.Bus.WriteWord(0x2589_0024, 0x0000);
        Require(mountedCdRegisters.LastCommandCode == 0x03, "CD Block session-info command latch failed.");
        Require(discMap.Bus.ReadWord(0x2589_0020) == 0x0100, "CD Block session-info session count failed.");
        Require(discMap.Bus.ReadWord(0x2589_0024) == 0x0098, "CD Block session-info leadout FAD failed.");
        discMap.Bus.WriteWord(0x2589_0018, 0x0400);
        discMap.Bus.WriteWord(0x2589_001C, 0x0000);
        discMap.Bus.WriteWord(0x2589_0020, 0x0000);
        discMap.Bus.WriteWord(0x2589_0024, 0x0000);
        Require(mountedCdRegisters.LastCommandCode == 0x04, "CD Block init command latch failed.");
        Require(discMap.Bus.ReadWord(0x2589_0018) == 0x2080, "CD Block init busy status failed.");
        for (var poll = 0; poll < 8; poll++)
        {
            discMap.Bus.WriteWord(0x2589_0018, 0x0000);
            discMap.Bus.WriteWord(0x2589_001C, 0x0000);
            discMap.Bus.WriteWord(0x2589_0020, 0x0000);
            discMap.Bus.WriteWord(0x2589_0024, 0x0000);
        }

        Require(discMap.Bus.ReadWord(0x2589_0018) == 0x0180, "CD Block init pause transition failed.");
        discMap.Bus.WriteWord(0x2589_0018, 0x6000);
        discMap.Bus.WriteWord(0x2589_001C, 0xFF00);
        discMap.Bus.WriteWord(0x2589_0020, 0x0000);
        discMap.Bus.WriteWord(0x2589_0024, 0x0000);
        Require(mountedCdRegisters.LastCommandCode == 0x60, "CD Block set-sector-length command latch failed.");
        Require((discMap.Bus.ReadWord(0x2589_0008) & 0x0041) == 0x0041, "CD Block set-sector-length ESEL HIRQ failed.");
        discMap.Bus.WriteWord(0x2589_0018, 0x4804);
        discMap.Bus.WriteWord(0x2589_001C, 0x0000);
        discMap.Bus.WriteWord(0x2589_0020, 0x0000);
        discMap.Bus.WriteWord(0x2589_0024, 0x0000);
        Require(mountedCdRegisters.LastCommandCode == 0x48, "CD Block reset-selector command latch failed.");
        Require((discMap.Bus.ReadWord(0x2589_0008) & 0x0041) == 0x0041, "CD Block reset-selector ESEL HIRQ failed.");
        discMap.Bus.WriteWord(0x2589_0018, 0x6700);
        discMap.Bus.WriteWord(0x2589_001C, 0x0000);
        discMap.Bus.WriteWord(0x2589_0020, 0x0000);
        discMap.Bus.WriteWord(0x2589_0024, 0x0000);
        Require(mountedCdRegisters.LastCommandCode == 0x67, "CD Block get-copy-error command latch failed.");
        Require(discMap.Bus.ReadWord(0x2589_0018) == 0x0100, "CD Block get-copy-error status failed.");
        Require(discMap.Bus.ReadWord(0x2589_001C) == 0x0000, "CD Block get-copy-error CR2 failed.");
        discMap.Bus.WriteWord(0x2589_0018, 0x4000);
        discMap.Bus.WriteWord(0x2589_001C, 0x0096);
        discMap.Bus.WriteWord(0x2589_0020, 0x0000);
        discMap.Bus.WriteWord(0x2589_0024, 0x0002);
        Require(mountedCdRegisters.LastCommandCode == 0x40, "CD Block filter-range command latch failed.");
        Require((discMap.Bus.ReadWord(0x2589_0008) & 0x0041) == 0x0041, "CD Block filter-range ESEL HIRQ failed.");
        discMap.Bus.WriteWord(0x2589_0018, 0x6100);
        discMap.Bus.WriteWord(0x2589_001C, 0x0000);
        discMap.Bus.WriteWord(0x2589_0020, 0x0000);
        discMap.Bus.WriteWord(0x2589_0024, 0x0001);
        Require(mountedCdRegisters.LastCommandCode == 0x61, "CD Block get-sector command latch failed.");
        Require(discMap.Bus.ReadWord(0x2589_0018) == 0x4080, "CD Block get-sector DTREQ status failed.");
        Require(discMap.Bus.ReadWord(0x2589_001C) == 0x0400, "CD Block get-sector transfer length failed.");
        Require((discMap.Bus.ReadWord(0x2589_0008) & 0x0003) == 0x0003, "CD Block get-sector DRDY HIRQ failed.");
        Require(discMap.Bus.ReadWord(0x2589_0000) == 0x0001, "CD Block get-sector first word failed.");
        Require(discMap.Bus.ReadWord(0x2589_0000) == 0x0203, "CD Block get-sector second word failed.");
        for (var index = 0; index < 1022; index++)
        {
            discMap.Bus.ReadWord(0x2589_0000);
        }

        Require(discMap.Bus.ReadWord(0x2589_0000) == 0x0000, "CD Block get-sector FIFO exhausted failed.");
        discMap.Bus.WriteWord(0x2589_0018, 0x7500);
        discMap.Bus.WriteWord(0x2589_001C, 0x0000);
        discMap.Bus.WriteWord(0x2589_0020, 0x0000);
        discMap.Bus.WriteWord(0x2589_0024, 0x0000);
        Require(mountedCdRegisters.LastCommandCode == 0x75, "CD Block abort-file command latch failed.");
        Require(discMap.Bus.ReadWord(0x2589_0018) == 0x2180, "CD Block abort-file status failed.");
        Require((discMap.Bus.ReadWord(0x2589_0008) & 0x0201) == 0x0201, "CD Block abort-file EFLS HIRQ failed.");

        var pauseDiscMap = SaturnSystemMap.CreateBringup(
            bios,
            new SaturnBringupOptions
            {
                DiscImage = discImage,
                MountedDiscInitialStatus = CdBlockDriveStatus.Pause,
            });
        pauseDiscMap.Bus.WriteWord(0x2589_0018, 0x0000);
        pauseDiscMap.Bus.WriteWord(0x2589_001C, 0x0000);
        pauseDiscMap.Bus.WriteWord(0x2589_0020, 0x0000);
        pauseDiscMap.Bus.WriteWord(0x2589_0024, 0x0000);
        Require(pauseDiscMap.Bus.ReadWord(0x2589_0018) == 0x0180, "CD Block mounted-disc status override failed.");
    }
    finally
    {
        File.Delete(discPath);
    }

    var isoPath = Path.GetTempFileName();
    try
    {
        CreateTinyIsoImage(isoPath);
        using var isoImage = new RawDiscImage(isoPath);
        var isoMap = SaturnSystemMap.CreateBringup(
            bios,
            new SaturnBringupOptions { DiscImage = isoImage });
        var isoCdRegisters = isoMap.Stubs.OfType<CdBlockRegisterBusDevice>().Single();

        isoMap.Bus.WriteWord(0x2589_0018, 0xE100);
        isoMap.Bus.WriteWord(0x2589_001C, 0x0000);
        isoMap.Bus.WriteWord(0x2589_0020, 0x0000);
        isoMap.Bus.WriteWord(0x2589_0024, 0x0000);
        Require(isoCdRegisters.LastCommandCode == 0xE1, "CD Block get-auth command latch failed.");
        Require(isoMap.Bus.ReadWord(0x2589_0018) == 0x0200, "CD Block get-auth status failed.");
        Require(isoMap.Bus.ReadWord(0x2589_001C) == 0x0004, "CD Block get-auth Saturn type failed.");
        Require(isoMap.Bus.ReadWord(0x2589_0020) == 0x0000, "CD Block get-auth reserved word 1 failed.");
        Require(isoMap.Bus.ReadWord(0x2589_0024) == 0x0000, "CD Block get-auth reserved word 2 failed.");

        isoMap.Bus.WriteWord(0x2589_0018, 0x7100);
        isoMap.Bus.WriteWord(0x2589_001C, 0x0000);
        isoMap.Bus.WriteWord(0x2589_0020, 0x0000);
        isoMap.Bus.WriteWord(0x2589_0024, 0x0000);
        Require(isoCdRegisters.LastCommandCode == 0x71, "CD Block read-directory command latch failed.");
        Require((isoMap.Bus.ReadWord(0x2589_0008) & 0x0201) == 0x0201, "CD Block read-directory EFLS HIRQ failed.");

        isoMap.Bus.WriteWord(0x2589_0018, 0x7200);
        isoMap.Bus.WriteWord(0x2589_001C, 0x0000);
        isoMap.Bus.WriteWord(0x2589_0020, 0x0000);
        isoMap.Bus.WriteWord(0x2589_0024, 0x0000);
        Require(isoCdRegisters.LastCommandCode == 0x72, "CD Block filesystem-scope command latch failed.");
        Require(isoMap.Bus.ReadWord(0x2589_0018) == 0x0280, "CD Block filesystem-scope status failed.");
        Require(isoMap.Bus.ReadWord(0x2589_001C) == 0x0001, "CD Block filesystem-scope file count failed.");
        Require(isoMap.Bus.ReadWord(0x2589_0020) == 0x0100, "CD Block filesystem-scope directory scope failed.");
        Require(isoMap.Bus.ReadWord(0x2589_0024) == 0x0002, "CD Block filesystem-scope file offset failed.");

        isoMap.Bus.WriteWord(0x2589_0018, 0x7300);
        isoMap.Bus.WriteWord(0x2589_001C, 0x0000);
        isoMap.Bus.WriteWord(0x2589_0020, 0x0000);
        isoMap.Bus.WriteWord(0x2589_0024, 0x0002);
        Require(isoCdRegisters.LastCommandCode == 0x73, "CD Block get-file-info command latch failed.");
        Require(isoMap.Bus.ReadWord(0x2589_0018) == 0x4080, "CD Block get-file-info DTREQ status failed.");
        Require(isoMap.Bus.ReadWord(0x2589_001C) == 0x0006, "CD Block get-file-info transfer length failed.");
        Require((isoMap.Bus.ReadWord(0x2589_0008) & 0x0003) == 0x0003, "CD Block get-file-info DRDY HIRQ failed.");
        Require(isoMap.Bus.ReadWord(0x2589_0000) == 0x0000, "CD Block file-info FAD high failed.");
        Require(isoMap.Bus.ReadWord(0x2589_0000) == 0x00B4, "CD Block file-info FAD low failed.");
        Require(isoMap.Bus.ReadWord(0x2589_0000) == 0x0000, "CD Block file-info size high failed.");
        Require(isoMap.Bus.ReadWord(0x2589_0000) == 0x0800, "CD Block file-info size low failed.");
        Require(isoMap.Bus.ReadWord(0x2589_0000) == 0x0000, "CD Block file-info unit/gap failed.");
        Require(isoMap.Bus.ReadWord(0x2589_0000) == 0x0000, "CD Block file-info number/attribute failed.");

        isoMap.Bus.WriteWord(0x2589_0018, 0x7400);
        isoMap.Bus.WriteWord(0x2589_001C, 0x0000);
        isoMap.Bus.WriteWord(0x2589_0020, 0x0000);
        isoMap.Bus.WriteWord(0x2589_0024, 0x0002);
        Require(isoCdRegisters.LastCommandCode == 0x74, "CD Block read-file command latch failed.");
        Require((isoMap.Bus.ReadWord(0x2589_0008) & 0x0201) == 0x0201, "CD Block read-file EFLS HIRQ failed.");

        isoMap.Bus.WriteWord(0x2589_0018, 0x6100);
        isoMap.Bus.WriteWord(0x2589_001C, 0x0000);
        isoMap.Bus.WriteWord(0x2589_0020, 0x0000);
        isoMap.Bus.WriteWord(0x2589_0024, 0x0001);
        Require(isoCdRegisters.LastCommandCode == 0x61, "CD Block read-file get-sector command latch failed.");
        Require(isoMap.Bus.ReadWord(0x2589_0018) == 0x4080, "CD Block read-file get-sector DTREQ status failed.");
        Require(isoMap.Bus.ReadWord(0x2589_001C) == 0x0400, "CD Block read-file sector transfer length failed.");
        Require(isoMap.Bus.ReadWord(0x2589_0000) == 0xCAFE, "CD Block read-file first data word failed.");
        Require(isoMap.Bus.ReadWord(0x2589_0000) == 0xBABE, "CD Block read-file second data word failed.");
    }
    finally
    {
        File.Delete(isoPath);
    }

    var cueDirectory = Path.Combine(Path.GetTempPath(), $"systemregis-cue-{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(cueDirectory);
        var cuePath = CreateTinyCueImage(cueDirectory);
        using var cueImage = new CueDiscImage(cuePath);
        Require(cueImage.SectorSize == RawDiscImage.DefaultSectorSize, "CUE disc logical sector size failed.");
        Require(cueImage.SectorCount == 40, "CUE disc sector count failed.");
        Span<byte> cueSector = stackalloc byte[RawDiscImage.DefaultSectorSize];
        Require(cueImage.ReadSector(30, cueSector) == RawDiscImage.DefaultSectorSize, "CUE disc sector read length failed.");
        Require(cueSector[0] == 0xCA && cueSector[1] == 0xFE && cueSector[2] == 0xBA && cueSector[3] == 0xBE, "CUE disc user-data offset failed.");

        var cueMap = SaturnSystemMap.CreateBringup(
            bios,
            new SaturnBringupOptions { DiscImage = cueImage });
        cueMap.Bus.WriteWord(0x2589_0018, 0x7100);
        cueMap.Bus.WriteWord(0x2589_001C, 0x0000);
        cueMap.Bus.WriteWord(0x2589_0020, 0x0000);
        cueMap.Bus.WriteWord(0x2589_0024, 0x0000);
        cueMap.Bus.WriteWord(0x2589_0018, 0x7300);
        cueMap.Bus.WriteWord(0x2589_001C, 0x0000);
        cueMap.Bus.WriteWord(0x2589_0020, 0x0000);
        cueMap.Bus.WriteWord(0x2589_0024, 0x0002);
        Require(cueMap.Bus.ReadWord(0x2589_0018) == 0x4080, "CUE CD Block get-file-info DTREQ status failed.");
        Require(cueMap.Bus.ReadWord(0x2589_0000) == 0x0000, "CUE CD Block file-info FAD high failed.");
        Require(cueMap.Bus.ReadWord(0x2589_0000) == 0x00B4, "CUE CD Block file-info FAD low failed.");
    }
    finally
    {
        if (Directory.Exists(cueDirectory))
        {
            Directory.Delete(cueDirectory, recursive: true);
        }
    }
}

static void VerifyVdp1CommandDecode()
{
    var commandBytes = new byte[0x20];
    WriteWord(commandBytes, 0x00, 0x1204);
    WriteWord(commandBytes, 0x02, 0x0040);
    WriteWord(commandBytes, 0x08, 0x0100);
    WriteWord(commandBytes, 0x0A, 0x0407);
    WriteWord(commandBytes, 0x0C, 0xFFF0);
    WriteWord(commandBytes, 0x0E, 0x0020);

    var command = Vdp1Command.Read(commandBytes, 0);
    Require(command.CommandName == "polygon", "VDP1 command type decode failed.");
    Require(command.JumpMode == 1 && command.LinkAddress == 0x200, "VDP1 command link decode failed.");
    Require(command.CharacterByteAddress == 0x800, "VDP1 character address decode failed.");
    Require(command.CharacterWidth == 32 && command.CharacterHeight == 7, "VDP1 character size decode failed.");
    Require(command.Xa == -16 && command.Ya == 32, "VDP1 coordinate decode failed.");

    static void WriteWord(Span<byte> destination, int offset, ushort value)
    {
        destination[offset] = (byte)(value >> 8);
        destination[offset + 1] = (byte)value;
    }
}

static void VerifyVdp1SoftwareRenderer()
{
    var vram = new byte[0x80000];
    var colorRam = new byte[0x1000];
    colorRam[2] = 0x00;
    colorRam[3] = 0x1F;
    colorRam[4] = 0x03;
    colorRam[5] = 0xE0;
    vram[0x100] = 0x1F;
    vram[0x101] = 0xF2;

    var commands = new[]
    {
        MakeCommand(0x0009, xc: 7, yc: 1),
        MakeCommand(0x0000, characterAddress: 0x0020, characterSize: 0x0101),
        MakeCommand(0x8000),
    };
    var rendered = Vdp1SoftwareRenderer.Render(vram, colorRam, commands, width: 8, height: 2);
    Require(rendered.DrawnSprites == 1, "VDP1 normal-sprite renderer did not draw the command.");
    Require(rendered.DrawnPixels == 1, "VDP1 normal-sprite end-code handling failed.");
    Require(rendered.Frame.BgraPixels.Span[0] == 0xFFFF_0000, "VDP1 CRAM RGB555 conversion failed.");
    Require(rendered.Frame.BgraPixels.Span[1] == 0xFF00_0000, "VDP1 transparent texel overwrote the frame.");
    Require(rendered.Frame.BgraPixels.Span[3] == 0xFF00_0000, "VDP1 renderer continued after two end codes.");

    var rgbVram = new byte[0x80000];
    rgbVram[0x100] = 0x00;
    rgbVram[0x101] = 0x1F;
    rgbVram[0x102] = 0x80;
    rgbVram[0x103] = 0x1F;
    var rgbCommand = MakeCommand(0x0000, drawMode: 0x0028, characterAddress: 0x0020, characterSize: 0x0101);
    var rgbRendered = Vdp1SoftwareRenderer.Render(rgbVram, colorRam, [rgbCommand, MakeCommand(0x8000)], width: 8, height: 1);
    Require(rgbRendered.DrawnPixels == 1, "VDP1 direct-RGB transparency threshold failed.");
    Require(rgbRendered.Frame.BgraPixels.Span[0] == 0xFF00_0000, "VDP1 low direct-RGB value was not transparent.");
    Require(rgbRendered.Frame.BgraPixels.Span[1] == 0xFFFF_0000, "VDP1 direct-RGB visible pixel conversion failed.");

    var polygon = MakeCommand(0x0004, drawMode: 0x0040, color: 0x801F, xa: 1, ya: 0, xb: 3, yb: 0, xc: 3, yc: 2, xd: 1, yd: 2);
    var polygonRendered = Vdp1SoftwareRenderer.Render(vram, colorRam, [polygon, MakeCommand(0x8000)], width: 5, height: 4);
    Require(polygonRendered.DrawnPixels > 0, "VDP1 polygon renderer drew no pixels.");
    Require(polygonRendered.Frame.BgraPixels.Span[7] == 0xFFFF_0000, "VDP1 polygon fill failed.");

    var line = MakeCommand(0x0006, drawMode: 0x0040, color: 0x83E0, xa: 0, ya: 0, xb: 3, yb: 3);
    var lineRendered = Vdp1SoftwareRenderer.Render(vram, colorRam, [line, MakeCommand(0x8000)], width: 4, height: 4);
    Require(lineRendered.DrawnPixels == 4, "VDP1 line rasterization failed.");
    Require(lineRendered.Frame.BgraPixels.Span[10] == 0xFF00_FF00, "VDP1 line color conversion failed.");

    var quadVram = new byte[0x80000];
    for (var offset = 0x100; offset < 0x110; offset += 2)
    {
        quadVram[offset] = 0x80;
        quadVram[offset + 1] = 0x1F;
    }

    var scaled = MakeCommand(
        0x0501, drawMode: 0x0068, characterAddress: 0x0020, characterSize: 0x0101,
        xa: 1, ya: 1, xb: 3, yb: 2);
    var scaledRendered = Vdp1SoftwareRenderer.Render(quadVram, colorRam, [scaled, MakeCommand(0x8000)], width: 6, height: 5);
    Require(scaledRendered.DrawnPixels > 0, "VDP1 scaled sprite renderer drew no pixels.");
    Require(scaledRendered.Frame.BgraPixels.Span[14] == 0xFFFF_0000, "VDP1 scaled sprite texture mapping failed.");

    var distorted = MakeCommand(
        0x0002, drawMode: 0x0068, characterAddress: 0x0020, characterSize: 0x0101,
        xa: 0, ya: 0, xb: 4, yb: 1, xc: 3, yc: 4, xd: 0, yd: 3);
    var distortedRendered = Vdp1SoftwareRenderer.Render(quadVram, colorRam, [distorted, MakeCommand(0x8000)], width: 5, height: 5);
    Require(distortedRendered.DrawnPixels > 0, "VDP1 distorted sprite renderer drew no pixels.");
    Require(distortedRendered.Frame.BgraPixels.Span[12] == 0xFFFF_0000, "VDP1 distorted sprite texture mapping failed.");

    static Vdp1Command MakeCommand(
        ushort control,
        ushort drawMode = 0,
        ushort color = 0,
        ushort characterAddress = 0,
        ushort characterSize = 0,
        short xa = 0,
        short ya = 0,
        short xb = 0,
        short yb = 0,
        short xc = 0,
        short yc = 0,
        short xd = 0,
        short yd = 0) =>
        new(
            0,
            control,
            0,
            drawMode,
            color,
            characterAddress,
            characterSize,
            xa,
            ya,
            xb,
            yb,
            xc,
            yc,
            xd,
            yd,
            0);
}

static void VerifyVdp2BackScreenRenderer()
{
    var vram = new byte[0x80000];
    var registers = new byte[0x200];
    WriteWord(registers, 0xAC, 0x0000);
    WriteWord(registers, 0xAE, 0x0010);
    WriteWord(vram, 0x20, 0x7FFF);

    var rows = Vdp2BackScreenRenderer.CreateRows(vram, registers, height: 2);
    Require(rows.SequenceEqual([0xFFFF_FFFFu, 0xFFFF_FFFFu]), "VDP2 solid back-screen lookup failed.");

    WriteWord(registers, 0xAC, 0x8000);
    WriteWord(vram, 0x22, 0x001F);
    rows = Vdp2BackScreenRenderer.CreateRows(vram, registers, height: 2);
    Require(rows[0] == 0xFFFF_FFFF && rows[1] == 0xFFFF_0000, "VDP2 per-line back-screen lookup failed.");

    var rendered = Vdp1SoftwareRenderer.Render(
        new byte[0x80000],
        new byte[0x1000],
        [],
        rows,
        width: 2,
        height: 2);
    Require(rendered.Frame.BgraPixels.Span[0] == 0xFFFF_FFFF, "VDP1 compositor ignored VDP2 back-screen row 0.");
    Require(rendered.Frame.BgraPixels.Span[2] == 0xFFFF_0000, "VDP1 compositor ignored VDP2 back-screen row 1.");

    static void WriteWord(Span<byte> destination, int offset, ushort value)
    {
        destination[offset] = (byte)(value >> 8);
        destination[offset + 1] = (byte)value;
    }
}

static void VerifyVdp2TilemapRenderer()
{
    var vram = new byte[0x80000];
    var colorRam = new byte[0x1000];
    var registers = new byte[0x200];
    WriteWord(registers, 0x20, 0x0001);
    WriteWord(registers, 0x28, 0x0010);
    WriteWord(registers, 0x40, 0x0101);
    WriteWord(registers, 0x42, 0x0101);
    WriteWord(registers, 0xF8, 0x0001);
    WriteWord(vram, 0x4000, 0x0000);
    WriteWord(vram, 0x4002, 0x0002);
    vram[0x40] = 0x01;
    WriteWord(colorRam, 0x0002, 0x001F);

    var frame = Vdp2TilemapRenderer.Render(vram, colorRam, registers, width: 8, height: 8);
    Require(frame[0] == 0xFFFF_0000, "VDP2 NBG 8bpp tile pixel lookup failed.");
    Require(frame[1] == 0xFF00_0000, "VDP2 transparent NBG dot overwrote the back screen.");

    Array.Clear(vram);
    WriteWord(registers, 0x28, 0x0012);
    vram[0] = 0x01;
    frame = Vdp2TilemapRenderer.Render(vram, colorRam, registers, width: 1, height: 1);
    Require(frame[0] == 0xFFFF_0000, "VDP2 NBG 8bpp bitmap pixel lookup failed.");

    static void WriteWord(Span<byte> destination, int offset, ushort value)
    {
        destination[offset] = (byte)(value >> 8);
        destination[offset + 1] = (byte)value;
    }
}

static void CreateTinyIsoImage(string path)
{
    const int rootDirectoryLba = 20;
    const int bootFileLba = 30;
    var image = new byte[RawDiscImage.DefaultSectorSize * 40];
    var primaryVolumeDescriptor = image.AsSpan(RawDiscImage.DefaultSectorSize * 16, RawDiscImage.DefaultSectorSize);
    WriteAscii(image.AsSpan(0, 16), "SEGA SEGASATURN ");
    primaryVolumeDescriptor[0] = 1;
    WriteAscii(primaryVolumeDescriptor[1..6], "CD001");
    primaryVolumeDescriptor[6] = 1;
    WriteDirectoryRecord(primaryVolumeDescriptor, 156, rootDirectoryLba, RawDiscImage.DefaultSectorSize, 0x02, [0]);

    var rootDirectory = image.AsSpan(RawDiscImage.DefaultSectorSize * rootDirectoryLba, RawDiscImage.DefaultSectorSize);
    var offset = 0;
    offset += WriteDirectoryRecord(rootDirectory, offset, rootDirectoryLba, RawDiscImage.DefaultSectorSize, 0x02, [0]);
    offset += WriteDirectoryRecord(rootDirectory, offset, rootDirectoryLba, RawDiscImage.DefaultSectorSize, 0x02, [1]);
    WriteDirectoryRecord(rootDirectory, offset, bootFileLba, RawDiscImage.DefaultSectorSize, 0x00, "BOOT.BIN;1"u8);

    var bootFile = image.AsSpan(RawDiscImage.DefaultSectorSize * bootFileLba, RawDiscImage.DefaultSectorSize);
    bootFile[0] = 0xCA;
    bootFile[1] = 0xFE;
    bootFile[2] = 0xBA;
    bootFile[3] = 0xBE;

    File.WriteAllBytes(path, image);
}

static string CreateTinyCueImage(string directory)
{
    var cuePath = Path.Combine(directory, "tiny.cue");
    var binPath = Path.Combine(directory, "tiny.bin");
    var isoPath = Path.Combine(directory, "tiny.iso");
    CreateTinyIsoImage(isoPath);
    var isoBytes = File.ReadAllBytes(isoPath);
    var rawBytes = new byte[40 * 2352];
    for (var sector = 0; sector < 40; sector++)
    {
        var rawOffset = sector * 2352;
        rawBytes[rawOffset] = 0x00;
        rawBytes.AsSpan(rawOffset + 1, 10).Fill(0xFF);
        rawBytes[rawOffset + 11] = 0x00;
        isoBytes.AsSpan(sector * RawDiscImage.DefaultSectorSize, RawDiscImage.DefaultSectorSize)
            .CopyTo(rawBytes.AsSpan(rawOffset + 16, RawDiscImage.DefaultSectorSize));
    }

    File.WriteAllBytes(binPath, rawBytes);
    File.WriteAllText(
        cuePath,
        """
        FILE "tiny.bin" BINARY
          TRACK 01 MODE1/2352
            INDEX 01 00:00:00

        """);
    return cuePath;
}

static int WriteDirectoryRecord(
    Span<byte> sector,
    int offset,
    uint extentLba,
    uint dataLength,
    byte flags,
    ReadOnlySpan<byte> name)
{
    var length = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
    var record = sector.Slice(offset, length);
    record.Clear();
    record[0] = (byte)length;
    WriteUInt32LittleEndian(record[2..6], extentLba);
    WriteUInt32BigEndian(record[6..10], extentLba);
    WriteUInt32LittleEndian(record[10..14], dataLength);
    WriteUInt32BigEndian(record[14..18], dataLength);
    record[25] = flags;
    record[28] = 1;
    record[31] = 1;
    record[32] = (byte)name.Length;
    name.CopyTo(record[33..]);
    return length;
}

static void WriteUInt32LittleEndian(Span<byte> destination, uint value)
{
    destination[0] = (byte)value;
    destination[1] = (byte)(value >> 8);
    destination[2] = (byte)(value >> 16);
    destination[3] = (byte)(value >> 24);
}

static void WriteUInt32BigEndian(Span<byte> destination, uint value)
{
    destination[0] = (byte)(value >> 24);
    destination[1] = (byte)(value >> 16);
    destination[2] = (byte)(value >> 8);
    destination[3] = (byte)value;
}

static void WriteAscii(Span<byte> destination, string value)
{
    for (var index = 0; index < value.Length; index++)
    {
        destination[index] = (byte)value[index];
    }
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

    masterBus.WriteLong(0xFFFF_FF00, 7);
    masterBus.WriteLong(0xFFFF_FF04, unchecked((uint)-100));
    Require(masterBus.ReadLong(0xFFFF_FF04) == unchecked((uint)-14), "SH-2 DIVU 32/32 quotient failed.");
    Require(masterBus.ReadLong(0xFFFF_FF10) == unchecked((uint)-2), "SH-2 DIVU 32/32 remainder failed.");

    masterBus.WriteLong(0xFFFF_FF00, 0x0022_0000);
    masterBus.WriteLong(0xFFFF_FF10, 0xFFFF_FFFE);
    masterBus.WriteLong(0xFFFF_FF14, 0x7988_0000);
    Require(masterBus.ReadLong(0xFFFF_FF14) == 0xFFFF_F484, "SH-2 DIVU 64/32 quotient failed.");
    Require(masterBus.ReadLong(0xFFFF_FF10) == 0, "SH-2 DIVU 64/32 remainder failed.");
    Require(masterBus.ReadLong(0xFFFF_FF34) == 0xFFFF_F484, "SH-2 DIVU register mirror failed.");

    masterBus.WriteLong(0xFFFF_FF08, 0);
    masterBus.WriteLong(0xFFFF_FF00, 0);
    masterBus.WriteLong(0xFFFF_FF04, 1);
    Require((masterBus.ReadLong(0xFFFF_FF08) & 1) != 0, "SH-2 DIVU divide-by-zero flag failed.");
    Require(masterBus.ReadLong(0xFFFF_FF04) == 0x7FFF_FFFF, "SH-2 DIVU divide-by-zero saturation failed.");
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

static void VerifySh2SleepAndInterruptWake()
{
    var ram = new ByteArrayMemory("RAM", 0x2000);
    var bus = new SaturnAddressMapBuilder()
        .Map(0x0000_0000, 0x0000_1FFF, ram)
        .Build();
    bus.WriteLong(0x0000_0000, 0x0000_0100);
    bus.WriteLong(0x0000_0004, 0x0000_0040);
    bus.WriteWord(0x0000_0100, 0x001B);
    bus.WriteWord(0x0000_0102, 0x0009);
    bus.WriteLong(0x0000_0400 + (0x40 * 4), 0x0000_0800);

    var cpu = new Sh2Cpu("Sleep SH-2", bus, 0x0000_0000);
    cpu.Reset();
    cpu.Registers.VectorBaseRegister = 0x0000_0400;
    cpu.Registers.General[15] = 0x0000_1000;
    cpu.StepInstruction();
    Require(cpu.IsSleeping, "SH-2 SLEEP did not enter the sleep state.");
    Require(cpu.Registers.ProgramCounter == 0x0000_0102, "SH-2 SLEEP resume PC was incorrect.");

    cpu.StepInstruction();
    Require(cpu.Registers.ProgramCounter == 0x0000_0102, "Sleeping SH-2 continued executing instructions.");
    Require(!cpu.RequestInterrupt(4, 0x40), "Masked SH-2 interrupt woke SLEEP.");
    Require(cpu.IsSleeping, "Masked SH-2 interrupt cleared the sleep state.");

    Require(cpu.RequestInterrupt(5, 0x40), "Unmasked SH-2 interrupt did not wake SLEEP.");
    Require(!cpu.IsSleeping, "Accepted SH-2 interrupt left the CPU sleeping.");
    Require(cpu.Registers.ProgramCounter == 0x0000_0800, "SH-2 SLEEP wake vector failed.");
    Require(bus.ReadLong(0x0000_0FF8) == 0x0000_0102, "SH-2 SLEEP wake stacked the wrong resume PC.");
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

    WriteWord(code, 0x08, 0x431B);
    data.WriteByte(0x20, 0);
    cpu.Reset();
    cpu.Registers.General[3] = 0x0600_0020;
    cpu.StepInstruction();
    Require(cpu.Registers.T, "SH-2 TAS.B failed zero-byte test.");
    Require(data.ReadByte(0x20) == 0x80, "SH-2 TAS.B failed to set bit 7.");
    Require(cpu.UnimplementedInstructionCount == 0, "SH-2 TAS.B was recorded as unimplemented.");

    data.WriteByte(0x20, 0x45);
    cpu.Reset();
    cpu.Registers.General[3] = 0x0600_0020;
    cpu.StepInstruction();
    Require(!cpu.Registers.T, "SH-2 TAS.B failed nonzero-byte test.");
    Require(data.ReadByte(0x20) == 0xC5, "SH-2 TAS.B did not preserve existing bits.");

    WriteWord(code, 0x08, 0x0008);
    cpu.Reset();
    cpu.Registers.T = true;
    cpu.StepInstruction();
    Require(!cpu.Registers.T, "SH-2 CLRT failed to clear T.");
    Require(cpu.UnimplementedInstructionCount == 0, "SH-2 CLRT was recorded as unimplemented.");

    WriteWord(code, 0x08, 0x4122);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0600_0008;
    cpu.Registers.ProcedureRegister = 0xCAFEBABE;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0x0600_0004, "SH-2 STS.L PR,@-Rn did not predecrement.");
    Require(ReadLong(data, 4) == 0xCAFE_BABE, "SH-2 STS.L PR,@-Rn failed.");

    WriteWord(code, 0x08, 0x4112);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0600_0008;
    cpu.Registers.MacLow = 0x1234_5678;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0x0600_0004, "SH-2 STS.L MACL,@-Rn did not predecrement.");
    Require(ReadLong(data, 4) == 0x1234_5678, "SH-2 STS.L MACL,@-Rn failed.");

    WriteWord(code, 0x08, 0x091A);
    cpu.Reset();
    cpu.Registers.MacLow = 0x0000_0040;
    cpu.Registers.ProcedureRegister = 0xCAFEBABE;
    cpu.StepInstruction();
    Require(cpu.Registers.General[9] == 0x0000_0040, "SH-2 STS MACL,Rn failed.");

    WriteWord(code, 0x08, 0x431A);
    cpu.Reset();
    cpu.Registers.General[3] = 0x89AB_CDEF;
    cpu.StepInstruction();
    Require(cpu.Registers.MacLow == 0x89AB_CDEF, "SH-2 LDS Rn,MACL failed.");
    Require(cpu.UnimplementedInstructionCount == 0, "SH-2 LDS Rn,MACL was recorded as unimplemented.");

    WriteWord(code, 0x08, 0x090A);
    cpu.Reset();
    cpu.Registers.MacHigh = 0x1357_2468;
    cpu.StepInstruction();
    Require(cpu.Registers.General[9] == 0x1357_2468, "SH-2 STS MACH,Rn failed.");

    WriteWord(code, 0x08, 0x420A);
    cpu.Reset();
    cpu.Registers.General[2] = 0x7654_3210;
    cpu.StepInstruction();
    Require(cpu.Registers.MacHigh == 0x7654_3210, "SH-2 LDS Rn,MACH failed.");
    Require(cpu.UnimplementedInstructionCount == 0, "SH-2 LDS Rn,MACH was recorded as unimplemented.");

    WriteWord(code, 0x08, 0x4F02);
    cpu.Reset();
    cpu.Registers.General[15] = 0x0600_0008;
    cpu.Registers.MacHigh = 0x2468_1357;
    cpu.StepInstruction();
    Require(cpu.Registers.General[15] == 0x0600_0004, "SH-2 STS.L MACH,@-Rn did not predecrement.");
    Require(ReadLong(data, 4) == 0x2468_1357, "SH-2 STS.L MACH,@-Rn failed.");

    WriteWord(code, 0x08, 0x4116);
    WriteLong(data, 8, 0x89AB_CDEF);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0600_0008;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0x0600_000C, "SH-2 LDS.L @Rn+,MACL did not postincrement.");
    Require(cpu.Registers.MacLow == 0x89AB_CDEF, "SH-2 LDS.L @Rn+,MACL failed.");

    WriteWord(code, 0x08, 0x4F06);
    WriteLong(data, 8, 0x7654_3210);
    cpu.Reset();
    cpu.Registers.General[15] = 0x0600_0008;
    cpu.StepInstruction();
    Require(cpu.Registers.General[15] == 0x0600_000C, "SH-2 LDS.L @Rn+,MACH did not postincrement.");
    Require(cpu.Registers.MacHigh == 0x7654_3210, "SH-2 LDS.L @Rn+,MACH failed.");

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

    WriteWord(code, 0x08, 0x312E);
    cpu.Reset();
    cpu.Registers.General[1] = 1;
    cpu.Registers.General[2] = 2;
    cpu.Registers.T = true;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 4, "SH-2 ADDC Rm,Rn failed carry-in result.");
    Require(!cpu.Registers.T, "SH-2 ADDC Rm,Rn failed carry-in T flag.");

    WriteWord(code, 0x08, 0x312E);
    cpu.Reset();
    cpu.Registers.General[1] = 0xFFFF_FFFF;
    cpu.Registers.General[2] = 0;
    cpu.Registers.T = true;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0, "SH-2 ADDC Rm,Rn failed overflow result.");
    Require(cpu.Registers.T, "SH-2 ADDC Rm,Rn failed overflow T flag.");

    WriteWord(code, 0x08, 0x0127);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0001_0000;
    cpu.Registers.General[2] = 0x0000_0010;
    cpu.StepInstruction();
    Require(cpu.Registers.MacLow == 0x0010_0000, "SH-2 MUL.L Rm,Rn failed.");

    WriteWord(code, 0x08, 0x212E);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0000_FFFF;
    cpu.Registers.General[2] = 2;
    cpu.StepInstruction();
    Require(cpu.Registers.MacLow == 0x0001_FFFE, "SH-2 MULU.W Rm,Rn failed.");

    WriteWord(code, 0x08, 0x212F);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0000_FFFF;
    cpu.Registers.General[2] = 2;
    cpu.StepInstruction();
    Require(cpu.Registers.MacLow == 0xFFFF_FFFE, "SH-2 MULS.W Rm,Rn failed.");

    WriteWord(code, 0x08, 0x3125);
    cpu.Reset();
    cpu.Registers.General[1] = 0xFFFF_FFFF;
    cpu.Registers.General[2] = 2;
    cpu.StepInstruction();
    Require(cpu.Registers.MacHigh == 0x0000_0001, "SH-2 DMULU.L Rm,Rn failed high word.");
    Require(cpu.Registers.MacLow == 0xFFFF_FFFE, "SH-2 DMULU.L Rm,Rn failed low word.");

    WriteWord(code, 0x08, 0x312D);
    cpu.Reset();
    cpu.Registers.General[1] = 0xFFFF_FFFF;
    cpu.Registers.General[2] = 2;
    cpu.StepInstruction();
    Require(cpu.Registers.MacHigh == 0xFFFF_FFFF, "SH-2 DMULS.L Rm,Rn failed high word.");
    Require(cpu.Registers.MacLow == 0xFFFF_FFFE, "SH-2 DMULS.L Rm,Rn failed low word.");

    WriteWord(code, 0x08, 0x203D);
    cpu.Reset();
    cpu.Registers.General[0] = 0x1122_3344;
    cpu.Registers.General[3] = 0xAABB_CCDD;
    cpu.StepInstruction();
    Require(cpu.Registers.General[0] == 0xCCDD_1122, "SH-2 XTRCT Rm,Rn failed.");

    WriteWord(code, 0x08, 0x0028);
    cpu.Reset();
    cpu.Registers.MacHigh = 0x1234_5678;
    cpu.Registers.MacLow = 0x89AB_CDEF;
    cpu.StepInstruction();
    Require(cpu.Registers.MacHigh == 0, "SH-2 CLRMAC failed MACH.");
    Require(cpu.Registers.MacLow == 0, "SH-2 CLRMAC failed MACL.");

    WriteWord(code, 0x08, 0x012F);
    WriteLong(data, 0x10, 0xFFFF_FFFE);
    WriteLong(data, 0x20, 0x0000_0003);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0600_0010;
    cpu.Registers.General[2] = 0x0600_0020;
    cpu.Registers.MacHigh = 0x0000_0000;
    cpu.Registers.MacLow = 0x0000_0001;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0x0600_0014, "SH-2 MAC.L did not postincrement source.");
    Require(cpu.Registers.General[2] == 0x0600_0024, "SH-2 MAC.L did not postincrement destination.");
    Require(cpu.Registers.MacHigh == 0xFFFF_FFFF, "SH-2 MAC.L failed high word.");
    Require(cpu.Registers.MacLow == 0xFFFF_FFFB, "SH-2 MAC.L failed low word.");

    WriteLong(data, 0x10, 0x0000_0002);
    WriteLong(data, 0x20, 0x0000_0002);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0600_0010;
    cpu.Registers.General[2] = 0x0600_0020;
    cpu.Registers.S = true;
    cpu.Registers.MacHigh = 0x0000_7FFF;
    cpu.Registers.MacLow = 0xFFFF_FFFE;
    cpu.StepInstruction();
    Require(cpu.Registers.S, "SH-2 SR.S flag did not remain set.");
    Require(cpu.Registers.MacHigh == 0x0000_7FFF, "SH-2 MAC.L saturation failed high word.");
    Require(cpu.Registers.MacLow == 0xFFFF_FFFF, "SH-2 MAC.L saturation failed low word.");

    WriteLong(data, 0x10, 0xFFFF_FFFE);
    WriteLong(data, 0x20, 0x0000_0002);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0600_0010;
    cpu.Registers.General[2] = 0x0600_0020;
    cpu.Registers.S = true;
    cpu.Registers.MacHigh = 0xFFFF_8000;
    cpu.Registers.MacLow = 0x0000_0001;
    cpu.StepInstruction();
    Require(cpu.Registers.MacHigh == 0xFFFF_8000, "SH-2 MAC.L negative saturation failed high word.");
    Require(cpu.Registers.MacLow == 0x0000_0000, "SH-2 MAC.L negative saturation failed low word.");

    WriteWord(code, 0x08, 0x312A);
    cpu.Reset();
    cpu.Registers.General[1] = 5;
    cpu.Registers.General[2] = 3;
    cpu.Registers.T = true;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 1, "SH-2 SUBC Rm,Rn failed no-borrow result.");
    Require(!cpu.Registers.T, "SH-2 SUBC Rm,Rn failed no-borrow T flag.");

    WriteWord(code, 0x08, 0x312A);
    cpu.Reset();
    cpu.Registers.General[1] = 0;
    cpu.Registers.General[2] = 1;
    cpu.Registers.T = false;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0xFFFF_FFFF, "SH-2 SUBC Rm,Rn failed borrow result.");
    Require(cpu.Registers.T, "SH-2 SUBC Rm,Rn failed borrow T flag.");

    WriteWord(code, 0x08, 0x2127);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0000_0001;
    cpu.Registers.General[2] = 0x8000_0000;
    cpu.StepInstruction();
    Require(!cpu.Registers.Q, "SH-2 DIV0S failed Q flag.");
    Require(cpu.Registers.M, "SH-2 DIV0S failed M flag.");
    Require(cpu.Registers.T, "SH-2 DIV0S failed mixed-sign T flag.");

    WriteWord(code, 0x08, 0x2127);
    cpu.Reset();
    cpu.Registers.General[1] = 0x8000_0000;
    cpu.Registers.General[2] = 0x8000_0000;
    cpu.StepInstruction();
    Require(cpu.Registers.Q, "SH-2 DIV0S failed negative Q flag.");
    Require(cpu.Registers.M, "SH-2 DIV0S failed negative M flag.");
    Require(!cpu.Registers.T, "SH-2 DIV0S failed same-sign T flag.");

    WriteWord(code, 0x08, 0x0019);
    cpu.Reset();
    cpu.Registers.M = true;
    cpu.Registers.Q = true;
    cpu.Registers.T = true;
    cpu.StepInstruction();
    Require(!cpu.Registers.M, "SH-2 DIV0U failed M flag.");
    Require(!cpu.Registers.Q, "SH-2 DIV0U failed Q flag.");
    Require(!cpu.Registers.T, "SH-2 DIV0U failed T flag.");

    WriteWord(code, 0x08, 0x3124);
    cpu.Reset();
    cpu.Registers.General[1] = 4;
    cpu.Registers.General[2] = 1;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 7, "SH-2 DIV1 failed subtract path result.");
    Require(!cpu.Registers.Q, "SH-2 DIV1 failed subtract path Q flag.");
    Require(!cpu.Registers.M, "SH-2 DIV1 failed subtract path M flag.");
    Require(cpu.Registers.T, "SH-2 DIV1 failed subtract path T flag.");

    WriteWord(code, 0x08, 0x3124);
    cpu.Reset();
    cpu.Registers.StatusRegister = 0x0000_0200;
    cpu.Registers.General[1] = 0x8000_0000;
    cpu.Registers.General[2] = 1;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 1, "SH-2 DIV1 failed add path result.");
    Require(!cpu.Registers.Q, "SH-2 DIV1 failed add path Q flag.");
    Require(cpu.Registers.M, "SH-2 DIV1 failed add path M flag.");
    Require(!cpu.Registers.T, "SH-2 DIV1 failed add path T flag.");

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

    WriteWord(code, 0x08, 0x4105);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0000_0003;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0x8000_0001, "SH-2 ROTR failed.");
    Require(cpu.Registers.T, "SH-2 ROTR did not move bit 0 into T.");

    WriteWord(code, 0x08, 0x4120);
    cpu.Reset();
    cpu.Registers.General[1] = 0x8000_0001;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0x0000_0002, "SH-2 SHAL failed.");
    Require(cpu.Registers.T, "SH-2 SHAL did not move bit 31 into T.");

    WriteWord(code, 0x08, 0x4125);
    cpu.Reset();
    cpu.Registers.General[1] = 0x0000_0003;
    cpu.Registers.T = true;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0x8000_0001, "SH-2 ROTCR failed carry-in/result.");
    Require(cpu.Registers.T, "SH-2 ROTCR did not move bit 0 into T.");

    WriteWord(code, 0x08, 0x612B);
    cpu.Reset();
    cpu.Registers.General[2] = 3;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0xFFFF_FFFD, "SH-2 NEG Rm,Rn failed.");

    WriteWord(code, 0x08, 0x611B);
    cpu.Reset();
    cpu.Registers.General[1] = 0;
    cpu.StepInstruction();
    Require(cpu.Registers.General[1] == 0, "SH-2 NEG Rn,Rn zero failed.");

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

    WriteWord(code, 0x08, 0x8D01);
    WriteWord(code, 0x0A, 0x4111);
    cpu.Reset();
    cpu.Registers.T = true;
    cpu.Registers.General[1] = 0x8000_0000;
    cpu.StepInstruction();
    Require(!cpu.Registers.T, "SH-2 BT/S delay slot test setup did not clear T.");
    Require(cpu.Registers.ProgramCounter == 0x0000_000E, "SH-2 BT/S used delay-slot T instead of latched T.");

    WriteWord(code, 0x08, 0x8F01);
    WriteWord(code, 0x0A, 0x4111);
    cpu.Reset();
    cpu.Registers.T = false;
    cpu.Registers.General[1] = 0;
    cpu.StepInstruction();
    Require(cpu.Registers.T, "SH-2 BF/S delay slot test setup did not set T.");
    Require(cpu.Registers.ProgramCounter == 0x0000_000E, "SH-2 BF/S used delay-slot T instead of latched T.");

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
