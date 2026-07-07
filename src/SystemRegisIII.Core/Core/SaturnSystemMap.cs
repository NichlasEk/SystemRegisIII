using SystemRegisIII.Core.Core.Bus;
using SystemRegisIII.Core.Core.Memory;

namespace SystemRegisIII.Core.Core;

public sealed class SaturnSystemMap
{
    private SaturnSystemMap(
        PageMappedBus bus,
        IMainMemory workRamLow,
        IMainMemory workRamHigh,
        IReadOnlyList<StubBusDevice> stubs)
    {
        Bus = bus;
        WorkRamLow = workRamLow;
        WorkRamHigh = workRamHigh;
        Stubs = stubs;
    }

    public PageMappedBus Bus { get; }
    public IMainMemory WorkRamLow { get; }
    public IMainMemory WorkRamHigh { get; }
    public IReadOnlyList<StubBusDevice> Stubs { get; }

    public static SaturnSystemMap CreateBringup(BiosImage bios, SaturnBringupOptions? options = null)
    {
        options ??= new SaturnBringupOptions();
        var biosRom = new RomDevice("BIOS ROM", bios.Bytes);
        var workRamLow = new ByteArrayMemory("Work RAM Low", 1024 * 1024);
        IMainMemory workRamHigh = CreateHighWorkRam(options);
        var workRamHighDevice = (IBusDevice)workRamHigh;
        var cdStatusMode = false;
        var cdBlockRegisterMirror = new StubBusDevice("CD Block Register Mirror")
            .AddReadWordProvider(0x090018, () => cdStatusMode ? (ushort)0x2000 : (ushort)0x0043)
            .AddReadWordProvider(0x09001C, () => cdStatusMode ? (ushort)0x0000 : (ushort)0x4442)
            .AddReadWordProvider(0x090020, () => cdStatusMode ? (ushort)0x0000 : (ushort)0x4C4F)
            .AddReadWordProvider(0x090024, () => cdStatusMode ? (ushort)0x0000 : (ushort)0x434B)
            .AddWriteObserver(0x090008, _ => cdStatusMode = true)
            .AddWriteObserver(0x090009, _ => cdStatusMode = true);

        StubBusDevice[] stubs =
        [
            new("SMPC Registers"),
            new("Backup RAM / Cartridge Area"),
            new("Cartridge / Expansion Area"),
            new("CD Block Area"),
            new("A-Bus Probe Area"),
            cdBlockRegisterMirror,
            new("SCSP Area"),
            new("VDP1 Area"),
            new("VDP2 Area"),
            new("VDP2 CRAM Area"),
            new("SCU / System Control Area"),
            new("SH-2 Internal Registers"),
        ];

        var builder = new SaturnAddressMapBuilder()
            .Map(0x0000_0000, (uint)(biosRom.SizeBytes - 1), biosRom)
            .Map(0x0010_0000, 0x0010_007F, stubs[0])
            .Map(0x0020_0000, 0x002F_FFFF, workRamLow)
            .Map(0x0100_0000, 0x010F_FFFF, stubs[1])
            .Map(0x0180_0000, 0x01FF_FFFF, stubs[2])
            .Map(0x0200_0000, 0x020F_FFFF, stubs[3])
            .Map(0x0400_0000, 0x04FF_FFFF, stubs[4])
            .Map(0x0580_0000, 0x058F_FFFF, stubs[5])
            .Map(0x05A0_0000, 0x05AF_FFFF, stubs[6])
            .Map(0x05B0_0000, 0x05BF_FFFF, stubs[7])
            .Map(0x05C0_0000, 0x05DF_FFFF, stubs[8])
            .Map(0x05E0_0000, 0x05EF_FFFF, stubs[9])
            .Map(0x05F0_0000, 0x05FF_FFFF, stubs[10])
            .Map(0x0600_0000, 0x060F_FFFF, workRamHighDevice)
            .Map(0x6000_0000, 0x600F_FFFF, workRamHighDevice)
            .Map(0xFFFF_8000, 0xFFFF_FFFF, stubs[11]);

        return new SaturnSystemMap(builder.Build(), workRamLow, workRamHigh, stubs);
    }

    private static IMainMemory CreateHighWorkRam(SaturnBringupOptions options)
    {
        var highRam = new ByteArrayMemory("Work RAM High", 1024 * 1024);
        if (!options.SimulateSlaveReady)
        {
            return highRam;
        }

        var overlay = new OverlayMemory(highRam);
        overlay.AddReadOnlyLong(0x240, 0x3252_4459);
        return overlay;
    }
}
