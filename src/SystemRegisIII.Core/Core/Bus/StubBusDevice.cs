namespace SystemRegisIII.Core.Core.Bus;

public sealed class StubBusDevice(string name, byte readValue = 0) : IBusDevice
{
    public string Name { get; } = name;
    public long ReadCount { get; private set; }
    public long WriteCount { get; private set; }

    public byte ReadByte(uint offset)
    {
        ReadCount++;
        return readValue;
    }

    public void WriteByte(uint offset, byte value)
    {
        WriteCount++;
    }
}
