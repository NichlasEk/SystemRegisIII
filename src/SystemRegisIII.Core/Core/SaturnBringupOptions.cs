namespace SystemRegisIII.Core.Core;

using SystemRegisIII.Core.Core.CdBlock;

public sealed class SaturnBringupOptions
{
    public bool SimulateSlaveReady { get; init; }
    public IDiscImage? DiscImage { get; init; }
}
