using SystemRegisIII.Cli;
using SystemRegisIII.Core.Core;
using SystemRegisIII.Core.Core.Bus;
using SystemRegisIII.Core.Core.Cpu.Sh2;
using SystemRegisIII.Core.Core.Memory;
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
    var bus = new MappedBus();
    var biosRom = new RomDevice("BIOS ROM", bios.Bytes);
    var workRamLow = new ByteArrayMemory("Work RAM Low", 1024 * 1024);
    var workRamHigh = new ByteArrayMemory("Work RAM High", 1024 * 1024);
    var sh2Internal = new StubBusDevice("SH-2 Internal Registers");

    bus.Map(0x0000_0000, (uint)(biosRom.SizeBytes - 1), biosRom);
    bus.Map(0x0020_0000, 0x002F_FFFF, workRamLow);
    bus.Map(0x0600_0000, 0x060F_FFFF, workRamHigh);
    bus.Map(0x6000_0000, 0x600F_FFFF, workRamHigh);
    bus.Map(0xFFFF_FE00, 0xFFFF_FFFF, sh2Internal);

    if (traceEnabled)
    {
        bus.Accessed += (_, eventArgs) =>
        {
            var access = eventArgs.Access;
            var op = access.IsWrite ? "write" : "read";
            trace.Write(new TraceEvent(
                "Bus",
                0,
                $"{op} {access.SizeBytes} byte(s) 0x{access.Address:X8}=0x{access.Value:X8} {access.DeviceName}"));
        };
    }

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
    Console.WriteLine($"Mapped regions: {bus.Regions.Count}");

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
