namespace SystemRegisIII.Core.Core.Cpu.Sh2;

public sealed class Sh2Registers
{
    private const uint InterruptMask = 0x0000_00F0;
    private const uint QMask = 0x0000_0100;
    private const uint MMask = 0x0000_0200;

    public uint ProgramCounter { get; set; }

    public uint StatusRegister { get; set; }

    public uint ProcedureRegister { get; set; }

    public uint GlobalBaseRegister { get; set; }

    public uint VectorBaseRegister { get; set; }

    public uint MacHigh { get; set; }

    public uint MacLow { get; set; }

    public uint[] General { get; } = new uint[16];

    public bool T
    {
        get => (StatusRegister & 1) != 0;
        set => StatusRegister = value ? StatusRegister | 1u : StatusRegister & ~1u;
    }

    public bool Q
    {
        get => (StatusRegister & QMask) != 0;
        set => StatusRegister = value ? StatusRegister | QMask : StatusRegister & ~QMask;
    }

    public bool M
    {
        get => (StatusRegister & MMask) != 0;
        set => StatusRegister = value ? StatusRegister | MMask : StatusRegister & ~MMask;
    }

    public int InterruptLevelMask
    {
        get => (int)((StatusRegister & InterruptMask) >> 4);
        set => StatusRegister = (StatusRegister & ~InterruptMask) | (((uint)value & 0xF) << 4);
    }
}
