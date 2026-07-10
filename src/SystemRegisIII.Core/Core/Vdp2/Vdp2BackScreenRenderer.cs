namespace SystemRegisIII.Core.Core.Vdp2;

public static class Vdp2BackScreenRenderer
{
    public static uint[] CreateRows(
        ReadOnlySpan<byte> vram,
        ReadOnlySpan<byte> registers,
        int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var rows = new uint[height];
        if (vram.IsEmpty || registers.Length < 0xB0)
        {
            Array.Fill(rows, 0xFF00_0000u);
            return rows;
        }

        var upper = ReadWord(registers, 0xAC);
        var lower = ReadWord(registers, 0xAE);
        var tableAddress = (uint)(((upper & 0x8007) << 16) | lower);
        var perLine = (tableAddress & 0x8000_0000) != 0;
        var wordAddress = tableAddress & 0x7_FFFF;

        for (var y = 0; y < height; y++)
        {
            var address = ((wordAddress + (perLine ? (uint)y : 0)) & 0x3_FFFF) * 2;
            rows[y] = ConvertRgb555((ushort)(ReadWord(vram, (int)address) & 0x7FFF));
        }

        return rows;
    }

    private static ushort ReadWord(ReadOnlySpan<byte> memory, int address)
    {
        var high = memory[address % memory.Length];
        var low = memory[(address + 1) % memory.Length];
        return (ushort)((high << 8) | low);
    }

    private static uint ConvertRgb555(ushort color)
    {
        var red = Expand5(color & 0x1F);
        var green = Expand5((color >> 5) & 0x1F);
        var blue = Expand5((color >> 10) & 0x1F);
        return 0xFF00_0000u | ((uint)red << 16) | ((uint)green << 8) | (uint)blue;
    }

    private static int Expand5(int value) => (value << 3) | (value >> 2);
}
