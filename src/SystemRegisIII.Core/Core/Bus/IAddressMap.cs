namespace SystemRegisIII.Core.Core.Bus;

public interface IAddressMap
{
    IReadOnlyList<BusRegion> Regions { get; }

    bool TryResolve(uint address, out BusRegion region, out uint offset);
}
