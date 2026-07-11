namespace SystemRegisIII.Core.Core.Vdp2;

public static class Vdp2TilemapRenderer
{
    public static uint[] Render(
        ReadOnlySpan<byte> vram,
        ReadOnlySpan<byte> colorRam,
        ReadOnlySpan<byte> registers,
        int width = 320,
        int height = 224)
    {
        var rows = Vdp2BackScreenRenderer.CreateRows(vram, registers, height);
        var pixels = new uint[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            pixels.AsSpan(y * width, width).Fill(rows[y]);
        }

        if (vram.IsEmpty || colorRam.IsEmpty || registers.Length < 0xFC)
        {
            return pixels;
        }

        var bgon = ReadWord(registers, 0x20);
        var priorities = new[]
        {
            ReadWord(registers, 0xF8) & 0x7,
            (ReadWord(registers, 0xF8) >> 8) & 0x7,
            ReadWord(registers, 0xFA) & 0x7,
            (ReadWord(registers, 0xFA) >> 8) & 0x7,
        };
        foreach (var layer in Enumerable.Range(0, 4).OrderBy(layer => priorities[layer]))
        {
            if ((bgon & (1 << layer)) == 0 || priorities[layer] == 0)
            {
                continue;
            }

            DrawLayer(vram, colorRam, registers, pixels, width, height, layer, (bgon & (0x100 << layer)) != 0);
        }

        return pixels;
    }

    private static void DrawLayer(
        ReadOnlySpan<byte> vram,
        ReadOnlySpan<byte> colorRam,
        ReadOnlySpan<byte> registers,
        Span<uint> destination,
        int width,
        int height,
        int layer,
        bool transparencyDisabled)
    {
        var chctla = ReadWord(registers, 0x28);
        var chctlb = ReadWord(registers, 0x2A);
        var charSize = layer switch
        {
            0 => chctla & 1,
            1 => (chctla >> 8) & 1,
            2 => chctlb & 1,
            _ => (chctlb >> 4) & 1,
        };
        var colorMode = layer switch
        {
            0 => (chctla >> 4) & 0x7,
            1 => (chctla >> 12) & 0x3,
            2 => (chctlb >> 1) & 1,
            _ => (chctlb >> 5) & 1,
        };
        if (colorMode > 1)
        {
            return;
        }

        var bitsPerPixel = colorMode == 0 ? 4 : 8;
        var pncn = ReadWord(registers, 0x30 + (layer * 2));
        var oneWordPattern = (pncn & 0x8000) != 0;
        var auxiliaryMode = (pncn & 0x4000) != 0;
        var supplement = pncn & 0x3FF;
        var planeSize = (ReadWord(registers, 0x3A) >> (layer * 2)) & 0x3;
        var mapOffset = (ReadWord(registers, 0x3C) >> (layer * 4)) & 0x7;
        var mapRegisters = ReadMapRegisters(registers, layer);
        var cramOffset = (ReadWord(registers, 0xE4) >> (layer * 4)) & 0x7;
        var (scrollX, scrollY) = ReadScroll(registers, layer);
        var patternShift = 13 - (oneWordPattern ? 1 : 0) - (charSize << 1);

        for (var y = 0; y < height; y++)
        {
            var sourceY = y + scrollY;
            for (var x = 0; x < width; x++)
            {
                var sourceX = x + scrollX;
                var mapIndex = ((sourceX >> (9 + ((planeSize & 1) != 0 ? 1 : 0))) & 1) |
                    ((sourceY >> (7 + ((planeSize & 2) != 0 ? 1 : 0))) & 2);
                var adjustedMap = ((mapOffset << 6) + (mapRegisters[mapIndex] & ~planeSize)) << patternShift;
                var planeOffset = (((sourceX >> 9) & planeSize & 1) |
                    ((sourceY >> 8) & planeSize & 2)) << patternShift;
                var pageOffset = ((((sourceX >> 3) & 0x3F) >> charSize) +
                    ((((sourceY >> 3) & 0x3F) >> charSize) << (6 - charSize))) << (oneWordPattern ? 0 : 1);
                var nameAddress = (adjustedMap + planeOffset + pageOffset) & 0x3FFFF;
                DecodePattern(vram, nameAddress, oneWordPattern, auxiliaryMode, supplement, bitsPerPixel,
                    out var character, out var palette, out var horizontalFlip, out var verticalFlip);

                var cellX = sourceX & 7;
                var cellY = sourceY & 7;
                if (horizontalFlip) cellX ^= 7;
                if (verticalFlip) cellY ^= 7;
                if (charSize != 0)
                {
                    var cellIndex = (((sourceX >> 3) ^ (horizontalFlip ? 1 : 0)) & 1) |
                        (((sourceY >> 3) ^ (verticalFlip ? 2 : 0)) & 2);
                    character = (character + (cellIndex * (bitsPerPixel >> 2))) & 0x7FFF;
                }

                var characterAddress = ((character << 4) + ((cellY * bitsPerPixel) >> 1)) & 0x3FFFF;
                var dot = bitsPerPixel == 8
                    ? ReadByteFromWords(vram, characterAddress, cellX)
                    : ReadNibbleFromWords(vram, characterAddress, cellX);
                if (dot == 0 && !transparencyDisabled)
                {
                    continue;
                }

                var paletteBase = (((palette << 4) & ~((1 << bitsPerPixel) - 1)) + (cramOffset << 8));
                destination[(y * width) + x] = ReadColor(colorRam, paletteBase + dot);
            }
        }
    }

    private static void DecodePattern(
        ReadOnlySpan<byte> vram, int address, bool oneWord, bool auxiliaryMode, int supplement, int bitsPerPixel,
        out int character, out int palette, out bool horizontalFlip, out bool verticalFlip)
    {
        var first = ReadVramWord(vram, address);
        if (!oneWord)
        {
            palette = first & 0x7F;
            verticalFlip = (first & 0x8000) != 0;
            horizontalFlip = (first & 0x4000) != 0;
            character = ReadVramWord(vram, address + 1) & 0x7FFF;
            return;
        }

        palette = bitsPerPixel >= 8 ? ((first >> 12) & 7) << 4 : (first >> 12) & 0xF;
        if (!auxiliaryMode)
        {
            verticalFlip = (first & 0x800) != 0;
            horizontalFlip = (first & 0x400) != 0;
            character = (first & 0x3FF) + ((supplement & 0x1F) << 10);
        }
        else
        {
            verticalFlip = horizontalFlip = false;
            character = (first & 0xFFF) + ((supplement & 0x1C) << 10);
        }
    }

    private static int[] ReadMapRegisters(ReadOnlySpan<byte> registers, int layer)
    {
        var offset = 0x40 + (layer * 4);
        var first = ReadWord(registers, offset);
        var second = ReadWord(registers, offset + 2);
        return [first & 0x3F, (first >> 8) & 0x3F, second & 0x3F, (second >> 8) & 0x3F];
    }

    private static (int X, int Y) ReadScroll(ReadOnlySpan<byte> registers, int layer) => layer switch
    {
        0 => (ReadWord(registers, 0x70) & 0x7FF, ReadWord(registers, 0x74) & 0x7FF),
        1 => (ReadWord(registers, 0x80) & 0x7FF, ReadWord(registers, 0x84) & 0x7FF),
        2 => (ReadWord(registers, 0x90) & 0x7FF, ReadWord(registers, 0x92) & 0x7FF),
        _ => (ReadWord(registers, 0x94) & 0x7FF, ReadWord(registers, 0x96) & 0x7FF),
    };

    private static int ReadByteFromWords(ReadOnlySpan<byte> vram, int wordAddress, int x)
    {
        var word = ReadVramWord(vram, wordAddress + (x >> 1));
        return (word >> (((x & 1) ^ 1) * 8)) & 0xFF;
    }

    private static int ReadNibbleFromWords(ReadOnlySpan<byte> vram, int wordAddress, int x)
    {
        var word = ReadVramWord(vram, wordAddress + (x >> 2));
        return (word >> (((x & 3) ^ 3) * 4)) & 0xF;
    }

    private static uint ReadColor(ReadOnlySpan<byte> colorRam, int index)
    {
        var color = ReadWord(colorRam, (index & 0x7FF) * 2);
        var red = Expand5(color & 0x1F);
        var green = Expand5((color >> 5) & 0x1F);
        var blue = Expand5((color >> 10) & 0x1F);
        return 0xFF00_0000u | ((uint)red << 16) | ((uint)green << 8) | (uint)blue;
    }

    private static ushort ReadVramWord(ReadOnlySpan<byte> vram, int wordAddress) =>
        ReadWord(vram, (wordAddress & 0x3FFFF) * 2);

    private static ushort ReadWord(ReadOnlySpan<byte> memory, int byteAddress)
    {
        var offset = byteAddress % memory.Length;
        return (ushort)((memory[offset] << 8) | memory[(offset + 1) % memory.Length]);
    }

    private static int Expand5(int value) => (value << 3) | (value >> 2);
}
