namespace SystemRegisIII.Core.Core.Bus;

public sealed class StubBusDevice(string name, byte readValue = 0) : IBusDevice
{
    private readonly Dictionary<uint, Func<byte>> _readProviders = [];
    private readonly Dictionary<uint, byte> _readOverrides = [];
    private readonly Dictionary<uint, Action<byte>> _writeObservers = [];
    private readonly Dictionary<uint, long> _readOffsets = [];
    private readonly Dictionary<uint, long> _writeOffsets = [];

    public string Name { get; } = name;
    public long ReadCount { get; private set; }
    public long WriteCount { get; private set; }
    public uint? FirstReadOffset { get; private set; }
    public uint? LastReadOffset { get; private set; }
    public uint? FirstWriteOffset { get; private set; }
    public uint? LastWriteOffset { get; private set; }

    public byte ReadByte(uint offset)
    {
        ReadCount++;
        FirstReadOffset ??= offset;
        LastReadOffset = offset;
        RecordOffset(_readOffsets, offset);
        if (_readProviders.TryGetValue(offset, out var provider))
        {
            return provider();
        }

        return _readOverrides.TryGetValue(offset, out var value) ? value : readValue;
    }

    public void WriteByte(uint offset, byte value)
    {
        WriteCount++;
        FirstWriteOffset ??= offset;
        LastWriteOffset = offset;
        RecordOffset(_writeOffsets, offset);
        if (_writeObservers.TryGetValue(offset, out var observer))
        {
            observer(value);
        }
    }

    public IReadOnlyList<(uint Offset, long Count)> GetHotReadOffsets(int count) =>
        GetHotOffsets(_readOffsets, count);

    public IReadOnlyList<(uint Offset, long Count)> GetHotWriteOffsets(int count) =>
        GetHotOffsets(_writeOffsets, count);

    public StubBusDevice AddReadOnlyWord(uint offset, ushort value)
    {
        _readOverrides[offset] = (byte)(value >> 8);
        _readOverrides[offset + 1] = (byte)value;
        return this;
    }

    public StubBusDevice AddReadWordProvider(uint offset, Func<ushort> provider)
    {
        _readProviders[offset] = () => (byte)(provider() >> 8);
        _readProviders[offset + 1] = () => (byte)provider();
        return this;
    }

    public StubBusDevice AddWriteObserver(uint offset, Action<byte> observer)
    {
        _writeObservers[offset] = observer;
        return this;
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
