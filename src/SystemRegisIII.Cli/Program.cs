using SystemRegisIII.Cli;
using SystemRegisIII.Core.Core;
using SystemRegisIII.Core.Core.Bus;
using SystemRegisIII.Core.Core.CdBlock;
using SystemRegisIII.Core.Core.Cpu.Sh2;
using SystemRegisIII.Core.Core.Memory;
using SystemRegisIII.Core.Core.Scu;
using SystemRegisIII.Core.Core.Smpc;
using SystemRegisIII.Core.Host.Input;
using SystemRegisIII.Core.Tools.TraceViewer;

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0 || Has(args, "--help") || Has(args, "-h"))
    {
        PrintUsage();
        return 0;
    }

    var command = args[0];
    return command switch
    {
        "run" => RunBios(args[1..]),
        _ => Fail($"Unknown command '{command}'."),
    };
}

static int RunBios(string[] args)
{
    var biosPath = GetOption(args, "--bios");
    if (biosPath is null)
    {
        return Fail("Missing required option: --bios <path>");
    }

    var discPath = GetOption(args, "--disc");
    var cdStatus = GetCdStatusOption(args);
    var instructionCount = GetIntOption(args, "--instructions", defaultValue: 64);
    var vblankInterval = Math.Max(2, GetIntOption(args, "--vblank-interval", defaultValue: 1_000_000));
    var vblankOutOffset = vblankInterval / 2;
    var traceEnabled = Has(args, "--trace");
    var simulateSlaveReady = Has(args, "--simulate-slave-ready");
    var dualSh2 = Has(args, "--dual-sh2");
    var digitalPadState = GetPadOption(args);
    var digitalPadPeripheralData = GetPadRawOption(args);

    var bios = BiosImageLoader.Load(biosPath);
    using var discImage = discPath is null ? null : OpenDiscImage(discPath);
    var trace = new RingTraceEventSink(capacity: Math.Clamp(instructionCount * 8, 512, 8_192));
    var systemMap = SaturnSystemMap.CreateBringup(
        bios,
        new SaturnBringupOptions
        {
            SimulateSlaveReady = simulateSlaveReady,
            DiscImage = discImage,
            MountedDiscInitialStatus = cdStatus,
            DigitalPadState = digitalPadState,
            DigitalPadPeripheralData = digitalPadPeripheralData,
        });
    var addressMap = systemMap.Bus;
    var masterInternalBus = new Sh2InternalRegisterBus(addressMap, Sh2CpuRole.Master);
    var slaveInternalBus = dualSh2 ? new Sh2InternalRegisterBus(addressMap, Sh2CpuRole.Slave) : null;
    Sh2Cpu? master = null;
    Sh2Cpu? slave = null;
    var masterCdStatusBufferWatch = new WatchedBus(
        masterInternalBus,
        0x0601_FF60,
        0x0601_FF8F,
        () => GetWatchContext(master));
    var masterFlagWatch = new WatchedBus(
        masterCdStatusBufferWatch,
        0x0602_0230,
        0x0602_024F,
        () => GetWatchContext(master));
    var masterCallbackWatch = new WatchedBus(
        masterFlagWatch,
        0x0602_0720,
        0x0602_075F,
        () => GetWatchContext(master));
    var masterTransformTableWatch = new WatchedBus(
        masterCallbackWatch,
        0x0603_0180,
        0x0603_028F,
        () => GetWatchContext(master));
    var masterGeometrySourceWatch = new WatchedBus(
        masterTransformTableWatch,
        0x0604_9E00,
        0x0604_A9FF,
        () => GetWatchContext(master));
    var masterMenuStateWatch = new WatchedBus(
        masterGeometrySourceWatch,
        0x060B_3060,
        0x060B_307F,
        () => GetWatchContext(master));
    var masterSmpcWatch = new WatchedBus(
        masterMenuStateWatch,
        0x0010_0000,
        0x0010_007F,
        () => GetWatchContext(master));
    var masterCdBlockWatch = new WatchedBus(
        masterSmpcWatch,
        0x0589_0000,
        0x0589_002F,
        () => GetWatchContext(master));
    WatchedBus? slaveFlagWatch = slaveInternalBus is null
        ? null
        : new WatchedBus(
            slaveInternalBus,
            0x0602_0230,
            0x0602_024F,
            () => GetWatchContext(slave));
    ISaturnBus masterBus = traceEnabled ? new TracingBus(masterCdBlockWatch, trace) : masterCdBlockWatch;
    ISaturnBus? slaveBus = slaveInternalBus is null
        ? null
        : traceEnabled ? new TracingBus(slaveFlagWatch!, trace) : slaveFlagWatch!;

    master = new Sh2Cpu("Master SH-2", masterBus, resetVectorAddress: 0x0000_0000, trace);
    slave = slaveBus is not null ? new Sh2Cpu("Slave SH-2", slaveBus, resetVectorAddress: 0x0000_0008, trace) : null;
    var smpc = systemMap.Stubs.OfType<SmpcRegisterBusDevice>().Single();
    var scu = systemMap.Stubs.OfType<ScuRegisterBusDevice>().Single();
    master.Reset();
    slave?.Reset();
    var masterPcHits = new Dictionary<uint, long>();
    var masterHandlerPcHits = new Dictionary<uint, long>();
    var masterCallbackPcHits = new Dictionary<uint, long>();
    var masterSetupPcHits = new Dictionary<uint, long>();
    var slavePcHits = dualSh2 ? new Dictionary<uint, long>() : null;
    var busFaults = new List<string>();
    var slaveWasEnabled = smpc.SlaveSh2Enabled;
    var interruptProbe = new ScuInterruptProbe();
    var normalizeProbe = new PcWindowProbe(
        "Master SH-2 normalize probe",
        0x0601_2C6C,
        0x0601_2CC2,
        capacity: 64,
        focusedProcedureRegister: 0x0601_1690);
    var geometryProducerProbe = new PcWindowProbe(
        "Master SH-2 geometry producer probe",
        0x0602_E924,
        0x0602_EA90,
        capacity: 96,
        focusedProcedureRegister: 0x0601_15BC);
    var geometryLargeProducerProbe = new PcWindowProbe(
        "Master SH-2 first-large geometry producer probe",
        0x0602_E924,
        0x0602_EA90,
        capacity: 96,
        focusedProcedureRegister: 0x0601_15BC,
        focusedR6Start: 0x0606_3D54,
        focusedR6End: 0x0606_3DD0);

    for (var i = 0; i < instructionCount; i++)
    {
        while (smpc.TryConsumeInterrupt())
        {
            scu.RaiseSmpc();
        }

        if (i > 0 && i % vblankInterval == 0)
        {
            scu.RaiseVBlankIn();
        }
        else if (i > 0 && i % vblankInterval == vblankOutOffset)
        {
            scu.RaiseVBlankOut();
        }

        if (scu.HasPendingVBlankIn)
        {
            var interruptedPc = master.Registers.ProgramCounter;
            var accepted = master.RequestInterrupt(15, 0x40);
            interruptProbe.RecordVBlankIn(accepted, interruptedPc);
            if (accepted)
            {
                scu.AcknowledgeVBlankIn();
            }
        }
        else if (scu.HasPendingVBlankOut)
        {
            var interruptedPc = master.Registers.ProgramCounter;
            var accepted = master.RequestInterrupt(14, 0x41);
            interruptProbe.RecordVBlankOut(accepted, interruptedPc);
            if (accepted)
            {
                scu.AcknowledgeVBlankOut();
            }
        }
        else if (scu.HasPendingSmpc)
        {
            var interruptedPc = master.Registers.ProgramCounter;
            var accepted = master.RequestInterrupt(8, 0x47);
            interruptProbe.RecordSmpc(accepted, interruptedPc);
            if (accepted)
            {
                scu.AcknowledgeSmpc();
            }
        }

        var masterPc = master.Registers.ProgramCounter;
        RecordPc(masterPcHits, masterPc);
        normalizeProbe.Record(i, master, addressMap, FormatLoopProbeInstruction);
        geometryProducerProbe.Record(i, master, addressMap, FormatLoopProbeInstruction);
        geometryLargeProducerProbe.Record(i, master, addressMap, FormatLoopProbeInstruction);
        if (masterPc is >= 0x0600_083C and <= 0x0600_094C)
        {
            RecordPc(masterHandlerPcHits, masterPc);
        }
        if (masterPc is >= 0x0602_8900 and <= 0x0602_8E20)
        {
            RecordPc(masterCallbackPcHits, masterPc);
        }
        if (masterPc is >= 0x0602_8C40 and <= 0x0602_8D40)
        {
            RecordPc(masterSetupPcHits, masterPc);
        }

        if (!TryStep(master, trace, busFaults))
        {
            break;
        }

        if (slave is not null && smpc.SlaveSh2Enabled)
        {
            if (!slaveWasEnabled)
            {
                slave.Reset();
            }

            RecordPc(slavePcHits!, slave.Registers.ProgramCounter);
            if (!TryStep(slave, trace, busFaults))
            {
                break;
            }
        }

        slaveWasEnabled = smpc.SlaveSh2Enabled;
    }

    Console.WriteLine($"BIOS: {bios.Name}");
    Console.WriteLine($"BIOS bytes: {bios.Bytes.Length:N0}");
    Console.WriteLine($"Master SH-2 PC: 0x{master.Registers.ProgramCounter:X8}");
    Console.WriteLine($"Master SH-2 SR: 0x{master.Registers.StatusRegister:X8}");
    if (slave is not null)
    {
        Console.WriteLine($"Slave SH-2 PC: 0x{slave.Registers.ProgramCounter:X8}");
        Console.WriteLine($"Slave SH-2 SR: 0x{slave.Registers.StatusRegister:X8}");
    }

    Console.WriteLine($"Slave-ready simulation: {(simulateSlaveReady ? "on" : "off")}");
    Console.WriteLine($"Dual SH-2 interleave: {(dualSh2 ? "on" : "off")}");
    Console.WriteLine($"VBlank interval: {vblankInterval:N0} instructions");
    if (slave is not null)
    {
        Console.WriteLine($"Slave SH-2 enabled by SMPC: {(smpc.SlaveSh2Enabled ? "on" : "off")}");
    }
    PrintMemoryActivity("Work RAM Low", systemMap.WorkRamLow);
    PrintMemoryActivity("Work RAM High", systemMap.WorkRamHigh);
    PrintInternalActivity("Master SH-2 internal", masterInternalBus);
    if (slaveInternalBus is not null)
    {
        PrintInternalActivity("Slave SH-2 internal", slaveInternalBus);
    }

    PrintUnimplemented(master);
    if (slave is not null)
    {
        PrintUnimplemented(slave);
    }

    PrintHotProgramCounters(master.Name, masterPcHits);
    PrintHotProgramCounters($"{master.Name} handler", masterHandlerPcHits);
    PrintHotProgramCounters($"{master.Name} callback", masterCallbackPcHits);
    PrintHotProgramCounters($"{master.Name} setup", masterSetupPcHits);
    if (slave is not null)
    {
        PrintHotProgramCounters(slave.Name, slavePcHits!);
    }

    PrintMasterGbrLoopProbe(master, addressMap);
    normalizeProbe.Print();
    geometryProducerProbe.Print();
    geometryLargeProducerProbe.Print();
    PrintMasterPcProbe(master, addressMap);
    PrintWatchWindow("Master CD status buffer watch", masterCdStatusBufferWatch);
    PrintWatchWindow("Master flag watch", masterFlagWatch);
    PrintWatchWindow("Master callback-state watch", masterCallbackWatch);
    PrintWatchWindow("Master transform-table watch", masterTransformTableWatch);
    PrintWatchWindow("Master geometry-source watch", masterGeometrySourceWatch);
    PrintWatchWindow("Master BIOS menu-state watch", masterMenuStateWatch);
    PrintWatchWindow("Master SMPC watch", masterSmpcWatch);
    PrintWatchWindow("Master CD Block watch", masterCdBlockWatch);
    if (slaveFlagWatch is not null)
    {
        PrintWatchWindow("Slave flag watch", slaveFlagWatch);
    }

    PrintScuInterruptState(scu, interruptProbe);
    PrintBusFaults(busFaults);
    Console.WriteLine($"Mapped regions: {addressMap.Regions.Count}");
    PrintTouchedStubs(systemMap);

    if (traceEnabled)
    {
        Console.WriteLine();
        Console.WriteLine("Trace:");
        foreach (var traceEvent in trace.Events)
        {
            Console.WriteLine($"[{traceEvent.Source}] {traceEvent.Message}");
        }
    }

    return 0;
}

static void RecordPc(Dictionary<uint, long> hits, uint pc)
{
    hits.TryGetValue(pc, out var count);
    hits[pc] = count + 1;
}

static void PrintHotProgramCounters(string name, Dictionary<uint, long> hits)
{
    var hot = hits
        .OrderByDescending(static pair => pair.Value)
        .Take(IsDetailedHotPcReport(name) ? 32 : 4)
        .Where(static pair => pair.Value > 1)
        .ToArray();

    if (hot.Length == 0)
    {
        return;
    }

    Console.WriteLine($"{name} hot PCs:");
    foreach (var (pc, count) in hot)
    {
        Console.WriteLine($"  0x{pc:X8}: {count:N0}");
    }
}

static bool IsDetailedHotPcReport(string name) =>
    name.Contains("handler", StringComparison.Ordinal)
    || name.Contains("callback", StringComparison.Ordinal)
    || name.Contains("setup", StringComparison.Ordinal);

static WatchedAccessContext? GetWatchContext(Sh2Cpu? cpu) =>
    cpu is null
        ? null
        : new WatchedAccessContext(
            cpu.CurrentInstructionProgramCounter,
            cpu.Registers.ProcedureRegister,
            cpu.Registers.GlobalBaseRegister,
            cpu.Registers.General[0],
            cpu.Registers.General[2],
            cpu.Registers.General[3],
            cpu.Registers.General[4],
            cpu.Registers.General[5],
            cpu.Registers.General[6],
            cpu.Registers.General[9],
            cpu.Registers.General[10],
            cpu.Registers.General[12],
            cpu.Registers.General[13]);

static void PrintMasterGbrLoopProbe(Sh2Cpu master, ISaturnBus bus)
{
    var pc = master.Registers.ProgramCounter;
    if (pc is < 0x0602_8314 or > 0x0602_831A)
    {
        return;
    }

    var gbr = master.Registers.GlobalBaseRegister;
    var watchedAddress = gbr + (0x90u * 4);
    Console.WriteLine("Master SH-2 GBR loop probe:");
    Console.WriteLine($"  GBR=0x{gbr:X8} R0=0x{master.Registers.General[0]:X8} R4=0x{master.Registers.General[4]:X8}");
    try
    {
        Console.WriteLine($"  [GBR+0x240]=[0x{watchedAddress:X8}]=0x{bus.ReadLong(watchedAddress):X8}");
        PrintWordWindow(bus, watchedAddress - 0x10, 12, "  flag window");
        PrintInstructionWindow(bus, 0x0602_8300, 16, "  code window");
        PrintWordWindow(bus, 0x0602_8350, 32, "  loop literals 0x06028350");
        PrintInstructionWindow(bus, 0x0600_0830, 32, "  handler window 0x06000830");
        PrintInstructionWindow(bus, 0x0600_08E0, 40, "  handler window 0x060008E0");
        PrintInstructionWindow(bus, 0x0600_0930, 64, "  handler window 0x06000930");
        PrintWordWindow(bus, 0x0600_0340, 16, "  handler data 0x06000340");
        PrintInstructionWindow(bus, 0x0600_0980, 32, "  handler target 0x06000980");
        PrintWordWindow(bus, 0x0600_0A00, 32, "  handler target 0x06000A00");
        PrintWordWindow(bus, 0x0602_0720, 32, "  flag callback area 0x06020720");
        PrintInstructionWindow(bus, 0x0602_81C0, 48, "  flag writer routine 0x060281C0");
        PrintInstructionWindow(bus, 0x0602_9EA0, 48, "  state init writer routine 0x06029EA0");
        PrintInstructionWindow(bus, 0x0602_8920, 48, "  vblank helper target 0x06028920");
        PrintInstructionWindow(bus, 0x0602_8C40, 80, "  wait setup target 0x06028C40");
        PrintInstructionWindow(bus, 0x0602_8D60, 48, "  vblank callback 0x06028D60");
        PrintInstructionWindow(bus, 0x0602_8D98, 48, "  vblank callback 0x06028D98");
    }
    catch (BusFaultException exception)
    {
        Console.WriteLine($"  [GBR+0x240]=[0x{watchedAddress:X8}] faulted at 0x{exception.Address:X8}");
    }
}

static void PrintMasterPcProbe(Sh2Cpu master, ISaturnBus bus)
{
    var pc = master.Registers.ProgramCounter;
    if (pc is >= 0x0602_BD20 and <= 0x0602_BD80)
    {
        PrintMasterPcProbeWindow(
            master,
            bus,
            codeStart: 0x0602_BD20,
            codeWords: 48,
            dataStart: 0x0602_BD80,
            dataWords: 32,
            precedingStart: 0x0602_BC80,
            precedingWords: 64);
    }
    else if (pc is >= 0x0604_0000 and <= 0x0604_0300)
    {
        PrintMasterPcProbeWindow(
            master,
            bus,
            codeStart: 0x0604_0000,
            codeWords: 320,
            dataStart: 0x0602_0230,
            dataWords: 16,
            precedingStart: 0x0604_4C60,
            precedingWords: 64);
        PrintInstructionWindow(bus, 0x0604_01B0, 96, "  frame wait loop 0x060401B0");
        PrintInstructionWindow(bus, 0x0604_14A0, 64, "  caller/status sequence 0x060414A0");
        PrintInstructionWindow(bus, 0x0604_0340, 128, "  menu-state reader/writer 0x06040340");
        PrintInstructionWindow(bus, 0x0604_3A80, 128, "  menu-state consumer 0x06043A80");
        PrintInstructionWindow(bus, 0x0604_3E80, 80, "  menu-state consumer 0x06043E80");
        PrintWordWindow(bus, 0x0601_FF60, 24, "  CD status buffers 0x0601FF60");
        PrintWordWindow(bus, 0x060B_3060, 16, "  BIOS menu state 0x060B3060");
    }
    else if (pc is >= 0x0601_3300 and <= 0x0601_3700)
    {
        PrintMasterPcProbeWindow(
            master,
            bus,
            codeStart: 0x0601_3300,
            codeWords: 448,
            dataStart: 0x0601_FF60,
            dataWords: 24,
            precedingStart: 0x0601_1200,
            precedingWords: 96);
        PrintInstructionWindow(bus, 0x0000_3B80, 128, "  CD status caller 0x00003B80");
        PrintInstructionWindow(bus, 0x0000_42B0, 96, "  CD response loop 0x000042B0");
        PrintWordWindow(bus, 0x0600_03A0, 8, "  CD HIRQ accumulator 0x060003A0");
        PrintWordWindow(bus, 0x0602_0230, 16, "  BIOS flags 0x06020230");
    }
    else if (pc is >= 0x0601_2C40 and <= 0x0601_2CC0)
    {
        PrintMasterPcProbeWindow(
            master,
            bus,
            codeStart: 0x0601_2C40,
            codeWords: 96,
            dataStart: 0x0601_FF60,
            dataWords: 24,
            precedingStart: 0x0601_2B80,
            precedingWords: 96);
        PrintInstructionWindow(bus, 0x0601_3300, 128, "  CD/status helper 0x06013300");
        PrintInstructionWindow(bus, 0x0601_1640, 80, "  normalize caller 0x06011640");
        PrintInstructionWindow(bus, 0x0601_2D80, 144, "  normalize caller/tail 0x06012D80");
        PrintInstructionWindow(bus, 0x0602_E2C0, 112, "  transform setup 0x0602E2C0");
        PrintInstructionWindow(bus, 0x0602_E680, 192, "  transform builder 0x0602E680");
        PrintInstructionWindow(bus, 0x0602_E900, 256, "  geometry producer 0x0602E900");
        PrintWordWindow(bus, 0x0604_C280, 96, "  geometry producer source window 0x0604C280");
        PrintWordWindow(bus, 0x0606_3D40, 96, "  geometry producer source window 0x06063D40");
        PrintInstructionWindow(bus, 0x0600_0830, 80, "  interrupt handler 0x06000830");
        PrintInstructionWindow(bus, 0x0600_08F0, 128, "  interrupt common handler 0x060008F0");
        PrintInstructionWindow(bus, 0x0602_8D60, 96, "  VBlank callback 0x06028D60");
        PrintWordWindow(bus, 0x0604_9E40, 96, "  geometry source vector 0x06049E40");
        PrintWordWindow(bus, 0x0602_AEE8, 64, "  normalize magnitude table 0x0602AEE8");
        PrintWordWindow(bus, 0x0602_AF20, 64, "  normalize shift table 0x0602AF20");
        PrintWordWindow(bus, 0x060B_3060, 16, "  BIOS menu state 0x060B3060");
        PrintWordWindow(bus, 0x0010_0000, 64, "  SMPC registers 0x00100000");
    }
    else if (pc is >= 0x0601_2D80 and <= 0x0601_2EA0)
    {
        PrintMasterPcProbeWindow(
            master,
            bus,
            codeStart: 0x0601_2D80,
            codeWords: 160,
            dataStart: 0x0602_AEE0,
            dataWords: 40,
            precedingStart: 0x0602_E400,
            precedingWords: 96);
        PrintWordWindow(bus, 0x0602_AF20, 24, "  table 0x0602AF20");
        PrintWordWindow(bus, 0x0601_FF60, 24, "  CD status buffers 0x0601FF60");
        PrintWordWindow(bus, master.Registers.General[12], 32, $"  dynamic source R12=0x{master.Registers.General[12]:X8}");
    }
    else if (pc is >= 0x0604_0B70 and <= 0x0604_0C20)
    {
        PrintMasterPcProbeWindow(
            master,
            bus,
            codeStart: 0x0604_0B70,
            codeWords: 96,
            dataStart: 0x0604_0C20,
            dataWords: 32,
            precedingStart: 0x0604_1460,
            precedingWords: 64);
        PrintInstructionWindow(bus, 0x0604_22A0, 128, "  CD/status callback 0x060422A0");
        PrintInstructionWindow(bus, 0x0604_2450, 128, "  CD/status poller 0x06042450");
        PrintWordWindow(bus, 0x0601_FF60, 24, "  CD status buffers 0x0601FF60");
    }
    else if (pc is >= 0x060F_0A00 and <= 0x060F_0A40)
    {
        PrintMasterPcProbeWindow(
            master,
            bus,
            codeStart: 0x060F_09E0,
            codeWords: 80,
            dataStart: 0x060F_0A80,
            dataWords: 32,
            precedingStart: 0x060F_3480,
            precedingWords: 96);
        PrintInstructionWindow(bus, 0x060F_2200, 96, "  Work RAM caller 0x060F2200");
        PrintWordWindow(bus, 0x0600_0350, 16, "  Work RAM table root 0x06000350");
        PrintWordWindow(bus, 0x0601_FF60, 24, "  CD/status stack 0x0601FF60");
    }
    else if (pc is >= 0x0000_32D0 and <= 0x0000_3310)
    {
        PrintMasterPcProbeWindow(
            master,
            bus,
            codeStart: 0x0000_32D0,
            codeWords: 64,
            dataStart: 0x0000_3310,
            dataWords: 32,
            precedingStart: 0x0000_40C0,
            precedingWords: 64);
        PrintInstructionWindow(bus, 0x0000_3FF0, 96, "  CD command helper 0x00003FF0");
        PrintInstructionWindow(bus, 0x0000_40D0, 64, "  CD HIRQ wait helper 0x000040D0");
        PrintWordWindow(bus, 0x0600_03A0, 8, "  CD HIRQ accumulator 0x060003A0");
        PrintWordWindow(bus, 0x0601_FF60, 24, "  CD status buffers 0x0601FF60");
    }
    else if (pc is >= 0x0000_4B70 and <= 0x0000_4C10)
    {
        PrintMasterPcProbeWindow(
            master,
            bus,
            codeStart: 0x0000_4B70,
            codeWords: 96,
            dataStart: 0x0000_4BB8,
            dataWords: 48,
            precedingStart: 0x0000_4180,
            precedingWords: 96);
        PrintInstructionWindow(bus, 0x0000_4240, 64, "  CD helper continuation 0x00004240");
        PrintInstructionWindow(bus, 0x0000_4280, 64, "  CD response helper 0x00004280");
        PrintInstructionWindow(bus, 0x0000_42BE, 64, "  CD response copy 0x000042BE");
        PrintWordWindow(bus, 0x0600_03A0, 8, "  CD HIRQ accumulator 0x060003A0");
    }
    else if (pc is >= 0x0000_4C20 and <= 0x0000_4CA0)
    {
        PrintMasterPcProbeWindow(
            master,
            bus,
            codeStart: 0x0000_4C20,
            codeWords: 64,
            dataStart: 0x0000_4CA0,
            dataWords: 32,
            precedingStart: 0x0000_4B80,
            precedingWords: 64);
        PrintInstructionWindow(bus, 0x0000_3B80, 96, "  CD status caller 0x00003B80");
        PrintWordWindow(bus, 0x0601_FF60, 24, "  CD status buffers 0x0601FF60");
    }
}

static void PrintMasterPcProbeWindow(
    Sh2Cpu master,
    ISaturnBus bus,
    uint codeStart,
    int codeWords,
    uint dataStart,
    int dataWords,
    uint precedingStart,
    int precedingWords)
{
    var pc = master.Registers.ProgramCounter;
    Console.WriteLine("Master SH-2 PC probe:");
    Console.WriteLine(
        $"  PC=0x{pc:X8} PR=0x{master.Registers.ProcedureRegister:X8} SR=0x{master.Registers.StatusRegister:X8} GBR=0x{master.Registers.GlobalBaseRegister:X8}");
    Console.WriteLine(
        $"  R0=0x{master.Registers.General[0]:X8} R1=0x{master.Registers.General[1]:X8} R2=0x{master.Registers.General[2]:X8} R3=0x{master.Registers.General[3]:X8}");
    Console.WriteLine(
        $"  R4=0x{master.Registers.General[4]:X8} R5=0x{master.Registers.General[5]:X8} R6=0x{master.Registers.General[6]:X8} R7=0x{master.Registers.General[7]:X8}");
    Console.WriteLine(
        $"  R8=0x{master.Registers.General[8]:X8} R9=0x{master.Registers.General[9]:X8} R10=0x{master.Registers.General[10]:X8} R11=0x{master.Registers.General[11]:X8}");
    Console.WriteLine(
        $"  R12=0x{master.Registers.General[12]:X8} R13=0x{master.Registers.General[13]:X8} R14=0x{master.Registers.General[14]:X8} R15=0x{master.Registers.General[15]:X8}");

    try
    {
        PrintInstructionWindow(bus, codeStart, codeWords, $"  code window 0x{codeStart:X8}");
        PrintWordWindow(bus, dataStart, dataWords, $"  data/literals 0x{dataStart:X8}");
        PrintInstructionWindow(bus, precedingStart, precedingWords, $"  preceding code 0x{precedingStart:X8}");
    }
    catch (BusFaultException exception)
    {
        Console.WriteLine($"  probe faulted at 0x{exception.Address:X8}");
    }
}

static void PrintScuInterruptState(ScuRegisterBusDevice scu, ScuInterruptProbe probe)
{
    if (scu.ReadCount == 0 && scu.WriteCount == 0 && scu.InterruptStatus == 0)
    {
        return;
    }

    Console.WriteLine("SCU interrupt state:");
    Console.WriteLine(
        $"  mask=0x{scu.InterruptMask:X8} status=0x{scu.InterruptStatus:X8} vblank-in-pending={scu.HasPendingVBlankIn} vblank-out-pending={scu.HasPendingVBlankOut} smpc-pending={scu.HasPendingSmpc}");
    Console.WriteLine($"  last status write=0x{scu.LastInterruptStatusWrite:X8}");
    probe.Print();
}

static void PrintWatchWindow(string label, WatchedBus watch)
{
    Console.WriteLine($"{label}: reads={watch.ReadCount:N0} writes={watch.WriteCount:N0}");
    if (watch.ReadCount == 0 && watch.WriteCount == 0)
    {
        return;
    }

    if (watch.ReadCount > 0)
    {
        Console.WriteLine(
            $"  read first=0x{watch.FirstReadAddress!.Value:X8} last=0x{watch.LastReadAddress!.Value:X8} value=0x{watch.LastReadValue!.Value:X8}");
        Console.WriteLine("  hot reads:");
        foreach (var (address, count, value, context) in watch.GetHotReads(8))
        {
            Console.WriteLine($"    0x{address:X8}: {count:N0} last=0x{value:X8} {FormatWatchContext(context)}");
        }
    }

    if (watch.WriteCount > 0)
    {
        Console.WriteLine(
            $"  write first=0x{watch.FirstWriteAddress!.Value:X8} last=0x{watch.LastWriteAddress!.Value:X8} value=0x{watch.LastWriteValue!.Value:X8}");
        Console.WriteLine("  hot writes:");
        foreach (var (address, count, value, context) in watch.GetHotWrites(12))
        {
            Console.WriteLine($"    0x{address:X8}: {count:N0} last=0x{value:X8} {FormatWatchContext(context)}");
        }

        Console.WriteLine("  recent writes:");
        foreach (var write in watch.RecentWrites.TakeLast(12))
        {
            Console.WriteLine($"    {FormatWatchContext(write.Context)} address=0x{write.Address:X8} value=0x{write.Value:X8}");
        }

        if (watch.LargeWriteCount > 0)
        {
            Console.WriteLine($"  large signed writes: {watch.LargeWriteCount:N0}");
            PrintLargeWrites("first large writes", watch.FirstLargeWrites);
            PrintLargeWrites("recent large writes", watch.RecentLargeWrites);
        }
    }

    if (watch.ReadCount > 0)
    {
        Console.WriteLine("  recent reads:");
        foreach (var read in watch.RecentReads.TakeLast(12))
        {
            Console.WriteLine($"    {FormatWatchContext(read.Context)} address=0x{read.Address:X8} value=0x{read.Value:X8}");
        }
    }
}

static void PrintLargeWrites(string label, IReadOnlyList<WatchedWrite> writes)
{
    if (writes.Count == 0)
    {
        return;
    }

    Console.WriteLine($"  {label}:");
    foreach (var write in writes)
    {
        Console.WriteLine($"    {FormatWatchContext(write.Context)} address=0x{write.Address:X8} value=0x{write.Value:X8}");
    }
}

static string FormatWatchContext(WatchedAccessContext? context)
{
    if (context is not { } value)
    {
        return "pc=<unknown>";
    }

    var pc = value.ProgramCounter is { } programCounter ? $"0x{programCounter:X8}" : "<unknown>";
    return $"pc={pc} pr=0x{value.ProcedureRegister:X8} gbr=0x{value.GlobalBaseRegister:X8} r0=0x{value.R0:X8} r2=0x{value.R2:X8} r3=0x{value.R3:X8} r4=0x{value.R4:X8} r5=0x{value.R5:X8} r6=0x{value.R6:X8} r9=0x{value.R9:X8} r10=0x{value.R10:X8} r12=0x{value.R12:X8} r13=0x{value.R13:X8}";
}

static string FormatLoopProbeInstruction(ISaturnBus bus, uint address)
{
    try
    {
        var opcode = bus.ReadWord(address);
        return $"0x{opcode:X4} {DecodeSh2Instruction(bus, address, opcode)}";
    }
    catch (BusFaultException exception)
    {
        return $"fault 0x{exception.Address:X8}";
    }
}

static void PrintWordWindow(ISaturnBus bus, uint startAddress, int wordCount, string label)
{
    Console.WriteLine($"{label}:");
    for (var index = 0; index < wordCount; index++)
    {
        var address = startAddress + (uint)(index * 2);
        Console.WriteLine($"    0x{address:X8}: 0x{bus.ReadWord(address):X4}");
    }
}

static void PrintInstructionWindow(ISaturnBus bus, uint startAddress, int wordCount, string label)
{
    Console.WriteLine($"{label}:");
    for (var index = 0; index < wordCount; index++)
    {
        var address = startAddress + (uint)(index * 2);
        var opcode = bus.ReadWord(address);
        Console.WriteLine($"    0x{address:X8}: 0x{opcode:X4}  {DecodeSh2Instruction(bus, address, opcode)}");
    }
}

static string DecodeSh2Instruction(ISaturnBus bus, uint address, ushort opcode)
{
    if (opcode == 0x0009)
    {
        return "NOP";
    }

    if (opcode == 0x000B)
    {
        return "RTS";
    }

    if (opcode == 0x002B)
    {
        return "RTE";
    }

    if ((opcode & 0xF000) == 0xA000)
    {
        return $"BRA 0x{BranchTarget(address, opcode & 0x0FFF):X8}";
    }

    if ((opcode & 0xF000) == 0xB000)
    {
        return $"BSR 0x{BranchTarget(address, opcode & 0x0FFF):X8}";
    }

    if ((opcode & 0xF000) == 0x1000)
    {
        var destination = (opcode >> 8) & 0xF;
        var source = (opcode >> 4) & 0xF;
        var displacement = opcode & 0xF;
        return $"MOV.L R{source},@(0x{displacement:X1},R{destination})";
    }

    if ((opcode & 0xF00F) == 0x0007)
    {
        var destination = (opcode >> 8) & 0xF;
        var source = (opcode >> 4) & 0xF;
        return $"MUL.L R{source},R{destination}";
    }

    if ((opcode & 0xF00F) == 0x000F)
    {
        var destination = (opcode >> 8) & 0xF;
        var source = (opcode >> 4) & 0xF;
        return $"MAC.L @R{source}+,@R{destination}+";
    }

    if ((opcode & 0xF0FF) == 0x0003)
    {
        return $"BSRF R{(opcode >> 8) & 0xF}";
    }

    if ((opcode & 0xF0FF) == 0x0023)
    {
        return $"BRAF R{(opcode >> 8) & 0xF}";
    }

    if ((opcode & 0xF00F) is 0x0004 or 0x0005 or 0x0006 or 0x000C or 0x000D or 0x000E)
    {
        var destination = (opcode >> 8) & 0xF;
        var source = (opcode >> 4) & 0xF;
        return (opcode & 0xF00F) switch
        {
            0x0004 => $"MOV.B R{source},@(R0,R{destination})",
            0x0005 => $"MOV.W R{source},@(R0,R{destination})",
            0x0006 => $"MOV.L R{source},@(R0,R{destination})",
            0x000C => $"MOV.B @(R0,R{source}),R{destination}",
            0x000D => $"MOV.W @(R0,R{source}),R{destination}",
            _ => $"MOV.L @(R0,R{source}),R{destination}",
        };
    }

    if ((opcode & 0xF000) == 0x5000)
    {
        var destination = (opcode >> 8) & 0xF;
        var source = (opcode >> 4) & 0xF;
        var displacement = opcode & 0xF;
        return $"MOV.L @(0x{displacement:X1},R{source}),R{destination}";
    }

    if ((opcode & 0xF000) == 0xD000)
    {
        var register = (opcode >> 8) & 0xF;
        var displacement = opcode & 0xFF;
        var literalAddress = ((address + 4) & 0xFFFF_FFFCu) + (uint)(displacement * 4);
        return $"MOV.L @(0x{displacement:X2},PC),R{register} ; [0x{literalAddress:X8}]=0x{ReadLongOrFault(bus, literalAddress)}";
    }

    if ((opcode & 0xF000) == 0xE000)
    {
        var register = (opcode >> 8) & 0xF;
        return $"MOV #0x{opcode & 0xFF:X2},R{register}";
    }

    if ((opcode & 0xF000) == 0x9000)
    {
        var register = (opcode >> 8) & 0xF;
        var displacement = opcode & 0xFF;
        var literalAddress = ((address + 4) & 0xFFFF_FFFCu) + (uint)(displacement * 2);
        return $"MOV.W @(0x{displacement:X2},PC),R{register} ; [0x{literalAddress:X8}]=0x{ReadWordOrFault(bus, literalAddress)}";
    }

    if ((opcode & 0xFF00) is 0x8800 or 0x8900 or 0x8B00 or 0x8D00 or 0x8F00)
    {
        var displacement = (sbyte)(opcode & 0xFF) * 2;
        var target = (uint)(address + 4 + displacement);
        return (opcode & 0xFF00) switch
        {
            0x8800 => $"CMP/EQ #0x{opcode & 0xFF:X2},R0",
            0x8900 => $"BT 0x{target:X8}",
            0x8B00 => $"BF 0x{target:X8}",
            0x8D00 => $"BT/S 0x{target:X8}",
            _ => $"BF/S 0x{target:X8}",
        };
    }

    if ((opcode & 0xFF00) is 0x8000 or 0x8100 or 0x8400 or 0x8500)
    {
        var register = (opcode >> 4) & 0xF;
        var displacement = opcode & 0xF;
        return (opcode & 0xFF00) switch
        {
            0x8000 => $"MOV.B R0,@(0x{displacement:X1},R{register})",
            0x8100 => $"MOV.W R0,@(0x{displacement:X1},R{register})",
            0x8400 => $"MOV.B @(0x{displacement:X1},R{register}),R0",
            _ => $"MOV.W @(0x{displacement:X1},R{register}),R0",
        };
    }

    if ((opcode & 0xFF00) is 0xC800 or 0xC900 or 0xCA00 or 0xCB00)
    {
        return (opcode & 0xFF00) switch
        {
            0xC800 => $"TST #0x{opcode & 0xFF:X2},R0",
            0xC900 => $"AND #0x{opcode & 0xFF:X2},R0",
            0xCA00 => $"XOR #0x{opcode & 0xFF:X2},R0",
            _ => $"OR #0x{opcode & 0xFF:X2},R0",
        };
    }

    if ((opcode & 0xFF00) is 0xC000 or 0xC100 or 0xC200 or 0xC400 or 0xC500 or 0xC600
        or 0xCC00 or 0xCD00 or 0xCE00 or 0xCF00)
    {
        return DecodeGbrInstruction(opcode);
    }

    if ((opcode & 0xF000) == 0x6000)
    {
        var destination = (opcode >> 8) & 0xF;
        var source = (opcode >> 4) & 0xF;
        return (opcode & 0xF) switch
        {
            0x0 => $"MOV.B @R{source},R{destination}",
            0x1 => $"MOV.W @R{source},R{destination}",
            0x2 => $"MOV.L @R{source},R{destination}",
            0x3 => $"MOV R{source},R{destination}",
            0x4 => $"MOV.B @R{source}+,R{destination}",
            0x5 => $"MOV.W @R{source}+,R{destination}",
            0x6 => $"MOV.L @R{source}+,R{destination}",
            0x7 => $"NOT R{source},R{destination}",
            0x8 => $"SWAP.B R{source},R{destination}",
            0x9 => $"SWAP.W R{source},R{destination}",
            0xB => $"NEG R{source},R{destination}",
            0xC => $"EXTU.B R{source},R{destination}",
            0xD => $"EXTU.W R{source},R{destination}",
            0xE => $"EXTS.B R{source},R{destination}",
            0xF => $"EXTS.W R{source},R{destination}",
            _ => $"6xxx op R{source},R{destination}",
        };
    }

    if ((opcode & 0xF000) == 0x2000)
    {
        var destination = (opcode >> 8) & 0xF;
        var source = (opcode >> 4) & 0xF;
        return (opcode & 0xF) switch
        {
            0x0 => $"MOV.B R{source},@R{destination}",
            0x1 => $"MOV.W R{source},@R{destination}",
            0x2 => $"MOV.L R{source},@R{destination}",
            0x4 => $"MOV.B R{source},@-R{destination}",
            0x5 => $"MOV.W R{source},@-R{destination}",
            0x6 => $"MOV.L R{source},@-R{destination}",
            0x7 => $"DIV0S R{source},R{destination}",
            0x8 => $"TST R{source},R{destination}",
            0x9 => $"AND R{source},R{destination}",
            0xA => $"XOR R{source},R{destination}",
            0xB => $"OR R{source},R{destination}",
            0xD => $"XTRCT R{source},R{destination}",
            0xE => $"MULU.W R{source},R{destination}",
            0xF => $"MULS.W R{source},R{destination}",
            _ => $"2xxx op R{source},R{destination}",
        };
    }

    if ((opcode & 0xF000) == 0x3000)
    {
        var destination = (opcode >> 8) & 0xF;
        var source = (opcode >> 4) & 0xF;
        return (opcode & 0xF) switch
        {
            0x0 => $"CMP/EQ R{source},R{destination}",
            0x2 => $"CMP/HS R{source},R{destination}",
            0x3 => $"CMP/GE R{source},R{destination}",
            0x4 => $"DIV1 R{source},R{destination}",
            0x5 => $"DMULU.L R{source},R{destination}",
            0x6 => $"CMP/HI R{source},R{destination}",
            0x7 => $"CMP/GT R{source},R{destination}",
            0x8 => $"SUB R{source},R{destination}",
            0xA => $"SUBC R{source},R{destination}",
            0xC => $"ADD R{source},R{destination}",
            0xD => $"DMULS.L R{source},R{destination}",
            0xE => $"ADDC R{source},R{destination}",
            _ => $"3xxx op R{source},R{destination}",
        };
    }

    if ((opcode & 0xF000) == 0x7000)
    {
        var register = (opcode >> 8) & 0xF;
        return $"ADD #0x{opcode & 0xFF:X2},R{register}";
    }

    if ((opcode & 0xF00F) == 0x400B)
    {
        return $"JSR @R{(opcode >> 8) & 0xF}";
    }

    if ((opcode & 0xF00F) == 0x402B)
    {
        return $"JMP @R{(opcode >> 8) & 0xF}";
    }

    if ((opcode & 0xF0FF) is 0x0002 or 0x0012 or 0x0022 or 0x000A or 0x001A or 0x002A)
    {
        var register = (opcode >> 8) & 0xF;
        return (opcode & 0xF0FF) switch
        {
            0x0002 => $"STC SR,R{register}",
            0x0012 => $"STC GBR,R{register}",
            0x0022 => $"STC VBR,R{register}",
            0x000A => $"STS MACH,R{register}",
            0x001A => $"STS MACL,R{register}",
            _ => $"STS PR,R{register}",
        };
    }

    if (opcode == 0x0028)
    {
        return "CLRMAC";
    }

    if ((opcode & 0xF0FF) == 0x4022)
    {
        return $"STS.L PR,@-R{(opcode >> 8) & 0xF}";
    }

    if ((opcode & 0xF0FF) == 0x4003)
    {
        return $"STC.L SR,@-R{(opcode >> 8) & 0xF}";
    }

    if ((opcode & 0xF0FF) == 0x4013)
    {
        return $"STC.L GBR,@-R{(opcode >> 8) & 0xF}";
    }

    if ((opcode & 0xF0FF) == 0x4023)
    {
        return $"STC.L VBR,@-R{(opcode >> 8) & 0xF}";
    }

    if ((opcode & 0xF0FF) == 0x4012)
    {
        return $"STS.L MACL,@-R{(opcode >> 8) & 0xF}";
    }

    if ((opcode & 0xF0FF) == 0x4026)
    {
        return $"LDS.L @R{(opcode >> 8) & 0xF}+,PR";
    }

    if ((opcode & 0xF0FF) == 0x4007)
    {
        return $"LDC.L @R{(opcode >> 8) & 0xF}+,SR";
    }

    if ((opcode & 0xF0FF) == 0x4017)
    {
        return $"LDC.L @R{(opcode >> 8) & 0xF}+,GBR";
    }

    if ((opcode & 0xF0FF) == 0x4027)
    {
        return $"LDC.L @R{(opcode >> 8) & 0xF}+,VBR";
    }

    if ((opcode & 0xF0FF) == 0x4016)
    {
        return $"LDS.L @R{(opcode >> 8) & 0xF}+,MACL";
    }

    if ((opcode & 0xF0FF) == 0x400E)
    {
        return $"LDC R{(opcode >> 8) & 0xF},SR";
    }

    if ((opcode & 0xF0FF) is 0x4000 or 0x4001 or 0x4010 or 0x4011 or 0x4015 or 0x4008 or 0x4009 or 0x4018 or 0x4019 or 0x4020 or 0x4021 or 0x4024 or 0x4025 or 0x4028 or 0x4029)
    {
        var register = (opcode >> 8) & 0xF;
        return (opcode & 0xF0FF) switch
        {
            0x4000 => $"SHLL R{register}",
            0x4001 => $"SHLR R{register}",
            0x4010 => $"DT R{register}",
            0x4011 => $"CMP/PZ R{register}",
            0x4015 => $"CMP/PL R{register}",
            0x4008 => $"SHLL2 R{register}",
            0x4009 => $"SHLR2 R{register}",
            0x4018 => $"SHLL8 R{register}",
            0x4019 => $"SHLR8 R{register}",
            0x4020 => $"SHAL R{register}",
            0x4021 => $"SHAR R{register}",
            0x4024 => $"ROTCL R{register}",
            0x4025 => $"ROTCR R{register}",
            0x4028 => $"SHLL16 R{register}",
            _ => $"SHLR16 R{register}",
        };
    }

    return "unknown";
}

static string DecodeGbrInstruction(ushort opcode)
{
    var displacement = opcode & 0xFF;
    return (opcode & 0xFF00) switch
    {
        0xC000 => $"MOV.B R0,@(0x{displacement:X2},GBR)",
        0xC100 => $"MOV.W R0,@(0x{displacement:X2},GBR)",
        0xC200 => $"MOV.L R0,@(0x{displacement:X2},GBR)",
        0xC400 => $"MOV.B @(0x{displacement:X2},GBR),R0",
        0xC500 => $"MOV.W @(0x{displacement:X2},GBR),R0",
        0xC600 => $"MOV.L @(0x{displacement:X2},GBR),R0",
        0xCC00 => $"TST.B #0x{displacement:X2},@(R0,GBR)",
        0xCD00 => $"AND.B #0x{displacement:X2},@(R0,GBR)",
        0xCE00 => $"XOR.B #0x{displacement:X2},@(R0,GBR)",
        _ => $"OR.B #0x{displacement:X2},@(R0,GBR)",
    };
}

static uint BranchTarget(uint address, int displacement12)
{
    var signed = displacement12 << 20;
    signed >>= 19;
    return (uint)(address + 4 + signed);
}

static string ReadWordOrFault(ISaturnBus bus, uint address)
{
    try
    {
        return bus.ReadWord(address).ToString("X4");
    }
    catch (BusFaultException)
    {
        return "<fault>";
    }
}

static string ReadLongOrFault(ISaturnBus bus, uint address)
{
    try
    {
        return bus.ReadLong(address).ToString("X8");
    }
    catch (BusFaultException)
    {
        return "<fault>";
    }
}

static void PrintInternalActivity(string label, Sh2InternalRegisterBus bus)
{
    if (bus.InternalReadCount == 0 && bus.InternalWriteCount == 0)
    {
        return;
    }

    Console.WriteLine($"{label}: reads={bus.InternalReadCount:N0} writes={bus.InternalWriteCount:N0}");
}

static bool TryStep(Sh2Cpu cpu, ITraceEventSink trace, List<string> busFaults)
{
    try
    {
        cpu.StepInstruction();
        return true;
    }
    catch (BusFaultException exception)
    {
        var pc = cpu.CurrentInstructionProgramCounter;
        var message = pc is null
            ? $"{cpu.Name} fault at 0x{exception.Address:X8}"
            : $"{cpu.Name} fault at 0x{exception.Address:X8} while executing 0x{pc.Value:X8}";
        busFaults.Add(message);
        trace.Write(new TraceEvent("Bus", 0, message));
        return false;
    }
}

static void PrintBusFaults(IReadOnlyList<string> busFaults)
{
    if (busFaults.Count == 0)
    {
        return;
    }

    Console.WriteLine("Bus faults:");
    foreach (var busFault in busFaults)
    {
        Console.WriteLine($"  {busFault}");
    }
}

static void PrintUnimplemented(Sh2Cpu cpu)
{
    if (cpu.FirstUnimplementedOpcode is null)
    {
        return;
    }

    Console.WriteLine(
        $"{cpu.Name} first unimplemented: 0x{cpu.FirstUnimplementedOpcode:X4} at 0x{cpu.FirstUnimplementedProgramCounter:X8}");
    Console.WriteLine(
        $"{cpu.Name} last unimplemented: 0x{cpu.LastUnimplementedOpcode:X4} at 0x{cpu.LastUnimplementedProgramCounter:X8}");
    Console.WriteLine($"{cpu.Name} unimplemented count: {cpu.UnimplementedInstructionCount:N0}");
}

static void PrintMemoryActivity(string label, IMainMemory memory)
{
    if (memory is not IWriteTrackedMemory tracked)
    {
        return;
    }

    if (tracked.WriteCount == 0)
    {
        Console.WriteLine($"{label}: no writes");
        return;
    }

    Console.WriteLine(
        $"{label}: writes={tracked.WriteCount:N0} first=0x{tracked.FirstWriteOffset!.Value:X6} last=0x{tracked.LastWriteOffset!.Value:X6}");
}

static void PrintTouchedStubs(SaturnSystemMap systemMap)
{
    var touched = systemMap.Stubs
        .Where(static stub => stub.ReadCount != 0 || stub.WriteCount != 0)
        .ToArray();

    if (touched.Length == 0)
    {
        return;
    }

    Console.WriteLine("Touched stubs:");
    foreach (var stub in touched)
    {
        Console.WriteLine($"  {stub.Name}: reads={stub.ReadCount:N0} writes={stub.WriteCount:N0}");
        PrintStubRange("read", stub.FirstReadOffset, stub.LastReadOffset);
        PrintStubRange("write", stub.FirstWriteOffset, stub.LastWriteOffset);
        if (stub is CdBlockRegisterBusDevice cdRegisters)
        {
            Console.WriteLine(
                $"    mounted disc: {(cdRegisters.HasDisc ? $"{cdRegisters.DiscName} ({cdRegisters.DiscSectorCount:N0} sectors)" : "<none>")}");
            Console.WriteLine($"    auth type: 0x{cdRegisters.AuthenticationType:X2}");
            Console.WriteLine($"    auth startup completed: {cdRegisters.AuthStartupCompleted}");
            Console.WriteLine(
                $"    last command CR: 0x{cdRegisters.LastCommandCr1:X4} 0x{cdRegisters.LastCommandCr2:X4} 0x{cdRegisters.LastCommandCr3:X4} 0x{cdRegisters.LastCommandCr4:X4}");
            Console.WriteLine(
                $"    command counts: {string.Join(", ", cdRegisters.CommandCounts.Select(static item => $"0x{item.Command:X2}:{item.Count:N0}"))}");
            Console.WriteLine($"    recent commands: {string.Join(", ", cdRegisters.RecentCommands.Select(static command => $"0x{command:X2}"))}");
            Console.WriteLine(
                $"    response CR: 0x{cdRegisters.ResponseCr1:X4} 0x{cdRegisters.ResponseCr2:X4} 0x{cdRegisters.ResponseCr3:X4} 0x{cdRegisters.ResponseCr4:X4}");
        }
        else if (stub is SmpcRegisterBusDevice smpcRegisters)
        {
            Console.WriteLine($"    last command: 0x{smpcRegisters.LastCommand:X2}");
            Console.WriteLine($"    recent commands: {string.Join(", ", smpcRegisters.RecentCommands.Select(static command => $"0x{command:X2}"))}");
            Console.WriteLine($"    SR: 0x{smpcRegisters.StatusRegisterValue:X2}");
            Console.WriteLine($"    IREG: {string.Join(", ", smpcRegisters.InputRegisters.Select(static value => $"0x{value:X2}"))}");
            Console.WriteLine($"    OREG: {string.Join(", ", smpcRegisters.OutputRegisters.Take(12).Select(static value => $"0x{value:X2}"))}");
            Console.WriteLine($"    pending interrupts: {smpcRegisters.PendingInterrupts}");
            Console.WriteLine($"    slave SH-2 enabled: {smpcRegisters.SlaveSh2Enabled}");
        }
        else if (stub is ScuRegisterBusDevice scuRegisters)
        {
            Console.WriteLine($"    interrupt mask: 0x{scuRegisters.InterruptMask:X8}");
            Console.WriteLine($"    interrupt status: 0x{scuRegisters.InterruptStatus:X8}");
            Console.WriteLine($"    last interrupt status write: 0x{scuRegisters.LastInterruptStatusWrite:X8}");
        }

        PrintHotStubOffsets("hot reads", stub.GetHotReadOffsets(8));
        PrintHotStubOffsets("hot writes", stub.GetHotWriteOffsets(8));
    }
}

static void PrintStubRange(string label, uint? firstOffset, uint? lastOffset)
{
    if (firstOffset is null || lastOffset is null)
    {
        return;
    }

    Console.WriteLine($"    {label}: first=0x{firstOffset.Value:X6} last=0x{lastOffset.Value:X6}");
}

static void PrintHotStubOffsets(string label, IReadOnlyList<(uint Offset, long Count)> offsets)
{
    if (offsets.Count == 0)
    {
        return;
    }

    Console.WriteLine($"    {label}:");
    foreach (var (offset, count) in offsets)
    {
        Console.WriteLine($"      0x{offset:X6}: {count:N0}");
    }
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1];
        }
    }

    return null;
}

static int GetIntOption(string[] args, string name, int defaultValue)
{
    var value = GetOption(args, name);
    return value is not null && int.TryParse(value, out var parsed) ? parsed : defaultValue;
}

static CdBlockDriveStatus? GetCdStatusOption(string[] args)
{
    var value = GetOption(args, "--cd-status");
    if (value is null)
    {
        return null;
    }

    return value.ToLowerInvariant() switch
    {
        "busy" => CdBlockDriveStatus.Busy,
        "pause" => CdBlockDriveStatus.Pause,
        "standby" => CdBlockDriveStatus.Standby,
        "play" => CdBlockDriveStatus.Play,
        "wait" => CdBlockDriveStatus.Wait,
        _ => throw new ArgumentException($"Unknown --cd-status '{value}'. Expected busy, pause, standby, play, or wait."),
    };
}

static SaturnInputState GetPadOption(string[] args)
{
    var value = GetOption(args, "--pad");
    if (value is null)
    {
        return SaturnInputState.None;
    }

    var state = SaturnInputState.None;
    foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!Enum.TryParse<SaturnInputState>(token, ignoreCase: true, out var button) || button == SaturnInputState.None)
        {
            throw new ArgumentException(
                $"Unknown --pad button '{token}'. Expected up, down, left, right, start, a, b, c, x, y, z, l, or r.");
        }

        state |= button;
    }

    return state;
}

static IReadOnlyList<byte>? GetPadRawOption(string[] args)
{
    var value = GetOption(args, "--pad-raw");
    if (value is null)
    {
        return null;
    }

    var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
    if (hex.Length != 8)
    {
        throw new ArgumentException("--pad-raw expects exactly four bytes as eight hex digits, for example F102FFFF.");
    }

    var bytes = new byte[4];
    for (var i = 0; i < bytes.Length; i++)
    {
        bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
    }

    return bytes;
}

static IDiscImage OpenDiscImage(string path) =>
    Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase)
        ? new CueDiscImage(path)
        : new RawDiscImage(path);

static bool Has(string[] args, string name) => args.Any(candidate => candidate == name);

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine();
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("SystemRegisIII CLI");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  SystemRegisIII.Cli run --bios <path> [--disc <path>] [--cd-status busy|pause|standby|play|wait] [--instructions N] [--vblank-interval N] [--pad buttons] [--pad-raw F102FFFF] [--trace] [--simulate-slave-ready] [--dual-sh2]");
}

sealed class ScuInterruptProbe
{
    private readonly InterruptProbeLine _vblankIn = new("VBlank-IN", 15, 0x40);
    private readonly InterruptProbeLine _vblankOut = new("VBlank-OUT", 14, 0x41);
    private readonly InterruptProbeLine _smpc = new("SMPC", 8, 0x47);

    public void RecordVBlankIn(bool accepted, uint pc) => _vblankIn.Record(accepted, pc);

    public void RecordVBlankOut(bool accepted, uint pc) => _vblankOut.Record(accepted, pc);

    public void RecordSmpc(bool accepted, uint pc) => _smpc.Record(accepted, pc);

    public void Print()
    {
        Console.WriteLine("  delivery:");
        _vblankIn.Print();
        _vblankOut.Print();
        _smpc.Print();
    }

    private sealed class InterruptProbeLine(string label, int level, byte vector)
    {
        private readonly string _label = label;
        private readonly int _level = level;
        private readonly byte _vector = vector;
        private long _attempts;
        private long _accepted;
        private uint? _lastAcceptedPc;

        public void Record(bool accepted, uint pc)
        {
            _attempts++;
            if (!accepted)
            {
                return;
            }

            _accepted++;
            _lastAcceptedPc = pc;
        }

        public void Print()
        {
            var lastAccepted = _lastAcceptedPc is { } pc ? $"0x{pc:X8}" : "<none>";
            Console.WriteLine(
                $"    {_label} level={_level} vector=0x{_vector:X2} attempts={_attempts:N0} accepted={_accepted:N0} last-accepted-pc={lastAccepted}");
        }
    }
}

sealed class PcWindowProbe(
    string label,
    uint start,
    uint end,
    int capacity,
    uint? focusedProcedureRegister = null,
    uint? focusedR6Start = null,
    uint? focusedR6End = null)
{
    private readonly Queue<PcWindowSample> _first = new(capacity);
    private readonly Queue<PcWindowSample> _last = new(capacity);
    private readonly Queue<PcWindowSample> _focusedFirst = new(capacity);
    private readonly Queue<PcWindowSample> _focusedLast = new(capacity);
    private readonly Dictionary<uint, long> _procedureRegisterHits = [];
    private long _hits;
    private long _focusedHits;

    public void Record(int instructionIndex, Sh2Cpu cpu, ISaturnBus bus, Func<ISaturnBus, uint, string> formatInstruction)
    {
        var pc = cpu.Registers.ProgramCounter;
        if (pc < start || pc > end)
        {
            return;
        }

        var r6 = cpu.Registers.General[6];
        if (focusedR6Start is { } r6Start && r6 < r6Start)
        {
            return;
        }

        if (focusedR6End is { } r6End && r6 > r6End)
        {
            return;
        }

        var sample = new PcWindowSample(
            instructionIndex,
            pc,
            cpu.Registers.ProcedureRegister,
            cpu.Registers.StatusRegister,
            cpu.Registers.General[0],
            cpu.Registers.General[1],
            cpu.Registers.General[2],
            cpu.Registers.General[3],
            cpu.Registers.General[4],
            cpu.Registers.General[5],
            r6,
            cpu.Registers.General[7],
            cpu.Registers.General[9],
            cpu.Registers.General[10],
            cpu.Registers.General[12],
            cpu.Registers.General[13],
            cpu.Registers.MacHigh,
            cpu.Registers.MacLow,
            formatInstruction(bus, pc));

        _hits++;
        _procedureRegisterHits.TryGetValue(sample.Pr, out var procedureRegisterHits);
        _procedureRegisterHits[sample.Pr] = procedureRegisterHits + 1;
        if (_first.Count < capacity)
        {
            _first.Enqueue(sample);
        }

        _last.Enqueue(sample);
        if (_last.Count > capacity)
        {
            _last.Dequeue();
        }

        if (focusedProcedureRegister != sample.Pr)
        {
            return;
        }

        _focusedHits++;
        if (_focusedFirst.Count < capacity)
        {
            _focusedFirst.Enqueue(sample);
        }

        _focusedLast.Enqueue(sample);
        if (_focusedLast.Count > capacity)
        {
            _focusedLast.Dequeue();
        }
    }

    public void Print()
    {
        if (_hits == 0)
        {
            return;
        }

        Console.WriteLine($"{label}: hits={_hits:N0} window=0x{start:X8}..0x{end:X8}");
        PrintProcedureRegisterHits();
        PrintSamples("first", _first);
        if (_hits > _first.Count)
        {
            PrintSamples("last", _last);
        }

        if (focusedProcedureRegister is { } pr && _focusedHits > 0)
        {
            Console.WriteLine($"  focused PR=0x{pr:X8} hits={_focusedHits:N0}:");
            PrintSamples("focused first", _focusedFirst);
            if (_focusedHits > _focusedFirst.Count)
            {
                PrintSamples("focused last", _focusedLast);
            }
        }
    }

    private void PrintProcedureRegisterHits()
    {
        Console.WriteLine("  hot PRs:");
        foreach (var (procedureRegister, count) in _procedureRegisterHits
                     .OrderByDescending(static pair => pair.Value)
                     .ThenBy(static pair => pair.Key)
                     .Take(12))
        {
            Console.WriteLine($"    0x{procedureRegister:X8}: {count:N0}");
        }
    }

    private static void PrintSamples(string name, IEnumerable<PcWindowSample> samples)
    {
        Console.WriteLine($"  {name} samples:");
        foreach (var sample in samples)
        {
            Console.WriteLine(
                $"    i={sample.InstructionIndex:N0} pc=0x{sample.Pc:X8} pr=0x{sample.Pr:X8} sr=0x{sample.Sr:X8} r0=0x{sample.R0:X8} r1=0x{sample.R1:X8} r2=0x{sample.R2:X8} r3=0x{sample.R3:X8} r4=0x{sample.R4:X8} r5=0x{sample.R5:X8} r6=0x{sample.R6:X8} r7=0x{sample.R7:X8} r9=0x{sample.R9:X8} r10=0x{sample.R10:X8} r12=0x{sample.R12:X8} r13=0x{sample.R13:X8} mach=0x{sample.Mach:X8} macl=0x{sample.Macl:X8} {sample.Instruction}");
        }
    }

    private readonly record struct PcWindowSample(
        int InstructionIndex,
        uint Pc,
        uint Pr,
        uint Sr,
        uint R0,
        uint R1,
        uint R2,
        uint R3,
        uint R4,
        uint R5,
        uint R6,
        uint R7,
        uint R9,
        uint R10,
        uint R12,
        uint R13,
        uint Mach,
        uint Macl,
        string Instruction);
}
