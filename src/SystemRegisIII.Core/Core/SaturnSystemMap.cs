using SystemRegisIII.Core.Core.Bus;
using SystemRegisIII.Core.Core.CdBlock;
using SystemRegisIII.Core.Core.Memory;
using SystemRegisIII.Core.Core.Scu;
using SystemRegisIII.Core.Core.Smpc;
using SystemRegisIII.Core.Core.Vdp1;
using SystemRegisIII.Core.Core.Vdp2;

namespace SystemRegisIII.Core.Core;

public sealed class SaturnSystemMap
{
    private readonly Vdp2RegisterBusDevice _vdp2RegisterTiming;

    private SaturnSystemMap(
        PageMappedBus bus,
        IMainMemory workRamLow,
        IMainMemory workRamHigh,
        DebugMemoryBusDevice vdp1Area,
        Vdp1RegisterBusDevice vdp1Registers,
        DebugMemoryBusDevice vdp2Vram,
        DebugMemoryBusDevice vdp2Cram,
        Vdp2RegisterBusDevice vdp2Registers,
        CdBlockRegisterBusDevice cdBlock,
        SaturnBackupRamBusDevice backupRam,
        FrtInputCaptureTriggerBusDevice slaveFrtInputCapture,
        FrtInputCaptureTriggerBusDevice masterFrtInputCapture,
        IReadOnlyList<IInspectableBusDevice> stubs)
    {
        Bus = bus;
        WorkRamLow = workRamLow;
        WorkRamHigh = workRamHigh;
        Vdp1Area = vdp1Area;
        Vdp1Registers = vdp1Registers;
        Vdp2Vram = vdp2Vram;
        Vdp2Cram = vdp2Cram;
        Vdp2Registers = vdp2Registers;
        _vdp2RegisterTiming = vdp2Registers;
        CdBlock = cdBlock;
        BackupRam = backupRam;
        SlaveFrtInputCapture = slaveFrtInputCapture;
        MasterFrtInputCapture = masterFrtInputCapture;
        Stubs = stubs;
    }

    public PageMappedBus Bus { get; }
    public IMainMemory WorkRamLow { get; }
    public IMainMemory WorkRamHigh { get; }
    public DebugMemoryBusDevice Vdp1Area { get; }
    public Vdp1RegisterBusDevice Vdp1Registers { get; }
    public DebugMemoryBusDevice Vdp2Vram { get; }
    public DebugMemoryBusDevice Vdp2Cram { get; }
    public DebugMemoryBusDevice Vdp2Registers { get; }
    public CdBlockRegisterBusDevice CdBlock { get; }
    public SaturnBackupRamBusDevice BackupRam { get; }
    public FrtInputCaptureTriggerBusDevice SlaveFrtInputCapture { get; }
    public FrtInputCaptureTriggerBusDevice MasterFrtInputCapture { get; }
    public IReadOnlyList<IInspectableBusDevice> Stubs { get; }

    public void AdvanceVdp2MasterInstructions(int instructionCount) =>
        _vdp2RegisterTiming.AdvanceMasterInstructions(instructionCount);

    public void NotifyVBlankIn() => Vdp1Registers.NotifyVBlankIn();

    public void NotifyVBlankOut() => Vdp1Registers.NotifyVBlankOut();

    public static SaturnSystemMap CreateBringup(BiosImage bios, SaturnBringupOptions? options = null)
    {
        options ??= new SaturnBringupOptions();
        var biosRom = new RomDevice("BIOS ROM", bios.Bytes);
        var workRamLow = new ByteArrayMemory("Work RAM Low", 1024 * 1024);
        IMainMemory workRamHigh = CreateHighWorkRam(options);
        var workRamHighDevice = (IBusDevice)workRamHigh;
        var smpcRegisters = new SmpcRegisterBusDevice(
            options.DigitalPadState,
            options.DigitalPadPeripheralData);
        var cdBlockRegisterMirror = new CdBlockRegisterBusDevice(
            options.DiscImage,
            options.MountedDiscInitialStatus);
        var backupRam = new SaturnBackupRamBusDevice();
        var scuRegisters = new ScuRegisterBusDevice();
        var scspArea = new StubBusDevice("SCSP Area").EnableWriteBack();
        if (options.SimulateScspCommandAck)
        {
            scspArea.AddReadByteProvider(0x700, static () => 0);
        }

        var vdp1Area = new DebugMemoryBusDevice("VDP1 Area", 1024 * 1024);
        var vdp1Registers = new Vdp1RegisterBusDevice();
        vdp1Registers.DrawCompleted += scuRegisters.RaiseVdp1DrawEnd;
        var vdp2Vram = new DebugMemoryBusDevice("VDP2 VRAM", 512 * 1024);
        var vdp2Cram = new DebugMemoryBusDevice("VDP2 CRAM", 4 * 1024);
        var vdp2Registers = new Vdp2RegisterBusDevice();
        var slaveFrtInputCapture = new FrtInputCaptureTriggerBusDevice("Slave FRT Input Capture");
        var masterFrtInputCapture = new FrtInputCaptureTriggerBusDevice("Master FRT Input Capture");

        IInspectableBusDevice[] stubs =
        [
            smpcRegisters,
            backupRam,
            new StubBusDevice("Cartridge / Expansion Area"),
            new StubBusDevice("CD Block Area"),
            new StubBusDevice("A-Bus Probe Area"),
            cdBlockRegisterMirror,
            scspArea,
            vdp1Area,
            vdp1Registers,
            vdp2Vram,
            vdp2Cram,
            vdp2Registers,
            scuRegisters,
            new StubBusDevice("SH-2 Internal Registers"),
            slaveFrtInputCapture,
            masterFrtInputCapture,
        ];

        var builder = new SaturnAddressMapBuilder()
            .Map(0x0000_0000, (uint)(biosRom.SizeBytes - 1), biosRom)
            .Map(0x0010_0000, 0x0017_FFFF, stubs[0])
            .Map(0x0018_0000, 0x001F_FFFF, stubs[1])
            .Map(0x0020_0000, 0x002F_FFFF, workRamLow)
            .Map(0x0100_0000, 0x017F_FFFF, slaveFrtInputCapture)
            .Map(0x0180_0000, 0x01FF_FFFF, masterFrtInputCapture)
            .Map(0x0200_0000, 0x020F_FFFF, stubs[3])
            .Map(0x0400_0000, 0x04FF_FFFF, stubs[4])
            .Map(0x0580_0000, 0x058F_FFFF, stubs[5])
            .Map(0x05A0_0000, 0x05BF_FFFF, stubs[6])
            .Map(0x05C0_0000, 0x05CF_FFFF, stubs[7])
            .Map(0x05D0_0000, 0x05DF_FFFF, stubs[8])
            .Map(0x05E0_0000, 0x05EF_FFFF, stubs[9])
            .Map(0x05F0_0000, 0x05F7_FFFF, stubs[10])
            .Map(0x05F8_0000, 0x05FB_FFFF, stubs[11])
            .Map(0x05FE_0000, 0x05FF_FFFF, stubs[12])
            .Map(0x0600_0000, 0x060F_FFFF, workRamHighDevice)
            .Map(0x0C00_0000, 0x0C0F_FFFF, workRamHighDevice)
            .Map(0xFFFF_8000, 0xFFFF_FFFF, stubs[13]);

        var bus = builder.Build();
        scuRegisters.ConnectDmaBus(bus);
        return new SaturnSystemMap(
            bus,
            workRamLow,
            workRamHigh,
            vdp1Area,
            vdp1Registers,
            vdp2Vram,
            vdp2Cram,
            vdp2Registers,
            cdBlockRegisterMirror,
            backupRam,
            slaveFrtInputCapture,
            masterFrtInputCapture,
            stubs);
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
