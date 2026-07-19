using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Core.Core.Cpu.Sh2;

public sealed class Sh2InternalRegisterBus : ISaturnBus, ISh2InstructionBus
{
    private const uint InternalStart = 0xFFFF_8000;
    private const uint InternalEnd = 0xFFFF_FFFF;
    private const uint DivisionRegisterStart = 0xFFFF_FF00;
    private const uint DmaRegisterStart = 0xFFFF_FF80;
    private const uint DmaOperationRegister = 0xFFFF_FFB0;
    private const uint CpuIdRegister = 0xFFFF_FFE0;
    private const uint CacheControlRegister = 0xFFFF_FE92;
    private const int CacheSetCount = 64;
    private const int CacheWayCount = 4;
    private const int CacheLineSize = 16;
    private const byte CacheEnable = 0x01;
    private const byte InstructionReplacementDisable = 0x02;
    private const byte DataReplacementDisable = 0x04;
    private const byte TwoWayMode = 0x08;
    private const byte CachePurge = 0x10;

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
    private readonly DmaChannel[] _dmaChannels = [new(), new()];
    private readonly List<Sh2DmaTransfer> _dmaTransfers = [];
    private readonly CacheSet[] _cache = Enumerable.Range(0, CacheSetCount).Select(static _ => new CacheSet()).ToArray();
    private uint _dmaOperation;
    private byte _cacheControl;

    public Sh2InternalRegisterBus(ISaturnBus externalBus, Sh2CpuRole role)
    {
        _externalBus = externalBus;
        Role = role;
        WriteLocalLong(CpuIdRegister, role == Sh2CpuRole.Slave ? 0x2000_0000u : 0u);
    }

    public Sh2CpuRole Role { get; }
    public long InternalReadCount { get; private set; }
    public long InternalWriteCount { get; private set; }
    public IReadOnlyList<Sh2DmaTransfer> DmaTransfers => _dmaTransfers;
    public byte CacheControl => _cacheControl;
    public long CacheHits { get; private set; }
    public long CacheMisses { get; private set; }

    public ushort ReadInstructionWord(uint address)
    {
        if (!IsCacheable(address) || (_cacheControl & CacheEnable) == 0)
        {
            return ReadWord(address);
        }

        var high = ReadCachedByte(address, isInstruction: true);
        var low = ReadCachedByte(address + 1, isInstruction: true);
        return (ushort)((high << 8) | low);
    }

    public byte ReadByte(uint address)
    {
        if (!IsInternal(address))
        {
            return ReadExternalByte(address, isInstruction: false);
        }

        InternalReadCount++;
        if (IsCacheControlMirror(address))
        {
            return _cacheControl;
        }

        return _registers[address - InternalStart];
    }

    public ushort ReadWord(uint address)
    {
        if (!IsInternal(address) && !IsInternal(address + 1))
        {
            if (!ShouldUseCache(address, 2))
            {
                return _externalBus.ReadWord(address);
            }

            var cachedHigh = ReadCachedByte(address, isInstruction: false);
            var cachedLow = ReadCachedByte(address + 1, isInstruction: false);
            return (ushort)((cachedHigh << 8) | cachedLow);
        }

        var high = ReadByte(address);
        var low = ReadByte(address + 1);
        return (ushort)((high << 8) | low);
    }

    public uint ReadLong(uint address)
    {
        if (!IsInternal(address) && !IsInternal(address + 3))
        {
            if (!ShouldUseCache(address, 4))
            {
                return _externalBus.ReadLong(address);
            }

            var cachedHigh = ReadWord(address);
            var cachedLow = ReadWord(address + 2);
            return ((uint)cachedHigh << 16) | cachedLow;
        }

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

        if (TryGetDmaRegister(address, out var dmaChannel, out var dmaOffset))
        {
            InternalReadCount += 4;
            return ReadDmaRegister(dmaChannel, dmaOffset);
        }

        if (address == DmaOperationRegister)
        {
            InternalReadCount += 4;
            return _dmaOperation;
        }

        var high = ReadWord(address);
        var low = ReadWord(address + 2);
        return ((uint)high << 16) | low;
    }

    public void WriteByte(uint address, byte value)
    {
        if (!IsInternal(address))
        {
            WriteExternalByte(address, value);
            return;
        }

        InternalWriteCount++;
        if (address == CacheControlRegister)
        {
            SetCacheControl(value);
            return;
        }

        _registers[address - InternalStart] = value;
    }

    public void WriteWord(uint address, ushort value)
    {
        if (!IsInternal(address) && !IsInternal(address + 1))
        {
            if (!ShouldUseCache(address, 2))
            {
                _externalBus.WriteWord(address, value);
                return;
            }

            WriteExternalByte(address, (byte)(value >> 8));
            WriteExternalByte(address + 1, (byte)value);
            return;
        }

        WriteByte(address, (byte)(value >> 8));
        WriteByte(address + 1, (byte)value);
    }

    public void WriteLong(uint address, uint value)
    {
        if (!IsInternal(address) && !IsInternal(address + 3))
        {
            if (!ShouldUseCache(address, 4))
            {
                _externalBus.WriteLong(address, value);
                return;
            }

            WriteWord(address, (ushort)(value >> 16));
            WriteWord(address + 2, (ushort)value);
            return;
        }

        if (TryGetDivisionRegisterOffset(address, out var divisionOffset))
        {
            InternalWriteCount += 4;
            WriteDivisionRegister(divisionOffset, value);
            return;
        }

        if (TryGetDmaRegister(address, out var dmaChannel, out var dmaOffset))
        {
            InternalWriteCount += 4;
            WriteDmaRegister(dmaChannel, dmaOffset, value);
            return;
        }

        if (address == DmaOperationRegister)
        {
            InternalWriteCount += 4;
            _dmaOperation = value & 0x0F;
            RunPendingDma();
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

    private uint ReadDmaRegister(int channelIndex, uint offset)
    {
        var channel = _dmaChannels[channelIndex];
        return offset switch
        {
            0x00 => channel.SourceAddress,
            0x04 => channel.DestinationAddress,
            0x08 => channel.TransferCount,
            0x0C => channel.Control,
            _ => 0,
        };
    }

    private void WriteDmaRegister(int channelIndex, uint offset, uint value)
    {
        var channel = _dmaChannels[channelIndex];
        switch (offset)
        {
            case 0x00:
                channel.SourceAddress = value;
                break;
            case 0x04:
                channel.DestinationAddress = value;
                break;
            case 0x08:
                channel.TransferCount = value & 0x00FF_FFFF;
                break;
            case 0x0C:
                // TE is cleared by writing zero and cannot be set directly by software.
                channel.Control = (value & ~2u) | (channel.Control & value & 2u);
                RunPendingDma();
                break;
        }
    }

    private void RunPendingDma()
    {
        if ((_dmaOperation & 0x07) != 1)
        {
            return;
        }

        for (var channelIndex = 0; channelIndex < _dmaChannels.Length; channelIndex++)
        {
            var channel = _dmaChannels[channelIndex];
            if ((channel.Control & 0x03) == 1)
            {
                RunDma(channelIndex, channel);
            }
        }
    }

    private byte ReadExternalByte(uint address, bool isInstruction)
    {
        if (!IsCacheable(address) || (_cacheControl & CacheEnable) == 0)
        {
            return _externalBus.ReadByte(address);
        }

        return ReadCachedByte(address, isInstruction);
    }

    private byte ReadCachedByte(uint address, bool isInstruction)
    {
        var set = _cache[(address >> 4) & (CacheSetCount - 1)];
        var tag = address & 0x1FFF_FC00;
        for (var wayIndex = 0; wayIndex < CacheWayCount; wayIndex++)
        {
            var way = set.Ways[wayIndex];
            if (way.Valid && way.Tag == tag)
            {
                CacheHits++;
                return way.Data[address & (CacheLineSize - 1)];
            }
        }

        CacheMisses++;
        var replacementDisabled = isInstruction
            ? (_cacheControl & InstructionReplacementDisable) != 0
            : (_cacheControl & DataReplacementDisable) != 0;
        if (replacementDisabled)
        {
            return _externalBus.ReadByte(address);
        }

        var activeWayCount = (_cacheControl & TwoWayMode) != 0 ? 2 : CacheWayCount;
        var replacementWay = set.NextReplacementWay % activeWayCount;
        set.NextReplacementWay = (replacementWay + 1) % activeWayCount;
        var replacement = set.Ways[replacementWay];
        replacement.Tag = tag;
        replacement.Valid = true;
        var lineAddress = address & ~(uint)(CacheLineSize - 1);
        for (var byteIndex = 0; byteIndex < CacheLineSize; byteIndex++)
        {
            replacement.Data[byteIndex] = _externalBus.ReadByte(lineAddress + (uint)byteIndex);
        }

        return replacement.Data[address & (CacheLineSize - 1)];
    }

    private void WriteExternalByte(uint address, byte value)
    {
        if (IsCacheable(address) && (_cacheControl & CacheEnable) != 0)
        {
            var set = _cache[(address >> 4) & (CacheSetCount - 1)];
            var tag = address & 0x1FFF_FC00;
            foreach (var way in set.Ways)
            {
                if (way.Valid && way.Tag == tag)
                {
                    way.Data[address & (CacheLineSize - 1)] = value;
                    break;
                }
            }
        }

        _externalBus.WriteByte(address, value);
    }

    private void SetCacheControl(byte value)
    {
        if ((value & CachePurge) != 0)
        {
            foreach (var set in _cache)
            {
                set.NextReplacementWay = 0;
                foreach (var way in set.Ways)
                {
                    way.Valid = false;
                }
            }
        }

        _cacheControl = (byte)(value & ~CachePurge);
        _registers[CacheControlRegister - InternalStart] = _cacheControl;
    }

    private void RunDma(int channelIndex, DmaChannel channel)
    {
        var transferSize = (channel.Control >> 10) & 3;
        var sourceMode = (channel.Control >> 12) & 3;
        var destinationMode = (channel.Control >> 14) & 3;
        var count = channel.TransferCount == 0 ? 0x0100_0000u : channel.TransferCount;
        var source = channel.SourceAddress;
        var destination = channel.DestinationAddress;

        _dmaTransfers.Add(new Sh2DmaTransfer(
            channelIndex,
            source,
            destination,
            count,
            channel.Control));
        if (_dmaTransfers.Count > 256)
        {
            _dmaTransfers.RemoveAt(0);
        }

        while (count > 0)
        {
            switch (transferSize)
            {
                case 0:
                    _externalBus.WriteByte(destination & 0x07FF_FFFF, _externalBus.ReadByte(source & 0x07FF_FFFF));
                    source = AdvanceDmaAddress(source, sourceMode, 1);
                    destination = AdvanceDmaAddress(destination, destinationMode, 1);
                    count--;
                    break;
                case 1:
                    _externalBus.WriteWord(destination & 0x07FF_FFFE, _externalBus.ReadWord(source & 0x07FF_FFFE));
                    source = AdvanceDmaAddress(source, sourceMode, 2);
                    destination = AdvanceDmaAddress(destination, destinationMode, 2);
                    count--;
                    break;
                case 2:
                    _externalBus.WriteLong(destination & 0x07FF_FFFC, _externalBus.ReadLong(source & 0x07FF_FFFC));
                    source = AdvanceDmaAddress(source, sourceMode, 4);
                    destination = AdvanceDmaAddress(destination, destinationMode, 4);
                    count--;
                    break;
                default:
                    // A 16-byte unit is four longwords. SH-2 always advances its source
                    // through the unit; the destination still follows its configured mode.
                    for (var word = 0; word < 4 && count > 0; word++)
                    {
                        var value = _externalBus.ReadLong((source + ((uint)word * 4)) & 0x07FF_FFFC);
                        _externalBus.WriteLong(destination & 0x07FF_FFFC, value);
                        destination = AdvanceDmaAddress(destination, destinationMode, 4);
                        count--;
                    }

                    source += 16;
                    break;
            }
        }

        channel.SourceAddress = source;
        channel.DestinationAddress = destination;
        channel.TransferCount = 0;
        channel.Control |= 2;
    }

    private static uint AdvanceDmaAddress(uint address, uint mode, uint amount) => mode switch
    {
        1 => address + amount,
        2 or 3 => address - amount,
        _ => address,
    };

    private void WriteLocalLong(uint address, uint value)
    {
        _registers[address - InternalStart] = (byte)(value >> 24);
        _registers[address - InternalStart + 1] = (byte)(value >> 16);
        _registers[address - InternalStart + 2] = (byte)(value >> 8);
        _registers[address - InternalStart + 3] = (byte)value;
    }

    private static bool IsInternal(uint address) => address is >= InternalStart and <= InternalEnd;

    private static bool IsCacheable(uint address) => address < 0x2000_0000;

    private bool ShouldUseCache(uint address, uint size) =>
        (_cacheControl & CacheEnable) != 0
        && IsCacheable(address)
        && IsCacheable(address + size - 1);

    private static bool IsCacheControlMirror(uint address) => address is >= CacheControlRegister and <= 0xFFFF_FE9F;

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

    private static bool TryGetDmaRegister(uint address, out int channel, out uint offset)
    {
        if (address is >= DmaRegisterStart and <= DmaRegisterStart + 0x1F && (address & 3) == 0)
        {
            channel = (int)((address - DmaRegisterStart) >> 4);
            offset = (address - DmaRegisterStart) & 0x0F;
            return true;
        }

        channel = 0;
        offset = 0;
        return false;
    }

    private sealed class DmaChannel
    {
        public uint SourceAddress { get; set; }
        public uint DestinationAddress { get; set; }
        public uint TransferCount { get; set; }
        public uint Control { get; set; }
    }

    private sealed class CacheSet
    {
        public CacheWay[] Ways { get; } = Enumerable.Range(0, CacheWayCount).Select(static _ => new CacheWay()).ToArray();
        public int NextReplacementWay { get; set; }
    }

    private sealed class CacheWay
    {
        public uint Tag { get; set; }
        public bool Valid { get; set; }
        public byte[] Data { get; } = new byte[CacheLineSize];
    }
}

public readonly record struct Sh2DmaTransfer(
    int Channel,
    uint SourceAddress,
    uint DestinationAddress,
    uint TransferCount,
    uint Control);
