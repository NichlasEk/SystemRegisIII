namespace SystemRegisIII.Core.Core;

public readonly record struct SaturnCycleBudget(long MasterCycles, long SlaveCycles)
{
    public SaturnCycleBudget(long masterCycles)
        : this(masterCycles, masterCycles)
    {
    }

    public bool HasCycles => MasterCycles > 0 || SlaveCycles > 0;

    public SaturnCycleBudget TakeSlice(long maxCycles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCycles);
        return new SaturnCycleBudget(
            Math.Min(MasterCycles, maxCycles),
            Math.Min(SlaveCycles, maxCycles));
    }

    public SaturnCycleBudget Subtract(SaturnCycleBudget slice) =>
        new(
            Math.Max(0, MasterCycles - slice.MasterCycles),
            Math.Max(0, SlaveCycles - slice.SlaveCycles));
}
