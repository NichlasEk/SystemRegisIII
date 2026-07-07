namespace SystemRegisIII.Core.Core.Bus;

public sealed class StubBusDevice(string name, byte readValue = 0) : IBusDevice
{
    public string Name { get; } = name;

    public byte ReadByte(uint offset) => readValue;

    public void WriteByte(uint offset, byte value)
    {
    }
}
