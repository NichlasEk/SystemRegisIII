using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Core.Core.Scu;

public sealed class ScuRegisterBusDevice : IInspectableBusDevice
{
    private const uint InterruptMaskOffset = 0x0E00A0;
    private const uint InterruptStatusOffset = 0x0E00A4;
    private const uint VBlankInBit = 1u << 0;
    private const uint VBlankOutBit = 1u << 1;
    private const uint SmpcBit = 1u << 7;
    private readonly Dictionary<uint, byte> _registers = [];
    private readonly Dictionary<uint, long> _readOffsets = [];
    private readonly Dictionary<uint, long> _writeOffsets = [];

    public string Name => "SCU / System Control Area";
    public long ReadCount { get; private set; }
    public long WriteCount { get; private set; }
    public uint? FirstReadOffset { get; private set; }
    public uint? LastReadOffset { get; private set; }
    public uint? FirstWriteOffset { get; private set; }
    public uint? LastWriteOffset { get; private set; }
    public uint InterruptMask { get; private set; } = 0xFFFF_FFFF;
    public uint InterruptStatus { get; private set; }
    public uint LastInterruptStatusWrite { get; private set; } = 0xFFFF_FFFF;
    public bool HasPendingVBlankIn => (InterruptStatus & VBlankInBit) != 0 && (InterruptMask & VBlankInBit) == 0;
    public bool HasPendingVBlankOut => (InterruptStatus & VBlankOutBit) != 0 && (InterruptMask & VBlankOutBit) == 0;
    public bool HasPendingSmpc => (InterruptStatus & SmpcBit) != 0 && (InterruptMask & SmpcBit) == 0;

    public byte ReadByte(uint offset)
    {
        ReadCount++;
        FirstReadOffset ??= offset;
        LastReadOffset = offset;
        RecordOffset(_readOffsets, offset);

        if (IsInterruptMask(offset))
        {
            return ReadWordByte(InterruptMask, offset - InterruptMaskOffset);
        }

        if (IsInterruptStatus(offset))
        {
            return ReadWordByte(InterruptStatus, offset - InterruptStatusOffset);
        }

        return _registers.TryGetValue(offset, out var value) ? value : (byte)0;
    }

    public void WriteByte(uint offset, byte value)
    {
        WriteCount++;
        FirstWriteOffset ??= offset;
        LastWriteOffset = offset;
        RecordOffset(_writeOffsets, offset);

        if (IsInterruptMask(offset))
        {
            InterruptMask = WriteWordByte(InterruptMask, offset - InterruptMaskOffset, value);
            return;
        }

        if (IsInterruptStatus(offset))
        {
            var written = WriteWordByte(0xFFFF_FFFF, offset - InterruptStatusOffset, value);
            LastInterruptStatusWrite = WriteWordByte(LastInterruptStatusWrite, offset - InterruptStatusOffset, value);
            InterruptStatus &= written;
            return;
        }

        _registers[offset] = value;
    }

    public void RaiseVBlankIn() => InterruptStatus |= VBlankInBit;

    public void RaiseVBlankOut() => InterruptStatus |= VBlankOutBit;

    public void RaiseSmpc() => InterruptStatus |= SmpcBit;

    public void AcknowledgeVBlankIn() => InterruptStatus &= ~VBlankInBit;

    public IReadOnlyList<(uint Offset, long Count)> GetHotReadOffsets(int count) =>
        GetHotOffsets(_readOffsets, count);

    public IReadOnlyList<(uint Offset, long Count)> GetHotWriteOffsets(int count) =>
        GetHotOffsets(_writeOffsets, count);

    private static bool IsInterruptMask(uint offset) => offset is >= InterruptMaskOffset and < InterruptMaskOffset + 4;

    private static bool IsInterruptStatus(uint offset) => offset is >= InterruptStatusOffset and < InterruptStatusOffset + 4;

    private static byte ReadWordByte(uint value, uint byteOffset)
    {
        var shift = (int)((3 - byteOffset) * 8);
        return (byte)(value >> shift);
    }

    private static uint WriteWordByte(uint current, uint byteOffset, byte value)
    {
        var shift = (int)((3 - byteOffset) * 8);
        var mask = 0xFFu << shift;
        return (current & ~mask) | ((uint)value << shift);
    }

    private static void RecordOffset(Dictionary<uint, long> offsets, uint offset)
    {
        offsets.TryGetValue(offset, out var count);
        offsets[offset] = count + 1;
    }

    private static IReadOnlyList<(uint Offset, long Count)> GetHotOffsets(Dictionary<uint, long> offsets, int count) =>
        offsets
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .Take(count)
            .Select(static pair => (pair.Key, pair.Value))
            .ToArray();
}
