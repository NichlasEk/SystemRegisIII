namespace SystemRegisIII.Core.Core.Cpu.Sh2;

public interface ISh2InstructionBus
{
    ushort ReadInstructionWord(uint address);
}
