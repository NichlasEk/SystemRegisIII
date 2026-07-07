namespace SystemRegisIII.Core.Core.Bus;

public interface IAddressMap
{
    IReadOnlyList<BusRegion> Regions { get; }
}
