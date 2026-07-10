using SystemRegisIII.Core.Host.Video;

namespace SystemRegisIII.Core.Core.Vdp1;

public sealed record Vdp1RenderResult(VideoFrame Frame, int DrawnSprites, int DrawnPixels);

public static class Vdp1SoftwareRenderer
{
    public static Vdp1RenderResult Render(
        ReadOnlySpan<byte> vram,
        ReadOnlySpan<byte> colorRam,
        IReadOnlyList<Vdp1Command> commands,
        int width = 320,
        int height = 224)
    {
        var pixels = new uint[checked(width * height)];
        Array.Fill(pixels, 0xFF00_0000u);
        var localX = 0;
        var localY = 0;
        var clipRight = width - 1;
        var clipBottom = height - 1;
        var drawnSprites = 0;
        var drawnPixels = 0;

        foreach (var command in commands)
        {
            if (command.End || command.Skip)
            {
                continue;
            }

            switch (command.CommandCode)
            {
                case 0x9:
                    clipRight = Math.Clamp(command.Xc & 0x1FFF, 0, width - 1);
                    clipBottom = Math.Clamp(command.Yc & 0x1FFF, 0, height - 1);
                    break;
                case 0xA:
                    localX = SignExtend(command.Xa, 11);
                    localY = SignExtend(command.Ya, 11);
                    break;
                case 0x0 when command.CharacterWidth > 0 && command.CharacterHeight > 0:
                    drawnSprites++;
                    drawnPixels += DrawNormalSprite(
                        vram,
                        colorRam,
                        pixels,
                        width,
                        command,
                        localX,
                        localY,
                        clipRight,
                        clipBottom);
                    break;
            }
        }

        return new Vdp1RenderResult(new VideoFrame(width, height, pixels), drawnSprites, drawnPixels);
    }

    private static int DrawNormalSprite(
        ReadOnlySpan<byte> vram,
        ReadOnlySpan<byte> colorRam,
        Span<uint> destination,
        int destinationWidth,
        Vdp1Command command,
        int localX,
        int localY,
        int clipRight,
        int clipBottom)
    {
        var originX = SignExtend(command.Xa, 13) + localX;
        var originY = SignExtend(command.Ya, 13) + localY;
        var horizontalFlip = (command.Control & 0x0010) != 0;
        var verticalFlip = (command.Control & 0x0020) != 0;
        var colorMode = (command.DrawMode >> 3) & 0x7;
        var showTransparentPixels = (command.DrawMode & 0x0040) != 0;
        var endCodesDisabled = (command.DrawMode & 0x0080) != 0;
        var drawn = 0;

        for (var destinationY = 0; destinationY < command.CharacterHeight; destinationY++)
        {
            var y = originY + destinationY;
            if (y < 0 || y > clipBottom)
            {
                continue;
            }

            var sourceY = verticalFlip ? command.CharacterHeight - 1 - destinationY : destinationY;
            var remainingEndCodes = 2;
            for (var destinationX = 0; destinationX < command.CharacterWidth; destinationX++)
            {
                var x = originX + destinationX;
                if (x < 0 || x > clipRight)
                {
                    continue;
                }

                var sourceX = horizontalFlip ? command.CharacterWidth - 1 - destinationX : destinationX;
                var pixelIndex = (sourceY * command.CharacterWidth) + sourceX;
                if (!endCodesDisabled && IsEndCode(vram, command, colorMode, pixelIndex))
                {
                    remainingEndCodes--;
                    if (remainingEndCodes == 0)
                    {
                        break;
                    }

                    continue;
                }

                var color = FetchColor(vram, colorRam, command, colorMode, sourceX, sourceY, showTransparentPixels);
                if (color is not uint bgra)
                {
                    continue;
                }

                destination[(y * destinationWidth) + x] = bgra;
                drawn++;
            }
        }

        return drawn;
    }

    private static bool IsEndCode(
        ReadOnlySpan<byte> vram,
        Vdp1Command command,
        int colorMode,
        int pixelIndex) => colorMode switch
        {
            0 or 1 => ReadPackedNibble(vram, command.CharacterByteAddress, pixelIndex) == 0xF,
            2 or 3 or 4 => ReadByte(vram, command.CharacterByteAddress + (uint)pixelIndex) == 0xFF,
            5 => (ReadWord(vram, command.CharacterByteAddress + (uint)(pixelIndex * 2)) & 0xC000) == 0x4000,
            _ => false,
        };

    private static uint? FetchColor(
        ReadOnlySpan<byte> vram,
        ReadOnlySpan<byte> colorRam,
        Vdp1Command command,
        int colorMode,
        int x,
        int y,
        bool showTransparentPixels)
    {
        var pixelIndex = (y * command.CharacterWidth) + x;
        int paletteIndex;
        switch (colorMode)
        {
            case 0:
                var nibble = ReadPackedNibble(vram, command.CharacterByteAddress, pixelIndex);
                if (nibble == 0 && !showTransparentPixels)
                {
                    return null;
                }

                paletteIndex = (command.Color & 0xFFF0) | nibble;
                return ReadPaletteColor(colorRam, paletteIndex);
            case 1:
                nibble = ReadPackedNibble(vram, command.CharacterByteAddress, pixelIndex);
                if (nibble == 0 && !showTransparentPixels)
                {
                    return null;
                }

                var lookupAddress = (uint)((command.Color & 0xFFFC) << 3) + (uint)(nibble * 2);
                return ConvertRgb555(ReadWord(vram, lookupAddress));
            case 2:
            case 3:
            case 4:
                var value = ReadByte(vram, command.CharacterByteAddress + (uint)pixelIndex);
                if (value == 0 && !showTransparentPixels)
                {
                    return null;
                }

                var mask = colorMode == 2 ? 0x3F : colorMode == 3 ? 0x7F : 0xFF;
                paletteIndex = (command.Color & ~mask) | (value & mask);
                return ReadPaletteColor(colorRam, paletteIndex);
            case 5:
                var rgb = ReadWord(vram, command.CharacterByteAddress + (uint)(pixelIndex * 2));
                if (rgb < 0x4000 && !showTransparentPixels)
                {
                    return null;
                }

                return ConvertRgb555(rgb);
            default:
                return null;
        }
    }

    private static uint ReadPaletteColor(ReadOnlySpan<byte> colorRam, int index) =>
        ConvertRgb555(ReadWord(colorRam, (uint)(index * 2)));

    private static uint ConvertRgb555(ushort color)
    {
        var red = Expand5(color & 0x1F);
        var green = Expand5((color >> 5) & 0x1F);
        var blue = Expand5((color >> 10) & 0x1F);
        return 0xFF00_0000u | ((uint)red << 16) | ((uint)green << 8) | (uint)blue;
    }

    private static int Expand5(int value) => (value << 3) | (value >> 2);

    private static int SignExtend(short value, int bits)
    {
        var mask = (1 << bits) - 1;
        var sign = 1 << (bits - 1);
        var result = value & mask;
        return (result ^ sign) - sign;
    }

    private static byte ReadByte(ReadOnlySpan<byte> memory, uint address) =>
        memory[(int)(address % (uint)memory.Length)];

    private static int ReadPackedNibble(ReadOnlySpan<byte> memory, uint baseAddress, int pixelIndex)
    {
        var packed = ReadByte(memory, baseAddress + (uint)(pixelIndex >> 1));
        return (pixelIndex & 1) == 0 ? packed >> 4 : packed & 0xF;
    }

    private static ushort ReadWord(ReadOnlySpan<byte> memory, uint address) =>
        (ushort)((ReadByte(memory, address) << 8) | ReadByte(memory, address + 1));
}
