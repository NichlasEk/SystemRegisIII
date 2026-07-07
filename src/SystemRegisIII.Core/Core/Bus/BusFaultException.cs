namespace SystemRegisIII.Core.Core.Bus;

public sealed class BusFaultException(uint address)
    : InvalidOperationException($"No Saturn bus device is mapped at 0x{address:X8}.")
{
    public uint Address { get; } = address;
}
