namespace SystemRegisIII.Core.Core.Bus;

public interface IBusDevice
{
    string Name { get; }

    byte ReadByte(uint offset);

    void WriteByte(uint offset, byte value);
}
