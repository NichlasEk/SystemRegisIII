namespace SystemRegisIII.Core.Core.Bus;

public sealed class SaturnAddressMapBuilder
{
    private readonly List<BusRegion> _regions = [];

    public SaturnAddressMapBuilder Map(uint start, uint endInclusive, IBusDevice device)
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
        return this;
    }

    public PageMappedBus Build() => new(_regions);
}
