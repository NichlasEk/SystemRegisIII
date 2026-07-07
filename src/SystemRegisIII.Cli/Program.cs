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
    ISaturnBus masterBus = traceEnabled ? new TracingBus(masterInternalBus, trace) : masterInternalBus;
    ISaturnBus? slaveBus = slaveInternalBus is null
        ? null
        : traceEnabled ? new TracingBus(slaveInternalBus, trace) : slaveInternalBus;

    var master = new Sh2Cpu("Master SH-2", masterBus, resetVectorAddress: 0x0000_0000, trace);
    var slave = slaveBus is not null ? new Sh2Cpu("Slave SH-2", slaveBus, resetVectorAddress: 0x0000_0008, trace) : null;
    var smpc = systemMap.Stubs.OfType<SmpcRegisterBusDevice>().Single();
    var scu = systemMap.Stubs.OfType<ScuRegisterBusDevice>().Single();
    master.Reset();
    slave?.Reset();
    var masterPcHits = new Dictionary<uint, long>();
    var slavePcHits = dualSh2 ? new Dictionary<uint, long>() : null;
    var busFaults = new List<string>();
    var slaveWasEnabled = smpc.SlaveSh2Enabled;

    for (var i = 0; i < instructionCount; i++)
    {
        if (i > 0 && i % 1_000_000 == 0)
        {
            scu.RaiseVBlankIn();
        }

        if (scu.HasPendingVBlankIn)
        {
            _ = master.RequestInterrupt(15, 0x40);
        }

        RecordPc(masterPcHits, master.Registers.ProgramCounter);
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
    if (slave is not null)
    {
        PrintHotProgramCounters(slave.Name, slavePcHits!);
    }

    PrintMasterGbrLoopProbe(master, addressMap);
    PrintScuInterruptState(scu);
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
        .Take(4)
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
        PrintWordWindow(bus, 0x0602_8300, 16, "  code window");
    }
    catch (BusFaultException exception)
    {
        Console.WriteLine($"  [GBR+0x240]=[0x{watchedAddress:X8}] faulted at 0x{exception.Address:X8}");
    }
}

static void PrintScuInterruptState(ScuRegisterBusDevice scu)
{
    if (scu.ReadCount == 0 && scu.WriteCount == 0 && scu.InterruptStatus == 0)
    {
        return;
    }

    Console.WriteLine("SCU interrupt state:");
    Console.WriteLine($"  mask=0x{scu.InterruptMask:X8} status=0x{scu.InterruptStatus:X8} vblank-in-pending={scu.HasPendingVBlankIn}");
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
        }
        else if (stub is SmpcRegisterBusDevice smpcRegisters)
        {
            Console.WriteLine($"    last command: 0x{smpcRegisters.LastCommand:X2}");
            Console.WriteLine($"    recent commands: {string.Join(", ", smpcRegisters.RecentCommands.Select(static command => $"0x{command:X2}"))}");
            Console.WriteLine($"    slave SH-2 enabled: {smpcRegisters.SlaveSh2Enabled}");
        }
        else if (stub is ScuRegisterBusDevice scuRegisters)
        {
            Console.WriteLine($"    interrupt mask: 0x{scuRegisters.InterruptMask:X8}");
            Console.WriteLine($"    interrupt status: 0x{scuRegisters.InterruptStatus:X8}");
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
