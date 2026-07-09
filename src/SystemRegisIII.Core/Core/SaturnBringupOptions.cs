namespace SystemRegisIII.Core.Core;

using SystemRegisIII.Core.Core.CdBlock;
using SystemRegisIII.Core.Host.Input;

public sealed class SaturnBringupOptions
{
    public bool SimulateSlaveReady { get; init; }
    public bool SimulateScspCommandAck { get; init; }
    public IDiscImage? DiscImage { get; init; }
    public CdBlockDriveStatus? MountedDiscInitialStatus { get; init; }
    public SaturnInputState DigitalPadState { get; init; }
    public IReadOnlyList<byte>? DigitalPadPeripheralData { get; init; }
}
