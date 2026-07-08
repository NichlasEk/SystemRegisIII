using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Cli;

internal sealed class WatchedBus(
    ISaturnBus inner,
    uint startAddress,
    uint endAddressInclusive,
    Func<uint?>? programCounterProvider = null) : ISaturnBus
{
    private readonly Dictionary<uint, long> _writes = [];
    private readonly Dictionary<uint, uint> _lastValues = [];
    private readonly Dictionary<uint, uint?> _lastProgramCounters = [];
    private readonly Queue<WatchedWrite> _recentWrites = new();

    public long WriteCount { get; private set; }
    public uint? FirstWriteAddress { get; private set; }
    public uint? LastWriteAddress { get; private set; }
    public uint? LastWriteValue { get; private set; }
    public IReadOnlyList<WatchedWrite> RecentWrites => _recentWrites.ToArray();

    public byte ReadByte(uint address) => inner.ReadByte(address);

    public ushort ReadWord(uint address) => inner.ReadWord(address);

    public uint ReadLong(uint address) => inner.ReadLong(address);

    public void WriteByte(uint address, byte value)
    {
        RecordWrite(address, value);
        inner.WriteByte(address, value);
    }

    public void WriteWord(uint address, ushort value)
    {
        RecordWrite(address, value);
        inner.WriteWord(address, value);
    }

    public void WriteLong(uint address, uint value)
    {
        RecordWrite(address, value);
        inner.WriteLong(address, value);
    }

    public IReadOnlyList<(uint Address, long Count, uint LastValue, uint? LastProgramCounter)> GetHotWrites(int count) =>
        _writes
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .Take(count)
            .Select(pair => (pair.Key, pair.Value, _lastValues[pair.Key], _lastProgramCounters[pair.Key]))
            .ToArray();

    private void RecordWrite(uint address, uint value)
    {
        var normalized = Normalize(address);
        if (normalized < startAddress || normalized > endAddressInclusive)
        {
            return;
        }

        WriteCount++;
        FirstWriteAddress ??= normalized;
        LastWriteAddress = normalized;
        LastWriteValue = value;
        _writes.TryGetValue(normalized, out var count);
        _writes[normalized] = count + 1;
        _lastValues[normalized] = value;
        var programCounter = programCounterProvider?.Invoke();
        _lastProgramCounters[normalized] = programCounter;

        _recentWrites.Enqueue(new WatchedWrite(programCounter, normalized, value));
        while (_recentWrites.Count > 24)
        {
            _recentWrites.Dequeue();
        }
    }

    private static uint Normalize(uint address)
    {
        if (address is >= 0x2000_0000 and <= 0x3FFF_FFFF)
        {
            return address - 0x2000_0000;
        }

        if (address is >= 0x4000_0000 and <= 0x5FFF_FFFF)
        {
            return address - 0x4000_0000;
        }

        return address;
    }
}

internal readonly record struct WatchedWrite(uint? ProgramCounter, uint Address, uint Value);
