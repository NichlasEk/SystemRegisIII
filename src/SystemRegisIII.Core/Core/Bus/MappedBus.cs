namespace SystemRegisIII.Core.Core.Bus;

public sealed class MappedBus : ISaturnBus
{
    private readonly List<BusRegion> _regions = [];

    public event EventHandler<BusAccessEventArgs>? Accessed;

    public IReadOnlyList<BusRegion> Regions => _regions;

    public void Map(uint start, uint endInclusive, IBusDevice device)
    {
        var region = new BusRegion(start, endInclusive, device);

        foreach (var existing in _regions)
        {
            var overlaps = start <= existing.EndInclusive && endInclusive >= existing.Start;
            if (overlaps)
            {
                throw new InvalidOperationException(
                    $"Bus region 0x{start:X8}-0x{endInclusive:X8} overlaps {existing.Device.Name}.");
            }
        }

        _regions.Add(region);
        _regions.Sort(static (left, right) => left.Start.CompareTo(right.Start));
    }

    public byte ReadByte(uint address)
    {
        var (region, offset) = Resolve(address);
        var value = region.Device.ReadByte(offset);
        Publish(address, 1, value, isWrite: false, region.Device.Name);
        return value;
    }

    public ushort ReadWord(uint address)
    {
        var high = ReadByte(address);
        var low = ReadByte(address + 1);
        return (ushort)((high << 8) | low);
    }

    public uint ReadLong(uint address)
    {
        var high = ReadWord(address);
        var low = ReadWord(address + 2);
        return ((uint)high << 16) | low;
    }

    public void WriteByte(uint address, byte value)
    {
        var (region, offset) = Resolve(address);
        region.Device.WriteByte(offset, value);
        Publish(address, 1, value, isWrite: true, region.Device.Name);
    }

    public void WriteWord(uint address, ushort value)
    {
        WriteByte(address, (byte)(value >> 8));
        WriteByte(address + 1, (byte)value);
    }

    public void WriteLong(uint address, uint value)
    {
        WriteWord(address, (ushort)(value >> 16));
        WriteWord(address + 2, (ushort)value);
    }

    private (BusRegion Region, uint Offset) Resolve(uint address)
    {
        var physicalAddress = Normalize(address);

        foreach (var region in _regions)
        {
            if (region.Contains(physicalAddress))
            {
                return (region, physicalAddress - region.Start);
            }
        }

        throw new BusFaultException(address);
    }

    private static uint Normalize(uint address)
    {
        if (address is >= 0x2000_0000 and <= 0x3FFF_FFFF)
        {
            return address - 0x2000_0000;
        }

        return address;
    }

    private void Publish(uint address, int sizeBytes, uint value, bool isWrite, string deviceName)
    {
        Accessed?.Invoke(this, new BusAccessEventArgs(new BusAccess(address, sizeBytes, value, isWrite, deviceName)));
    }
}
