using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Core.Core.Cpu.Sh2;

public sealed class Sh2InternalRegisterBus : ISaturnBus
{
    private const uint InternalStart = 0xFFFF_8000;
    private const uint InternalEnd = 0xFFFF_FFFF;
    private const uint CpuIdRegister = 0xFFFF_FFE0;

    private readonly ISaturnBus _externalBus;
    private readonly byte[] _registers = new byte[InternalEnd - InternalStart + 1];

    public Sh2InternalRegisterBus(ISaturnBus externalBus, Sh2CpuRole role)
    {
        _externalBus = externalBus;
        Role = role;
        WriteLocalLong(CpuIdRegister, role == Sh2CpuRole.Slave ? 0x2000_0000u : 0u);
    }

    public Sh2CpuRole Role { get; }
    public long InternalReadCount { get; private set; }
    public long InternalWriteCount { get; private set; }

    public byte ReadByte(uint address)
    {
        if (!IsInternal(address))
        {
            return _externalBus.ReadByte(address);
        }

        InternalReadCount++;
        return _registers[address - InternalStart];
    }

    public ushort ReadWord(uint address)
    {
        var high = ReadByte(address);
        var low = ReadByte(address + 1);
        return (ushort)((high << 8) | low);
    }

    public uint ReadLong(uint address)
    {
        var high = ReadWord(address);
        var low = ReadWord(address + 2);
        return ((uint)high << 16) | low;
    }

    public void WriteByte(uint address, byte value)
    {
        if (!IsInternal(address))
        {
            _externalBus.WriteByte(address, value);
            return;
        }

        InternalWriteCount++;
        _registers[address - InternalStart] = value;
    }

    public void WriteWord(uint address, ushort value)
    {
        WriteByte(address, (byte)(value >> 8));
        WriteByte(address + 1, (byte)value);
    }

    public void WriteLong(uint address, uint value)
    {
        WriteWord(address, (ushort)(value >> 16));
        WriteWord(address + 2, (ushort)value);
    }

    private void WriteLocalLong(uint address, uint value)
    {
        _registers[address - InternalStart] = (byte)(value >> 24);
        _registers[address - InternalStart + 1] = (byte)(value >> 16);
        _registers[address - InternalStart + 2] = (byte)(value >> 8);
        _registers[address - InternalStart + 3] = (byte)value;
    }

    private static bool IsInternal(uint address) => address is >= InternalStart and <= InternalEnd;
}
