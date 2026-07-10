namespace SystemRegisIII.Core.Core.Vdp1;

public readonly record struct Vdp1Command(
    uint Address,
    ushort Control,
    ushort Link,
    ushort DrawMode,
    ushort Color,
    ushort CharacterAddress,
    ushort CharacterSize,
    short Xa,
    short Ya,
    short Xb,
    short Yb,
    short Xc,
    short Yc,
    short Xd,
    short Yd,
    ushort GouraudAddress)
{
    public bool End => (Control & 0x8000) != 0;
    public bool Skip => (Control & 0x4000) != 0;
    public int JumpMode => (Control >> 12) & 0x3;
    public int CommandCode => Control & 0xF;
    public uint LinkAddress => (uint)Link << 3;
    public uint CharacterByteAddress => (uint)CharacterAddress << 3;
    public int CharacterWidth => ((CharacterSize >> 8) & 0x3F) * 8;
    public int CharacterHeight => CharacterSize & 0xFF;

    public string CommandName => CommandCode switch
    {
        0x0 => "normal-sprite",
        0x1 => "scaled-sprite",
        0x2 or 0x3 => "distorted-sprite",
        0x4 => "polygon",
        0x5 => "polyline",
        0x6 or 0x7 => "line",
        0x8 or 0xB => "user-clip",
        0x9 => "system-clip",
        0xA => "local-coordinate",
        _ => "invalid",
    };

    public static Vdp1Command Read(ReadOnlySpan<byte> vram, uint address)
    {
        if (address > vram.Length - 0x20)
        {
            throw new ArgumentOutOfRangeException(nameof(address));
        }

        return new Vdp1Command(
            address,
            ReadWord(vram, address, 0),
            ReadWord(vram, address, 1),
            ReadWord(vram, address, 2),
            ReadWord(vram, address, 3),
            ReadWord(vram, address, 4),
            ReadWord(vram, address, 5),
            (short)ReadWord(vram, address, 6),
            (short)ReadWord(vram, address, 7),
            (short)ReadWord(vram, address, 8),
            (short)ReadWord(vram, address, 9),
            (short)ReadWord(vram, address, 10),
            (short)ReadWord(vram, address, 11),
            (short)ReadWord(vram, address, 12),
            (short)ReadWord(vram, address, 13),
            ReadWord(vram, address, 14));
    }

    private static ushort ReadWord(ReadOnlySpan<byte> vram, uint address, int index)
    {
        var offset = checked((int)address + (index * 2));
        return (ushort)((vram[offset] << 8) | vram[offset + 1]);
    }
}
