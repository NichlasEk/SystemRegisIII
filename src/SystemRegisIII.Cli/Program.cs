using SystemRegisIII.Cli;
using SystemRegisIII.Core.Core;
using SystemRegisIII.Core.Core.Bus;
using SystemRegisIII.Core.Core.CdBlock;
using SystemRegisIII.Core.Core.Cpu.Sh2;
using SystemRegisIII.Core.Core.Memory;
using SystemRegisIII.Core.Core.Scu;
using SystemRegisIII.Core.Core.Smpc;
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

    var instructionCount = GetIntOption(args, "--instructions", defaultValue: 64);
    var traceEnabled = Has(args, "--trace");
    var simulateSlaveReady = Has(args, "--simulate-slave-ready");
    var dualSh2 = Has(args, "--dual-sh2");

    var bios = BiosImageLoader.Load(biosPath);
    var trace = new RingTraceEventSink(capacity: Math.Clamp(instructionCount * 8, 512, 8_192));
    var systemMap = SaturnSystemMap.CreateBringup(
        bios,
        new SaturnBringupOptions { SimulateSlaveReady = simulateSlaveReady });
    var addressMap = systemMap.Bus;
    var masterInternalBus = new Sh2InternalRegisterBus(addressMap, Sh2CpuRole.Master);
    var slaveInternalBus = dualSh2 ? new Sh2InternalRegisterBus(addressMap, Sh2CpuRole.Slave) : null;
    Sh2Cpu? master = null;
    Sh2Cpu? slave = null;
    var masterFlagWatch = new WatchedBus(
        masterInternalBus,
        0x0602_0230,
        0x0602_024F,
        () => GetWatchContext(master));
    var masterCallbackWatch = new WatchedBus(
        masterFlagWatch,
        0x0602_0720,
        0x0602_075F,
        () => GetWatchContext(master));
    WatchedBus? slaveFlagWatch = slaveInternalBus is null
        ? null
        : new WatchedBus(
            slaveInternalBus,
            0x0602_0230,
            0x0602_024F,
            () => GetWatchContext(slave));
    ISaturnBus masterBus = traceEnabled ? new TracingBus(masterCallbackWatch, trace) : masterCallbackWatch;
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

    for (var i = 0; i < instructionCount; i++)
    {
        while (smpc.TryConsumeInterrupt())
        {
            scu.RaiseSmpc();
        }

        if (i > 0 && i % 1_000_000 == 0)
        {
            scu.RaiseVBlankIn();
        }
        else if (i > 0 && i % 1_000_000 == 500_000)
        {
            scu.RaiseVBlankOut();
        }

        if (scu.HasPendingVBlankIn)
        {
            var accepted = master.RequestInterrupt(15, 0x40);
            interruptProbe.RecordVBlankIn(accepted, master.Registers.ProgramCounter);
            if (accepted)
            {
                scu.AcknowledgeVBlankIn();
            }
        }
        else if (scu.HasPendingVBlankOut)
        {
            var accepted = master.RequestInterrupt(14, 0x41);
            interruptProbe.RecordVBlankOut(accepted, master.Registers.ProgramCounter);
            if (accepted)
            {
                scu.AcknowledgeVBlankOut();
            }
        }
        else if (scu.HasPendingSmpc)
        {
            interruptProbe.RecordSmpc(master.RequestInterrupt(8, 0x47), master.Registers.ProgramCounter);
        }

        var masterPc = master.Registers.ProgramCounter;
        RecordPc(masterPcHits, masterPc);
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
    PrintWatchWindow("Master flag watch", masterFlagWatch);
    PrintWatchWindow("Master callback-state watch", masterCallbackWatch);
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
            cpu.Registers.General[0]);

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

static string FormatWatchContext(WatchedAccessContext? context)
{
    if (context is not { } value)
    {
        return "pc=<unknown>";
    }

    var pc = value.ProgramCounter is { } programCounter ? $"0x{programCounter:X8}" : "<unknown>";
    return $"pc={pc} pr=0x{value.ProcedureRegister:X8} gbr=0x{value.GlobalBaseRegister:X8} r0=0x{value.R0:X8}";
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
            0x8 => $"TST R{source},R{destination}",
            0x9 => $"AND R{source},R{destination}",
            0xA => $"XOR R{source},R{destination}",
            0xB => $"OR R{source},R{destination}",
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
            0xC => $"ADD R{source},R{destination}",
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

    if ((opcode & 0xF0FF) == 0x400E)
    {
        return $"LDC R{(opcode >> 8) & 0xF},SR";
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
        var message = $"{cpu.Name} fault at 0x{exception.Address:X8}";
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
                $"    last command CR: 0x{cdRegisters.LastCommandCr1:X4} 0x{cdRegisters.LastCommandCr2:X4} 0x{cdRegisters.LastCommandCr3:X4} 0x{cdRegisters.LastCommandCr4:X4}");
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
    Console.WriteLine("  SystemRegisIII.Cli run --bios <path> [--instructions N] [--trace] [--simulate-slave-ready] [--dual-sh2]");
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
