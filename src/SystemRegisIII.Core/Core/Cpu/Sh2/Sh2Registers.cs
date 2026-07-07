namespace SystemRegisIII.Core.Core.Cpu.Sh2;

public sealed class Sh2Registers
{
    public uint ProgramCounter { get; set; }

    public uint StatusRegister { get; set; }

    public uint[] General { get; } = new uint[16];
}
