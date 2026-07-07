namespace SystemRegisIII.Core.Core.Bus;

public interface ISaturnBus
{
    byte ReadByte(uint address);

    ushort ReadWord(uint address);

    uint ReadLong(uint address);

    void WriteByte(uint address, byte value);

    void WriteWord(uint address, ushort value);

    void WriteLong(uint address, uint value);
}
