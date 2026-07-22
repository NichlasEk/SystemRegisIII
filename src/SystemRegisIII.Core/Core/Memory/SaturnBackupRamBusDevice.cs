using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Core.Core.Memory;

public sealed class SaturnBackupRamBusDevice : IInspectableBusDevice
{
    private const int SizeBytes = 32 * 1024;
    private const uint AddressMask = SizeBytes - 1;
    private static ReadOnlySpan<byte> FormatSignature => "BackUpRam Format"u8;

    private readonly byte[] _memory = new byte[SizeBytes];
    private readonly Dictionary<uint, long> _readOffsets = [];
    private readonly Dictionary<uint, long> _writeOffsets = [];

    public SaturnBackupRamBusDevice()
    {
        for (var offset = 0; offset < 0x40; offset++)
        {
            _memory[offset] = FormatSignature[offset % FormatSignature.Length];
        }
    }

    public string Name => "Backup RAM / Cartridge Area";
    public long ReadCount { get; private set; }
    public long WriteCount { get; private set; }
    public uint? FirstReadOffset { get; private set; }
    public uint? LastReadOffset { get; private set; }
    public uint? FirstWriteOffset { get; private set; }
    public uint? LastWriteOffset { get; private set; }
    public ReadOnlyMemory<byte> Snapshot => _memory;

    public byte ReadByte(uint offset)
    {
        ReadCount++;
        FirstReadOffset ??= offset;
        LastReadOffset = offset;
        RecordOffset(_readOffsets, offset);

        return (offset & 1) == 0
            ? (byte)0xFF
            : _memory[(offset >> 1) & AddressMask];
    }

    public void WriteByte(uint offset, byte value)
    {
        WriteCount++;
        FirstWriteOffset ??= offset;
        LastWriteOffset = offset;
        RecordOffset(_writeOffsets, offset);

        if ((offset & 1) != 0)
        {
            _memory[(offset >> 1) & AddressMask] = value;
        }
    }

    public IReadOnlyList<(uint Offset, long Count)> GetHotReadOffsets(int count) =>
        GetHotOffsets(_readOffsets, count);

    public IReadOnlyList<(uint Offset, long Count)> GetHotWriteOffsets(int count) =>
        GetHotOffsets(_writeOffsets, count);

    private static void RecordOffset(Dictionary<uint, long> offsets, uint offset)
    {
        offsets.TryGetValue(offset, out var count);
        offsets[offset] = count + 1;
    }

    private static IReadOnlyList<(uint Offset, long Count)> GetHotOffsets(
        Dictionary<uint, long> offsets,
        int count) =>
        offsets
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .Take(count)
            .Select(static pair => (pair.Key, pair.Value))
            .ToArray();
}
