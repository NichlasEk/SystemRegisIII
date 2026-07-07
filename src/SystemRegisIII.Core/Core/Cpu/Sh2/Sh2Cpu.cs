using SystemRegisIII.Core.Core.Bus;
using SystemRegisIII.Core.Tools.TraceViewer;

namespace SystemRegisIII.Core.Core.Cpu.Sh2;

public sealed class Sh2Cpu : ISh2Cpu
{
    private readonly ISaturnBus _bus;
    private readonly ITraceEventSink? _trace;
    private long _cycles;

    public Sh2Cpu(string name, ISaturnBus bus, uint resetVectorAddress, ITraceEventSink? trace = null)
    {
        Name = name;
        _bus = bus;
        ResetVectorAddress = resetVectorAddress;
        _trace = trace;
    }

    public string Name { get; }
    public uint ResetVectorAddress { get; }
    public Sh2Registers Registers { get; } = new();
    public uint? FirstUnimplementedProgramCounter { get; private set; }
    public ushort? FirstUnimplementedOpcode { get; private set; }
    public uint? LastUnimplementedProgramCounter { get; private set; }
    public ushort? LastUnimplementedOpcode { get; private set; }
    public long UnimplementedInstructionCount { get; private set; }

    public void Reset()
    {
        Registers.ProgramCounter = _bus.ReadLong(ResetVectorAddress);
        Registers.StatusRegister = _bus.ReadLong(ResetVectorAddress + 4);
        Registers.ProcedureRegister = 0;
        Registers.GlobalBaseRegister = 0;
        Registers.VectorBaseRegister = 0;
        Array.Clear(Registers.General);
        FirstUnimplementedProgramCounter = null;
        FirstUnimplementedOpcode = null;
        LastUnimplementedProgramCounter = null;
        LastUnimplementedOpcode = null;
        UnimplementedInstructionCount = 0;
        _cycles = 0;
        Trace($"reset pc=0x{Registers.ProgramCounter:X8} sr=0x{Registers.StatusRegister:X8}");
    }

    public void Step(SaturnCycleBudget budget)
    {
        var instructions = Math.Max(1, Math.Min(256, budget.MasterCycles / 4));

        for (var i = 0; i < instructions; i++)
        {
            StepInstruction();
        }
    }

    public void StepInstruction()
    {
        var pc = Registers.ProgramCounter;
        var opcode = _bus.ReadWord(pc);
        Registers.ProgramCounter += 2;
        _cycles += 1;

        if ((opcode & 0xF000) == 0xB000)
        {
            var target = BranchTarget(pc, opcode & 0x0FFF);
            Registers.ProcedureRegister = Registers.ProgramCounter + 2;
            ExecuteDelaySlot();
            Registers.ProgramCounter = target;
            Trace($"0x{pc:X8}: BSR 0x{target:X8}");
            return;
        }

        if ((opcode & 0xF000) == 0xA000)
        {
            var target = BranchTarget(pc, opcode & 0x0FFF);
            ExecuteDelaySlot();
            Registers.ProgramCounter = target;
            Trace($"0x{pc:X8}: BRA 0x{target:X8}");
            return;
        }

        if ((opcode & 0xF000) == 0xD000)
        {
            var register = (opcode >> 8) & 0xF;
            var displacement = opcode & 0xFF;
            var address = ((Registers.ProgramCounter + 2) & 0xFFFF_FFFCu) + (uint)(displacement * 4);
            Registers.General[register] = _bus.ReadLong(address);
            Trace($"0x{pc:X8}: MOV.L @(0x{displacement:X2},PC),R{register} <- 0x{Registers.General[register]:X8}");
            return;
        }

        if ((opcode & 0xF000) == 0xE000)
        {
            var register = (opcode >> 8) & 0xF;
            var immediate = (uint)(int)(sbyte)(opcode & 0xFF);
            Registers.General[register] = immediate;
            Trace($"0x{pc:X8}: MOV #0x{opcode & 0xFF:X2},R{register}");
            return;
        }

        if ((opcode & 0xFF00) is 0xC000 or 0xC100 or 0xC200 or 0xC400 or 0xC500 or 0xC600)
        {
            ExecuteGbrMove(pc, opcode);
            return;
        }

        if ((opcode & 0xFF00) == 0x8900)
        {
            var displacement = SignExtend8(opcode & 0xFF) * 2;
            var target = (uint)(Registers.ProgramCounter + 2 + displacement);
            Trace($"0x{pc:X8}: BT 0x{target:X8} T={Registers.T}");
            if (Registers.T)
            {
                Registers.ProgramCounter = target;
            }

            return;
        }

        if ((opcode & 0xFF00) == 0x8B00)
        {
            var displacement = SignExtend8(opcode & 0xFF) * 2;
            var target = (uint)(Registers.ProgramCounter + 2 + displacement);
            Trace($"0x{pc:X8}: BF 0x{target:X8} T={Registers.T}");
            if (!Registers.T)
            {
                Registers.ProgramCounter = target;
            }

            return;
        }

        if ((opcode & 0xFF00) == 0x8F00)
        {
            var displacement = SignExtend8(opcode & 0xFF) * 2;
            var target = (uint)(Registers.ProgramCounter + 2 + displacement);
            Trace($"0x{pc:X8}: BF/S 0x{target:X8} T={Registers.T}");
            ExecuteDelaySlot();
            if (!Registers.T)
            {
                Registers.ProgramCounter = target;
            }

            return;
        }

        if ((opcode & 0xF000) == 0x7000)
        {
            var register = (opcode >> 8) & 0xF;
            Registers.General[register] += (uint)SignExtend8(opcode & 0xFF);
            Trace($"0x{pc:X8}: ADD #0x{opcode & 0xFF:X2},R{register}");
            return;
        }

        switch (opcode & 0xF00F)
        {
            case 0x3000:
            {
                var destination = (opcode >> 8) & 0xF;
                var source = (opcode >> 4) & 0xF;
                Registers.T = Registers.General[destination] == Registers.General[source];
                Trace($"0x{pc:X8}: CMP/EQ R{source},R{destination} T={Registers.T}");
                return;
            }
            case 0x2000:
            {
                var destination = (opcode >> 8) & 0xF;
                var source = (opcode >> 4) & 0xF;
                _bus.WriteByte(Registers.General[destination], (byte)Registers.General[source]);
                Trace($"0x{pc:X8}: MOV.B R{source},@R{destination}");
                return;
            }
            case 0x2001:
            {
                var destination = (opcode >> 8) & 0xF;
                var source = (opcode >> 4) & 0xF;
                _bus.WriteWord(Registers.General[destination], (ushort)Registers.General[source]);
                Trace($"0x{pc:X8}: MOV.W R{source},@R{destination}");
                return;
            }
            case 0x6002:
            {
                var destination = (opcode >> 8) & 0xF;
                var source = (opcode >> 4) & 0xF;
                Registers.General[destination] = _bus.ReadLong(Registers.General[source]);
                Trace($"0x{pc:X8}: MOV.L @R{source},R{destination}");
                return;
            }
            case 0x6006:
            {
                var destination = (opcode >> 8) & 0xF;
                var source = (opcode >> 4) & 0xF;
                Registers.General[destination] = _bus.ReadLong(Registers.General[source]);
                if (destination != source)
                {
                    Registers.General[source] += 4;
                }

                Trace($"0x{pc:X8}: MOV.L @R{source}+,R{destination}");
                return;
            }
            case 0x600C:
            {
                var destination = (opcode >> 8) & 0xF;
                var source = (opcode >> 4) & 0xF;
                Registers.General[destination] = Registers.General[source] & 0xFF;
                Trace($"0x{pc:X8}: EXTU.B R{source},R{destination}");
                return;
            }
            case 0x600D:
            {
                var destination = (opcode >> 8) & 0xF;
                var source = (opcode >> 4) & 0xF;
                Registers.General[destination] = Registers.General[source] & 0xFFFF;
                Trace($"0x{pc:X8}: EXTU.W R{source},R{destination}");
                return;
            }
            case 0x600E:
            {
                var destination = (opcode >> 8) & 0xF;
                var source = (opcode >> 4) & 0xF;
                Registers.General[destination] = (uint)(int)(sbyte)Registers.General[source];
                Trace($"0x{pc:X8}: EXTS.B R{source},R{destination}");
                return;
            }
            case 0x600F:
            {
                var destination = (opcode >> 8) & 0xF;
                var source = (opcode >> 4) & 0xF;
                Registers.General[destination] = (uint)(int)(short)Registers.General[source];
                Trace($"0x{pc:X8}: EXTS.W R{source},R{destination}");
                return;
            }
            case 0x2002:
            {
                var destination = (opcode >> 8) & 0xF;
                var source = (opcode >> 4) & 0xF;
                _bus.WriteLong(Registers.General[destination], Registers.General[source]);
                Trace($"0x{pc:X8}: MOV.L R{source},@R{destination}");
                return;
            }
            case 0x200A:
            {
                var destination = (opcode >> 8) & 0xF;
                var source = (opcode >> 4) & 0xF;
                Registers.General[destination] ^= Registers.General[source];
                Trace($"0x{pc:X8}: XOR R{source},R{destination}");
                return;
            }
            case 0x300C:
            {
                var destination = (opcode >> 8) & 0xF;
                var source = (opcode >> 4) & 0xF;
                Registers.General[destination] += Registers.General[source];
                Trace($"0x{pc:X8}: ADD R{source},R{destination}");
                return;
            }
        }

        switch (opcode & 0xF0FF)
        {
            case 0x0003:
            {
                var register = (opcode >> 8) & 0xF;
                var target = pc + 4 + Registers.General[register];
                Registers.ProcedureRegister = pc + 4;
                ExecuteDelaySlot();
                Registers.ProgramCounter = target;
                Trace($"0x{pc:X8}: BSRF R{register} -> 0x{target:X8}");
                return;
            }
            case 0x0023:
            {
                var register = (opcode >> 8) & 0xF;
                var target = pc + 4 + Registers.General[register];
                ExecuteDelaySlot();
                Registers.ProgramCounter = target;
                Trace($"0x{pc:X8}: BRAF R{register} -> 0x{target:X8}");
                return;
            }
            case 0x001A:
            {
                var register = (opcode >> 8) & 0xF;
                Registers.General[register] = Registers.ProcedureRegister;
                Trace($"0x{pc:X8}: STS PR,R{register}");
                return;
            }
            case 0x002A:
            {
                var register = (opcode >> 8) & 0xF;
                Registers.General[register] = Registers.ProcedureRegister;
                Trace($"0x{pc:X8}: STS PR,R{register}");
                return;
            }
            case 0x4000:
            {
                var register = (opcode >> 8) & 0xF;
                Registers.General[register] <<= 1;
                Trace($"0x{pc:X8}: SHLL R{register}");
                return;
            }
            case 0x4010:
            {
                var register = (opcode >> 8) & 0xF;
                Registers.General[register]--;
                Registers.T = Registers.General[register] == 0;
                Trace($"0x{pc:X8}: DT R{register} -> 0x{Registers.General[register]:X8} T={Registers.T}");
                return;
            }
            case 0x401E:
            {
                var register = (opcode >> 8) & 0xF;
                Registers.GlobalBaseRegister = Registers.General[register];
                Trace($"0x{pc:X8}: LDC R{register},GBR <- 0x{Registers.GlobalBaseRegister:X8}");
                return;
            }
            case 0x4028:
            {
                var register = (opcode >> 8) & 0xF;
                Registers.General[register] <<= 2;
                Trace($"0x{pc:X8}: SHLL2 R{register}");
                return;
            }
            case 0x402A:
            {
                var register = (opcode >> 8) & 0xF;
                Registers.ProcedureRegister = Registers.General[register];
                Trace($"0x{pc:X8}: LDS R{register},PR <- 0x{Registers.ProcedureRegister:X8}");
                return;
            }
            case 0x402E:
            {
                var register = (opcode >> 8) & 0xF;
                Registers.VectorBaseRegister = Registers.General[register];
                Trace($"0x{pc:X8}: LDC R{register},VBR <- 0x{Registers.VectorBaseRegister:X8}");
                return;
            }
        }

        switch (opcode)
        {
            case 0x0009:
                Trace($"0x{pc:X8}: NOP");
                break;
            case 0x000B:
                ExecuteDelaySlot();
                Registers.ProgramCounter = Registers.ProcedureRegister;
                Trace($"0x{pc:X8}: RTS -> 0x{Registers.ProgramCounter:X8}");
                break;
            default:
                RecordUnimplemented(pc, opcode);
                Trace($"0x{pc:X8}: opcode=0x{opcode:X4} (unimplemented)");
                return;
        }
    }

    private static uint BranchTarget(uint instructionAddress, int displacement)
    {
        if ((displacement & 0x800) != 0)
        {
            displacement |= unchecked((int)0xFFFF_F000);
        }

        return (uint)(instructionAddress + 4 + (displacement * 2));
    }

    private static int SignExtend8(int value) => (sbyte)(value & 0xFF);

    private void ExecuteGbrMove(uint pc, ushort opcode)
    {
        var displacement = (uint)(opcode & 0xFF);

        switch (opcode & 0xFF00)
        {
            case 0xC000:
            {
                var address = Registers.GlobalBaseRegister + displacement;
                _bus.WriteByte(address, (byte)Registers.General[0]);
                Trace($"0x{pc:X8}: MOV.B R0,@(0x{displacement:X2},GBR)");
                return;
            }
            case 0xC100:
            {
                var address = Registers.GlobalBaseRegister + (displacement * 2);
                _bus.WriteWord(address, (ushort)Registers.General[0]);
                Trace($"0x{pc:X8}: MOV.W R0,@(0x{displacement:X2},GBR)");
                return;
            }
            case 0xC200:
            {
                var address = Registers.GlobalBaseRegister + (displacement * 4);
                _bus.WriteLong(address, Registers.General[0]);
                Trace($"0x{pc:X8}: MOV.L R0,@(0x{displacement:X2},GBR)");
                return;
            }
            case 0xC400:
            {
                var address = Registers.GlobalBaseRegister + displacement;
                Registers.General[0] = (uint)(sbyte)_bus.ReadByte(address);
                Trace($"0x{pc:X8}: MOV.B @(0x{displacement:X2},GBR),R0");
                return;
            }
            case 0xC500:
            {
                var address = Registers.GlobalBaseRegister + (displacement * 2);
                Registers.General[0] = (uint)(short)_bus.ReadWord(address);
                Trace($"0x{pc:X8}: MOV.W @(0x{displacement:X2},GBR),R0");
                return;
            }
            case 0xC600:
            {
                var address = Registers.GlobalBaseRegister + (displacement * 4);
                Registers.General[0] = _bus.ReadLong(address);
                Trace($"0x{pc:X8}: MOV.L @(0x{displacement:X2},GBR),R0");
                return;
            }
        }
    }

    private void ExecuteDelaySlot()
    {
        Trace($"delay slot @ 0x{Registers.ProgramCounter:X8}");
        StepInstruction();
    }

    private void RecordUnimplemented(uint pc, ushort opcode)
    {
        FirstUnimplementedProgramCounter ??= pc;
        FirstUnimplementedOpcode ??= opcode;
        LastUnimplementedProgramCounter = pc;
        LastUnimplementedOpcode = opcode;
        UnimplementedInstructionCount++;
    }

    private void Trace(string message)
    {
        _trace?.Write(new TraceEvent(Name, _cycles, message));
    }
}
