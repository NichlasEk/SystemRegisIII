namespace SystemRegisIII.Core.Core.Bus;

public sealed class BusRegion
{
    public BusRegion(uint start, uint endInclusive, IBusDevice device)
    {
        if (endInclusive < start)
        {
            throw new ArgumentOutOfRangeException(nameof(endInclusive), "Region end must be after start.");
        }

        Start = start;
        EndInclusive = endInclusive;
        Device = device;
    }

    public uint Start { get; }
    public uint EndInclusive { get; }
    public IBusDevice Device { get; }

    public bool Contains(uint address) => address >= Start && address <= EndInclusive;
}
