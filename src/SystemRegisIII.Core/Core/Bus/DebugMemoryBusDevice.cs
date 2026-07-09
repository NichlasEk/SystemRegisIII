namespace SystemRegisIII.Core.Core.Bus;

public sealed class DebugMemoryBusDevice(string name, int sizeBytes, byte readValue = 0) : IInspectableBusDevice
{
    private readonly byte[] _memory = new byte[ValidateSize(sizeBytes)];
    private readonly Dictionary<uint, long> _readOffsets = [];
    private readonly Dictionary<uint, long> _writeOffsets = [];
    private uint _firstWriteOffset = uint.MaxValue;

    public string Name { get; } = name;
    public int SizeBytes => _memory.Length;
    public ReadOnlyMemory<byte> Snapshot => _memory;
    public long ReadCount { get; private set; }
    public long WriteCount { get; private set; }
    public uint? FirstReadOffset { get; private set; }
    public uint? LastReadOffset { get; private set; }
    public uint? FirstWriteOffset => WriteCount == 0 ? null : _firstWriteOffset;
    public uint? LastWriteOffset { get; private set; }

    public byte ReadByte(uint offset)
    {
        ReadCount++;
        FirstReadOffset ??= offset;
        LastReadOffset = offset;
        RecordOffset(_readOffsets, offset);
        return _memory.Length == 0 ? readValue : _memory[Wrap(offset)];
    }

    public void WriteByte(uint offset, byte value)
    {
        WriteCount++;
        _firstWriteOffset = Math.Min(_firstWriteOffset, offset);
        LastWriteOffset = offset;
        RecordOffset(_writeOffsets, offset);
        _memory[Wrap(offset)] = value;
    }

    public ushort ReadBigEndianWord(uint offset)
    {
        byte high = _memory[Wrap(offset)];
        byte low = _memory[Wrap(offset + 1)];
        return (ushort)((high << 8) | low);
    }

    public void Clear()
    {
        Array.Clear(_memory);
        ReadCount = 0;
        WriteCount = 0;
        FirstReadOffset = null;
        LastReadOffset = null;
        _firstWriteOffset = uint.MaxValue;
        LastWriteOffset = null;
        _readOffsets.Clear();
        _writeOffsets.Clear();
    }

    public IReadOnlyList<(uint Offset, long Count)> GetHotReadOffsets(int count) =>
        GetHotOffsets(_readOffsets, count);

    public IReadOnlyList<(uint Offset, long Count)> GetHotWriteOffsets(int count) =>
        GetHotOffsets(_writeOffsets, count);

    private int Wrap(uint offset) => (int)(offset % (uint)_memory.Length);

    private static void RecordOffset(Dictionary<uint, long> offsets, uint offset)
    {
        offsets.TryGetValue(offset, out long count);
        offsets[offset] = count + 1;
    }

    private static IReadOnlyList<(uint Offset, long Count)> GetHotOffsets(Dictionary<uint, long> offsets, int count) =>
        offsets
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .Take(count)
            .Select(static pair => (pair.Key, pair.Value))
            .ToArray();

    private static int ValidateSize(int sizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        return sizeBytes;
    }
}
