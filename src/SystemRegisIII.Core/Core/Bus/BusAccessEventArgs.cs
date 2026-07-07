namespace SystemRegisIII.Core.Core.Bus;

public sealed class BusAccessEventArgs(BusAccess access) : EventArgs
{
    public BusAccess Access { get; } = access;
}
