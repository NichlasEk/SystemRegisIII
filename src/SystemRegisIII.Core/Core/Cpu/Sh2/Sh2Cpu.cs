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
        var cycles = budget.MasterCycles > 0 ? budget.MasterCycles : budget.SlaveCycles;
        if (cycles <= 0)
        {
            return;
        }

        var instructions = Math.Max(1, Math.Min(256, cycles / 4));

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

        if ((opcode & 0xF000) == 0x1000)
        {
            var destination = (opcode >> 8) & 0xF;
            var source = (opcode >> 4) & 0xF;
            var displacement = (uint)(opcode & 0xF);
            var address = Registers.General[destination] + (displacement * 4);
            _bus.WriteLong(address, Registers.General[source]);
            Trace($"0x{pc:X8}: MOV.L R{source},@(0x{displacement:X1},R{destination})");
            return;
        }

        if ((opcode & 0xF000) == 0x5000)
        {
            var destination = (opcode >> 8) & 0xF;
            var source = (opcode >> 4) & 0xF;
            var displacement = (uint)(opcode & 0xF);
            Registers.General[destination] = _bus.ReadLong(Registers.General[source] + (displacement * 4));
            Trace($"0x{pc:X8}: MOV.L @(0x{displacement:X1},R{source}),R{destination}");
            return;
        }

        if ((opcode & 0xFF00) is 0xC000 or 0xC100 or 0xC200 or 0xC400 or 0xC500 or 0xC600)
        {
            ExecuteGbrMove(pc, opcode);
            return;
        }

        if ((opcode & 0xFF00) == 0xC700)
        {
            var displacement = opcode & 0xFF;
            Registers.General[0] = ((Registers.ProgramCounter + 2) & 0xFFFF_FFFCu) + (uint)(displacement * 4);
            Trace($"0x{pc:X8}: MOVA @(0x{displacement:X2},PC),R0 -> 0x{Registers.General[0]:X8}");
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

        if ((opcode & 0xFF00) == 0x8800)
        {
            var immediate = (uint)(int)(sbyte)(opcode & 0xFF);
            Registers.T = Registers.General[0] == immediate;
            Trace($"0x{pc:X8}: CMP/EQ #0x{opcode & 0xFF:X2},R0 T={Registers.T}");
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

        if ((opcode & 0xFF00) == 0x8D00)
        {
            var displacement = SignExtend8(opcode & 0xFF) * 2;
            var target = (uint)(Registers.ProgramCounter + 2 + displacement);
            Trace($"0x{pc:X8}: BT/S 0x{target:X8} T={Registers.T}");
            ExecuteDelaySlot();
            if (Registers.T)
            {
                Registers.ProgramCounter = target;
            }

            return;
        }

        switch (opcode & 0xFF00)
        {
            case 0x8000:
                {
                    var register = (opcode >> 4) & 0xF;
                    var displacement = (uint)(opcode & 0xF);
                    _bus.WriteByte(Registers.General[register] + displacement, (byte)Registers.General[0]);
                    Trace($"0x{pc:X8}: MOV.B R0,@(0x{displacement:X1},R{register})");
                    return;
                }
            case 0x8100:
                {
                    var register = (opcode >> 4) & 0xF;
                    var displacement = (uint)(opcode & 0xF);
                    _bus.WriteWord(Registers.General[register] + (displacement * 2), (ushort)Registers.General[0]);
                    Trace($"0x{pc:X8}: MOV.W R0,@(0x{displacement:X1},R{register})");
                    return;
                }
            case 0x8400:
                {
                    var register = (opcode >> 4) & 0xF;
                    var displacement = (uint)(opcode & 0xF);
                    Registers.General[0] = (uint)(int)(sbyte)_bus.ReadByte(Registers.General[register] + displacement);
                    Trace($"0x{pc:X8}: MOV.B @(0x{displacement:X1},R{register}),R0");
                    return;
                }
            case 0x8500:
                {
                    var register = (opcode >> 4) & 0xF;
                    var displacement = (uint)(opcode & 0xF);
                    Registers.General[0] = (uint)(int)(short)_bus.ReadWord(Registers.General[register] + (displacement * 2));
                    Trace($"0x{pc:X8}: MOV.W @(0x{displacement:X1},R{register}),R0");
                    return;
                }
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

        if ((opcode & 0xF000) == 0x9000)
        {
            var register = (opcode >> 8) & 0xF;
            var displacement = opcode & 0xFF;
            var address = Registers.ProgramCounter + 2 + (uint)(displacement * 2);
            Registers.General[register] = (uint)(int)(short)_bus.ReadWord(address);
            Trace($"0x{pc:X8}: MOV.W @(0x{displacement:X2},PC),R{register} <- 0x{Registers.General[register]:X8}");
            return;
        }

        switch (opcode & 0xFF00)
        {
            case 0xC800:
                {
                    var immediate = (uint)(opcode & 0xFF);
                    Registers.T = (Registers.General[0] & immediate) == 0;
                    Trace($"0x{pc:X8}: TST #0x{immediate:X2},R0 T={Registers.T}");
                    return;
                }
            case 0xC900:
                {
                    var immediate = (uint)(opcode & 0xFF);
                    Registers.General[0] &= immediate;
                    Trace($"0x{pc:X8}: AND #0x{immediate:X2},R0");
                    return;
                }
            case 0xCA00:
                {
                    var immediate = (uint)(opcode & 0xFF);
                    Registers.General[0] ^= immediate;
                    Trace($"0x{pc:X8}: XOR #0x{immediate:X2},R0");
                    return;
                }
            case 0xCB00:
                {
                    var immediate = (uint)(opcode & 0xFF);
                    Registers.General[0] |= immediate;
                    Trace($"0x{pc:X8}: OR #0x{immediate:X2},R0");
                    return;
                }
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
            case 0x0004:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    _bus.WriteByte(Registers.General[0] + Registers.General[destination], (byte)Registers.General[source]);
                    Trace($"0x{pc:X8}: MOV.B R{source},@(R0,R{destination})");
                    return;
                }
            case 0x0005:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    _bus.WriteWord(Registers.General[0] + Registers.General[destination], (ushort)Registers.General[source]);
                    Trace($"0x{pc:X8}: MOV.W R{source},@(R0,R{destination})");
                    return;
                }
            case 0x0006:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    _bus.WriteLong(Registers.General[0] + Registers.General[destination], Registers.General[source]);
                    Trace($"0x{pc:X8}: MOV.L R{source},@(R0,R{destination})");
                    return;
                }
            case 0x000C:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.General[destination] =
                        (uint)(int)(sbyte)_bus.ReadByte(Registers.General[0] + Registers.General[source]);
                    Trace($"0x{pc:X8}: MOV.B @(R0,R{source}),R{destination}");
                    return;
                }
            case 0x000D:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.General[destination] =
                        (uint)(int)(short)_bus.ReadWord(Registers.General[0] + Registers.General[source]);
                    Trace($"0x{pc:X8}: MOV.W @(R0,R{source}),R{destination}");
                    return;
                }
            case 0x000E:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.General[destination] = _bus.ReadLong(Registers.General[0] + Registers.General[source]);
                    Trace($"0x{pc:X8}: MOV.L @(R0,R{source}),R{destination}");
                    return;
                }
            case 0x3000:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.T = Registers.General[destination] == Registers.General[source];
                    Trace($"0x{pc:X8}: CMP/EQ R{source},R{destination} T={Registers.T}");
                    return;
                }
            case 0x3002:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.T = Registers.General[destination] >= Registers.General[source];
                    Trace($"0x{pc:X8}: CMP/HS R{source},R{destination} T={Registers.T}");
                    return;
                }
            case 0x3007:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.T = (int)Registers.General[destination] > (int)Registers.General[source];
                    Trace($"0x{pc:X8}: CMP/GT R{source},R{destination} T={Registers.T}");
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
            case 0x6003:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.General[destination] = Registers.General[source];
                    Trace($"0x{pc:X8}: MOV R{source},R{destination}");
                    return;
                }
            case 0x6000:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.General[destination] = (uint)(int)(sbyte)_bus.ReadByte(Registers.General[source]);
                    Trace($"0x{pc:X8}: MOV.B @R{source},R{destination}");
                    return;
                }
            case 0x6001:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.General[destination] = (uint)(int)(short)_bus.ReadWord(Registers.General[source]);
                    Trace($"0x{pc:X8}: MOV.W @R{source},R{destination}");
                    return;
                }
            case 0x6004:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.General[destination] = (uint)(int)(sbyte)_bus.ReadByte(Registers.General[source]);
                    if (destination != source)
                    {
                        Registers.General[source] += 1;
                    }

                    Trace($"0x{pc:X8}: MOV.B @R{source}+,R{destination}");
                    return;
                }
            case 0x6005:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.General[destination] = (uint)(int)(short)_bus.ReadWord(Registers.General[source]);
                    if (destination != source)
                    {
                        Registers.General[source] += 2;
                    }

                    Trace($"0x{pc:X8}: MOV.W @R{source}+,R{destination}");
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
            case 0x6008:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    var value = Registers.General[source];
                    Registers.General[destination] = (value & 0xFFFF_0000)
                                                     | ((value & 0x0000_00FF) << 8)
                                                     | ((value & 0x0000_FF00) >> 8);
                    Trace($"0x{pc:X8}: SWAP.B R{source},R{destination}");
                    return;
                }
            case 0x6009:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    var value = Registers.General[source];
                    Registers.General[destination] = (value << 16) | (value >> 16);
                    Trace($"0x{pc:X8}: SWAP.W R{source},R{destination}");
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
            case 0x2004:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.General[destination]--;
                    _bus.WriteByte(Registers.General[destination], (byte)Registers.General[source]);
                    Trace($"0x{pc:X8}: MOV.B R{source},@-R{destination}");
                    return;
                }
            case 0x2005:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.General[destination] -= 2;
                    _bus.WriteWord(Registers.General[destination], (ushort)Registers.General[source]);
                    Trace($"0x{pc:X8}: MOV.W R{source},@-R{destination}");
                    return;
                }
            case 0x2006:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.General[destination] -= 4;
                    _bus.WriteLong(Registers.General[destination], Registers.General[source]);
                    Trace($"0x{pc:X8}: MOV.L R{source},@-R{destination}");
                    return;
                }
            case 0x2008:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.T = (Registers.General[destination] & Registers.General[source]) == 0;
                    Trace($"0x{pc:X8}: TST R{source},R{destination} T={Registers.T}");
                    return;
                }
            case 0x2009:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.General[destination] &= Registers.General[source];
                    Trace($"0x{pc:X8}: AND R{source},R{destination}");
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
            case 0x200B:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.General[destination] |= Registers.General[source];
                    Trace($"0x{pc:X8}: OR R{source},R{destination}");
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
            case 0x3008:
                {
                    var destination = (opcode >> 8) & 0xF;
                    var source = (opcode >> 4) & 0xF;
                    Registers.General[destination] -= Registers.General[source];
                    Trace($"0x{pc:X8}: SUB R{source},R{destination}");
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
            case 0x0002:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.General[register] = Registers.StatusRegister;
                    Trace($"0x{pc:X8}: STC SR,R{register}");
                    return;
                }
            case 0x0012:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.General[register] = Registers.GlobalBaseRegister;
                    Trace($"0x{pc:X8}: STC GBR,R{register}");
                    return;
                }
            case 0x0022:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.General[register] = Registers.VectorBaseRegister;
                    Trace($"0x{pc:X8}: STC VBR,R{register}");
                    return;
                }
            case 0x002A:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.General[register] = Registers.ProcedureRegister;
                    Trace($"0x{pc:X8}: STS PR,R{register}");
                    return;
                }
            case 0x0029:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.General[register] = Registers.T ? 1u : 0u;
                    Trace($"0x{pc:X8}: MOVT R{register}");
                    return;
                }
            case 0x4000:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.T = (Registers.General[register] & 0x8000_0000) != 0;
                    Registers.General[register] <<= 1;
                    Trace($"0x{pc:X8}: SHLL R{register} T={Registers.T}");
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
            case 0x4011:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.T = (int)Registers.General[register] >= 0;
                    Trace($"0x{pc:X8}: CMP/PZ R{register} T={Registers.T}");
                    return;
                }
            case 0x4015:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.T = (int)Registers.General[register] > 0;
                    Trace($"0x{pc:X8}: CMP/PL R{register} T={Registers.T}");
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
            case 0x4008:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.General[register] <<= 2;
                    Trace($"0x{pc:X8}: SHLL2 R{register}");
                    return;
                }
            case 0x4009:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.General[register] >>= 2;
                    Trace($"0x{pc:X8}: SHLR2 R{register}");
                    return;
                }
            case 0x4018:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.General[register] <<= 8;
                    Trace($"0x{pc:X8}: SHLL8 R{register}");
                    return;
                }
            case 0x4019:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.General[register] >>= 8;
                    Trace($"0x{pc:X8}: SHLR8 R{register}");
                    return;
                }
            case 0x4021:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.T = (Registers.General[register] & 1) != 0;
                    Registers.General[register] = (uint)((int)Registers.General[register] >> 1);
                    Trace($"0x{pc:X8}: SHAR R{register} T={Registers.T}");
                    return;
                }
            case 0x4024:
                {
                    var register = (opcode >> 8) & 0xF;
                    var carry = Registers.T ? 1u : 0u;
                    Registers.T = (Registers.General[register] & 0x8000_0000) != 0;
                    Registers.General[register] = (Registers.General[register] << 1) | carry;
                    Trace($"0x{pc:X8}: ROTCL R{register} T={Registers.T}");
                    return;
                }
            case 0x402A:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.ProcedureRegister = Registers.General[register];
                    Trace($"0x{pc:X8}: LDS R{register},PR <- 0x{Registers.ProcedureRegister:X8}");
                    return;
                }
            case 0x400B:
                {
                    var register = (opcode >> 8) & 0xF;
                    var target = Registers.General[register];
                    Registers.ProcedureRegister = Registers.ProgramCounter + 2;
                    ExecuteDelaySlot();
                    Registers.ProgramCounter = target;
                    Trace($"0x{pc:X8}: JSR @R{register} -> 0x{target:X8}");
                    return;
                }
            case 0x402B:
                {
                    var register = (opcode >> 8) & 0xF;
                    var target = Registers.General[register];
                    ExecuteDelaySlot();
                    Registers.ProgramCounter = target;
                    Trace($"0x{pc:X8}: JMP @R{register} -> 0x{target:X8}");
                    return;
                }
            case 0x402E:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.VectorBaseRegister = Registers.General[register];
                    Trace($"0x{pc:X8}: LDC R{register},VBR <- 0x{Registers.VectorBaseRegister:X8}");
                    return;
                }
            case 0x400E:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.StatusRegister = Registers.General[register];
                    Trace($"0x{pc:X8}: LDC R{register},SR <- 0x{Registers.StatusRegister:X8}");
                    return;
                }
            case 0x4022:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.General[register] -= 4;
                    _bus.WriteLong(Registers.General[register], Registers.ProcedureRegister);
                    Trace($"0x{pc:X8}: STS.L PR,@-R{register}");
                    return;
                }
            case 0x4026:
                {
                    var register = (opcode >> 8) & 0xF;
                    Registers.ProcedureRegister = _bus.ReadLong(Registers.General[register]);
                    Registers.General[register] += 4;
                    Trace($"0x{pc:X8}: LDS.L @R{register}+,PR <- 0x{Registers.ProcedureRegister:X8}");
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
            case 0x002B:
                {
                    var returnPc = _bus.ReadLong(Registers.General[15]);
                    var returnSr = _bus.ReadLong(Registers.General[15] + 4);
                    Registers.General[15] += 8;
                    ExecuteDelaySlot();
                    Registers.ProgramCounter = returnPc;
                    Registers.StatusRegister = returnSr;
                    Trace($"0x{pc:X8}: RTE -> pc=0x{returnPc:X8} sr=0x{returnSr:X8}");
                    break;
                }
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
