using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Cli;

internal sealed class WatchedBus(ISaturnBus inner, uint startAddress, uint endAddressInclusive) : ISaturnBus
{
    private readonly Dictionary<uint, long> _writes = [];
    private readonly Dictionary<uint, uint> _lastValues = [];

    public long WriteCount { get; private set; }
    public uint? FirstWriteAddress { get; private set; }
    public uint? LastWriteAddress { get; private set; }
    public uint? LastWriteValue { get; private set; }

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

    public IReadOnlyList<(uint Address, long Count, uint LastValue)> GetHotWrites(int count) =>
        _writes
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .Take(count)
            .Select(pair => (pair.Key, pair.Value, _lastValues[pair.Key]))
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
