namespace SystemRegisIII.Core.Core.Bus;

public sealed class PageMappedBus : ISaturnBus, IAddressMap
{
    public const int PageBits = 12;
    public const uint PageSize = 1u << PageBits;
    public const uint PageMask = PageSize - 1;

    private const int PageCount = 1 << (32 - PageBits);

    private readonly List<BusRegion>?[] _pages = new List<BusRegion>?[PageCount];

    internal PageMappedBus(IReadOnlyList<BusRegion> regions)
    {
        Regions = regions
            .OrderBy(static region => region.Start)
            .ToArray();

        foreach (var region in Regions)
        {
            var firstPage = region.Start >> PageBits;
            var lastPage = region.EndInclusive >> PageBits;

            for (var page = firstPage; page <= lastPage; page++)
            {
                _pages[page] ??= [];
                _pages[page]!.Add(region);
            }
        }
    }

    public IReadOnlyList<BusRegion> Regions { get; }

    public bool TryResolve(uint address, out BusRegion region, out uint offset)
    {
        var physicalAddress = Normalize(address);
        var page = physicalAddress >> PageBits;
        var candidates = _pages[page];

        if (candidates is not null)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Contains(physicalAddress))
                {
                    region = candidate;
                    offset = physicalAddress - candidate.Start;
                    return true;
                }
            }
        }

        region = null!;
        offset = 0;
        return false;
    }

    public byte ReadByte(uint address)
    {
        var (region, offset) = Resolve(address);
        return region.Device.ReadByte(offset);
    }

    public ushort ReadWord(uint address)
    {
        var (region, offset) = Resolve(address);
        if (offset < region.EndInclusive - region.Start)
        {
            var regionHigh = region.Device.ReadByte(offset);
            var regionLow = region.Device.ReadByte(offset + 1);
            return (ushort)((regionHigh << 8) | regionLow);
        }

        var high = region.Device.ReadByte(offset);
        var low = ReadByte(address + 1);
        return (ushort)((high << 8) | low);
    }

    public uint ReadLong(uint address)
    {
        var (region, offset) = Resolve(address);
        var lastOffset = region.EndInclusive - region.Start;
        if (lastOffset >= 3 && offset <= lastOffset - 3)
        {
            return ((uint)region.Device.ReadByte(offset) << 24)
                | ((uint)region.Device.ReadByte(offset + 1) << 16)
                | ((uint)region.Device.ReadByte(offset + 2) << 8)
                | region.Device.ReadByte(offset + 3);
        }

        var high = ReadWord(address);
        var low = ReadWord(address + 2);
        return ((uint)high << 16) | low;
    }

    public void WriteByte(uint address, byte value)
    {
        var (region, offset) = Resolve(address);
        region.Device.WriteByte(offset, value);
    }

    public void WriteWord(uint address, ushort value)
    {
        var (region, offset) = Resolve(address);
        if (offset < region.EndInclusive - region.Start)
        {
            region.Device.WriteByte(offset, (byte)(value >> 8));
            region.Device.WriteByte(offset + 1, (byte)value);
            return;
        }

        region.Device.WriteByte(offset, (byte)(value >> 8));
        WriteByte(address + 1, (byte)value);
    }

    public void WriteLong(uint address, uint value)
    {
        var (region, offset) = Resolve(address);
        var lastOffset = region.EndInclusive - region.Start;
        if (lastOffset >= 3 && offset <= lastOffset - 3)
        {
            region.Device.WriteByte(offset, (byte)(value >> 24));
            region.Device.WriteByte(offset + 1, (byte)(value >> 16));
            region.Device.WriteByte(offset + 2, (byte)(value >> 8));
            region.Device.WriteByte(offset + 3, (byte)value);
            return;
        }

        WriteWord(address, (ushort)(value >> 16));
        WriteWord(address + 2, (ushort)value);
    }

    private (BusRegion Region, uint Offset) Resolve(uint address)
    {
        if (TryResolve(address, out var region, out var offset))
        {
            return (region, offset);
        }

        throw new BusFaultException(address);
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

        if (address is >= 0x8000_0000 and <= 0x9FFF_FFFF)
        {
            return address - 0x8000_0000;
        }

        return address;
    }
}
