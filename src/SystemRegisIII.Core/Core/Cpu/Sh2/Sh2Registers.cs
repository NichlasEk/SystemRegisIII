namespace SystemRegisIII.Core.Core.Cpu.Sh2;

public sealed class Sh2Registers
{
    public uint ProgramCounter { get; set; }

    public uint StatusRegister { get; set; }

    public uint ProcedureRegister { get; set; }

    public uint[] General { get; } = new uint[16];

    public bool T
    {
        get => (StatusRegister & 1) != 0;
        set => StatusRegister = value ? StatusRegister | 1u : StatusRegister & ~1u;
    }
}
