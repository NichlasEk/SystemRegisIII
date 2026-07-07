using SystemRegisIII.Cli;
using SystemRegisIII.Core.Core;
using SystemRegisIII.Core.Core.Bus;
using SystemRegisIII.Core.Core.Cpu.Sh2;
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

    var bios = BiosImageLoader.Load(biosPath);
    var trace = new RingTraceEventSink(capacity: Math.Max(512, instructionCount * 8));
    var systemMap = SaturnSystemMap.CreateBringup(bios);
    var addressMap = systemMap.Bus;
    ISaturnBus bus = traceEnabled ? new TracingBus(addressMap, trace) : addressMap;

    var master = new Sh2Cpu("Master SH-2", bus, resetVectorAddress: 0x0000_0000, trace);
    master.Reset();

    for (var i = 0; i < instructionCount; i++)
    {
        try
        {
            master.StepInstruction();
        }
        catch (BusFaultException exception)
        {
            trace.Write(new TraceEvent("Bus", 0, $"fault at 0x{exception.Address:X8}"));
            break;
        }
    }

    Console.WriteLine($"BIOS: {bios.Name}");
    Console.WriteLine($"BIOS bytes: {bios.Bytes.Length:N0}");
    Console.WriteLine($"Master SH-2 PC: 0x{master.Registers.ProgramCounter:X8}");
    Console.WriteLine($"Master SH-2 SR: 0x{master.Registers.StatusRegister:X8}");
    if (master.FirstUnimplementedOpcode is not null)
    {
        Console.WriteLine(
            $"First unimplemented: 0x{master.FirstUnimplementedOpcode:X4} at 0x{master.FirstUnimplementedProgramCounter:X8}");
        Console.WriteLine(
            $"Last unimplemented: 0x{master.LastUnimplementedOpcode:X4} at 0x{master.LastUnimplementedProgramCounter:X8}");
        Console.WriteLine($"Unimplemented count: {master.UnimplementedInstructionCount:N0}");
    }

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
    Console.WriteLine("  SystemRegisIII.Cli run --bios <path> [--instructions N] [--trace]");
}
