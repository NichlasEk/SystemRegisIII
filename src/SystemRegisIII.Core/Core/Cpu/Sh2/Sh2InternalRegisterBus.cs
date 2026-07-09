using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Core.Core.Cpu.Sh2;

public sealed class Sh2InternalRegisterBus : ISaturnBus
{
    private const uint InternalStart = 0xFFFF_8000;
    private const uint InternalEnd = 0xFFFF_FFFF;
    private const uint DivisionRegisterStart = 0xFFFF_FF00;
    private const uint CpuIdRegister = 0xFFFF_FFE0;

    private readonly ISaturnBus _externalBus;
    private readonly byte[] _registers = new byte[InternalEnd - InternalStart + 1];
    private uint _divisor;
    private uint _dividend;
    private uint _divisionControl;
    private uint _divisionVector;
    private uint _dividendHigh;
    private uint _dividendLow;
    private uint _dividendHighShadow;
    private uint _dividendLowShadow;

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
        if (TryGetDivisionRegisterOffset(address, out var divisionOffset))
        {
            InternalReadCount += 4;
            return divisionOffset switch
            {
                0x00 => _divisor,
                0x04 => _dividend,
                0x08 => _divisionControl,
                0x0C => _divisionVector,
                0x10 => _dividendHigh,
                0x14 => _dividendLow,
                0x18 => _dividendHighShadow,
                0x1C => _dividendLowShadow,
                _ => 0,
            };
        }

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
        if (TryGetDivisionRegisterOffset(address, out var divisionOffset))
        {
            InternalWriteCount += 4;
            WriteDivisionRegister(divisionOffset, value);
            return;
        }

        WriteWord(address, (ushort)(value >> 16));
        WriteWord(address + 2, (ushort)value);
    }

    private void WriteDivisionRegister(uint offset, uint value)
    {
        switch (offset)
        {
            case 0x00:
                _divisor = value;
                break;
            case 0x04:
                _dividend = value;
                _dividendHigh = unchecked((uint)((int)value >> 31));
                _dividendLow = value;
                DivideSigned64By32();
                break;
            case 0x08:
                _divisionControl = value & 0x3;
                break;
            case 0x0C:
                _divisionVector = value;
                break;
            case 0x10:
                _dividendHigh = value;
                break;
            case 0x14:
                _dividendLow = value;
                DivideSigned64By32();
                break;
            case 0x18:
                _dividendHighShadow = value;
                break;
            case 0x1C:
                _dividendLowShadow = value;
                break;
        }
    }

    private void DivideSigned64By32()
    {
        var divisor = (int)_divisor;
        var dividend = ((long)(int)_dividendHigh << 32) | _dividendLow;

        if (divisor == 0 || (dividend == long.MinValue && divisor == -1))
        {
            SetDivisionOverflow(dividend, divisor);
            return;
        }

        var quotient = dividend / divisor;
        if (quotient < int.MinValue || quotient > int.MaxValue)
        {
            SetDivisionOverflow(dividend, divisor);
            return;
        }

        _dividendHigh = unchecked((uint)(int)(dividend % divisor));
        _dividend = _dividendLow = unchecked((uint)(int)quotient);
        _dividendHighShadow = _dividendHigh;
        _dividendLowShadow = _dividendLow;
    }

    private void SetDivisionOverflow(long dividend, int divisor)
    {
        _divisionControl |= 1;
        var negative = divisor == 0
            ? dividend < 0
            : (dividend < 0) != (divisor < 0);
        _dividend = _dividendLow = negative ? 0x8000_0000u : 0x7FFF_FFFFu;
        _dividendHighShadow = _dividendHigh;
        _dividendLowShadow = _dividendLow;
    }

    private void WriteLocalLong(uint address, uint value)
    {
        _registers[address - InternalStart] = (byte)(value >> 24);
        _registers[address - InternalStart + 1] = (byte)(value >> 16);
        _registers[address - InternalStart + 2] = (byte)(value >> 8);
        _registers[address - InternalStart + 3] = (byte)value;
    }

    private static bool IsInternal(uint address) => address is >= InternalStart and <= InternalEnd;

    private static bool TryGetDivisionRegisterOffset(uint address, out uint offset)
    {
        if (address is >= DivisionRegisterStart and <= DivisionRegisterStart + 0x3F
            && (address & 3) == 0)
        {
            offset = (address - DivisionRegisterStart) & 0x1F;
            return true;
        }

        offset = 0;
        return false;
    }
}
