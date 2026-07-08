using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Cli;

internal sealed class WatchedBus(
    ISaturnBus inner,
    uint startAddress,
    uint endAddressInclusive,
    Func<WatchedAccessContext?>? contextProvider = null) : ISaturnBus
{
    private readonly Dictionary<uint, long> _reads = [];
    private readonly Dictionary<uint, uint> _lastReadValues = [];
    private readonly Dictionary<uint, WatchedAccessContext?> _lastReadContexts = [];
    private readonly Dictionary<uint, long> _writes = [];
    private readonly Dictionary<uint, uint> _lastValues = [];
    private readonly Dictionary<uint, WatchedAccessContext?> _lastWriteContexts = [];
    private readonly Queue<WatchedAccess> _recentReads = new();
    private readonly Queue<WatchedWrite> _recentWrites = new();

    public long ReadCount { get; private set; }
    public uint? FirstReadAddress { get; private set; }
    public uint? LastReadAddress { get; private set; }
    public uint? LastReadValue { get; private set; }
    public long WriteCount { get; private set; }
    public uint? FirstWriteAddress { get; private set; }
    public uint? LastWriteAddress { get; private set; }
    public uint? LastWriteValue { get; private set; }
    public IReadOnlyList<WatchedAccess> RecentReads => _recentReads.ToArray();
    public IReadOnlyList<WatchedWrite> RecentWrites => _recentWrites.ToArray();

    public byte ReadByte(uint address)
    {
        var value = inner.ReadByte(address);
        RecordRead(address, value);
        return value;
    }

    public ushort ReadWord(uint address)
    {
        var value = inner.ReadWord(address);
        RecordRead(address, value);
        return value;
    }

    public uint ReadLong(uint address)
    {
        var value = inner.ReadLong(address);
        RecordRead(address, value);
        return value;
    }

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

    public IReadOnlyList<(uint Address, long Count, uint LastValue, WatchedAccessContext? LastContext)> GetHotReads(int count) =>
        _reads
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .Take(count)
            .Select(pair => (pair.Key, pair.Value, _lastReadValues[pair.Key], _lastReadContexts[pair.Key]))
            .ToArray();

    public IReadOnlyList<(uint Address, long Count, uint LastValue, WatchedAccessContext? LastContext)> GetHotWrites(int count) =>
        _writes
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .Take(count)
            .Select(pair => (pair.Key, pair.Value, _lastValues[pair.Key], _lastWriteContexts[pair.Key]))
            .ToArray();

    private void RecordRead(uint address, uint value)
    {
        var normalized = Normalize(address);
        if (normalized < startAddress || normalized > endAddressInclusive)
        {
            return;
        }

        ReadCount++;
        FirstReadAddress ??= normalized;
        LastReadAddress = normalized;
        LastReadValue = value;
        _reads.TryGetValue(normalized, out var count);
        _reads[normalized] = count + 1;
        _lastReadValues[normalized] = value;
        var context = contextProvider?.Invoke();
        _lastReadContexts[normalized] = context;

        _recentReads.Enqueue(new WatchedAccess(context, normalized, value));
        while (_recentReads.Count > 24)
        {
            _recentReads.Dequeue();
        }
    }

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
        var context = contextProvider?.Invoke();
        _lastWriteContexts[normalized] = context;

        _recentWrites.Enqueue(new WatchedWrite(context, normalized, value));
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

internal readonly record struct WatchedAccessContext(
    uint? ProgramCounter,
    uint ProcedureRegister,
    uint GlobalBaseRegister,
    uint R0);

internal readonly record struct WatchedAccess(WatchedAccessContext? Context, uint Address, uint Value);

internal readonly record struct WatchedWrite(WatchedAccessContext? Context, uint Address, uint Value);
