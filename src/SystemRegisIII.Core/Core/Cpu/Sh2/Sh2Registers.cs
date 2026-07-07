namespace SystemRegisIII.Core.Core.Cpu.Sh2;

public sealed class Sh2Registers
{
    private const uint InterruptMask = 0x0000_00F0;

    public uint ProgramCounter { get; set; }

    public uint StatusRegister { get; set; }

    public uint ProcedureRegister { get; set; }

    public uint GlobalBaseRegister { get; set; }

    public uint VectorBaseRegister { get; set; }

    public uint[] General { get; } = new uint[16];

    public bool T
    {
        get => (StatusRegister & 1) != 0;
        set => StatusRegister = value ? StatusRegister | 1u : StatusRegister & ~1u;
    }

    public int InterruptLevelMask
    {
        get => (int)((StatusRegister & InterruptMask) >> 4);
        set => StatusRegister = (StatusRegister & ~InterruptMask) | (((uint)value & 0xF) << 4);
    }
}
