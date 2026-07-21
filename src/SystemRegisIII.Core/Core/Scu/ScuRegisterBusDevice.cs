using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Core.Core.Scu;

public sealed class ScuRegisterBusDevice : IInspectableBusDevice
{
    private const uint InterruptMaskOffset = 0x00A0;
    private const uint InterruptStatusOffset = 0x00A4;
    private const uint DspProgramControlOffset = 0x0080;
    private const uint DspExecuteBit = 0x0001_0000;
    private const int DspExecutionPollCount = 16;
    private const uint DmaRegisterStride = 0x20;
    private const uint DmaReadAddressOffset = 0x00;
    private const uint DmaWriteAddressOffset = 0x04;
    private const uint DmaByteCountOffset = 0x08;
    private const uint DmaAddressAddOffset = 0x0C;
    private const uint DmaEnableOffset = 0x10;
    private const uint DmaModeOffset = 0x14;
    private const uint DmaEnableBit = 1u << 8;
    private const uint DmaManualStartBit = 1u << 0;
    private const uint DmaIndirectBit = 1u << 24;
    private const uint DmaReadUpdateBit = 1u << 16;
    private const uint DmaWriteUpdateBit = 1u << 8;
    private const uint VBlankInBit = 1u << 0;
    private const uint VBlankOutBit = 1u << 1;
    private const uint SmpcBit = 1u << 7;
    private const uint Dma2EndBit = 1u << 9;
    private const uint Dma1EndBit = 1u << 10;
    private const uint Dma0EndBit = 1u << 11;
    private readonly Dictionary<uint, byte> _registers = [];
    private readonly Dictionary<uint, long> _readOffsets = [];
    private readonly Dictionary<uint, long> _writeOffsets = [];
    private int _dspExecutionPollsRemaining;
    private ISaturnBus? _dmaBus;

    public string Name => "SCU / System Control Area";
    public long ReadCount { get; private set; }
    public long WriteCount { get; private set; }
    public uint? FirstReadOffset { get; private set; }
    public uint? LastReadOffset { get; private set; }
    public uint? FirstWriteOffset { get; private set; }
    public uint? LastWriteOffset { get; private set; }
    public uint InterruptMask { get; private set; } = 0xFFFF_FFFF;
    public uint InterruptStatus { get; private set; }
    public uint LastInterruptStatusWrite { get; private set; } = 0xFFFF_FFFF;
    public bool HasPendingVBlankIn => (InterruptStatus & VBlankInBit) != 0 && (InterruptMask & VBlankInBit) == 0;
    public bool HasPendingVBlankOut => (InterruptStatus & VBlankOutBit) != 0 && (InterruptMask & VBlankOutBit) == 0;
    public bool HasPendingSmpc => (InterruptStatus & SmpcBit) != 0 && (InterruptMask & SmpcBit) == 0;
    public bool HasPendingDma2End => (InterruptStatus & Dma2EndBit) != 0 && (InterruptMask & Dma2EndBit) == 0;
    public bool HasPendingDma1End => (InterruptStatus & Dma1EndBit) != 0 && (InterruptMask & Dma1EndBit) == 0;
    public bool HasPendingDma0End => (InterruptStatus & Dma0EndBit) != 0 && (InterruptMask & Dma0EndBit) == 0;
    public long CompletedDmaCount { get; private set; }
    public ScuDmaTransfer? LastDmaTransfer { get; private set; }

    public void ConnectDmaBus(ISaturnBus bus) => _dmaBus = bus;

    public byte ReadByte(uint offset)
    {
        ReadCount++;
        FirstReadOffset ??= offset;
        LastReadOffset = offset;
        RecordOffset(_readOffsets, offset);

        if (IsInterruptMask(offset))
        {
            return ReadWordByte(InterruptMask, offset - InterruptMaskOffset);
        }

        if (IsInterruptStatus(offset))
        {
            return ReadWordByte(InterruptStatus, offset - InterruptStatusOffset);
        }

        var registerValue = _registers.TryGetValue(offset, out var value) ? value : (byte)0;
        if (offset == DspProgramControlOffset + 3 && _dspExecutionPollsRemaining > 0)
        {
            _dspExecutionPollsRemaining--;
            if (_dspExecutionPollsRemaining == 0)
            {
                WriteRegisterLong(DspProgramControlOffset, ReadRegisterLong(DspProgramControlOffset) & ~DspExecuteBit);
            }
        }

        return registerValue;
    }

    public void WriteByte(uint offset, byte value)
    {
        WriteCount++;
        FirstWriteOffset ??= offset;
        LastWriteOffset = offset;
        RecordOffset(_writeOffsets, offset);

        if (IsInterruptMask(offset))
        {
            InterruptMask = WriteWordByte(InterruptMask, offset - InterruptMaskOffset, value);
            return;
        }

        if (IsInterruptStatus(offset))
        {
            var written = WriteWordByte(0xFFFF_FFFF, offset - InterruptStatusOffset, value);
            LastInterruptStatusWrite = WriteWordByte(LastInterruptStatusWrite, offset - InterruptStatusOffset, value);
            InterruptStatus &= written;
            return;
        }

        _registers[offset] = value;
        if (TryGetDmaLevel(offset, out var dmaLevel)
            && offset == (uint)(dmaLevel * DmaRegisterStride) + DmaEnableOffset + 3)
        {
            TryStartDma(dmaLevel);
        }
        if (offset == DspProgramControlOffset + 3
            && (ReadRegisterLong(DspProgramControlOffset) & DspExecuteBit) != 0)
        {
            _dspExecutionPollsRemaining = DspExecutionPollCount;
        }
    }

    public void RaiseVBlankIn() => InterruptStatus |= VBlankInBit;

    public void RaiseVBlankOut() => InterruptStatus |= VBlankOutBit;

    public void RaiseSmpc() => InterruptStatus |= SmpcBit;

    public void AcknowledgeVBlankIn() => InterruptStatus &= ~VBlankInBit;

    public void AcknowledgeVBlankOut() => InterruptStatus &= ~VBlankOutBit;

    public void AcknowledgeSmpc() => InterruptStatus &= ~SmpcBit;

    public void AcknowledgeDma2End() => InterruptStatus &= ~Dma2EndBit;

    public void AcknowledgeDma1End() => InterruptStatus &= ~Dma1EndBit;

    public void AcknowledgeDma0End() => InterruptStatus &= ~Dma0EndBit;

    public IReadOnlyList<(uint Offset, long Count)> GetHotReadOffsets(int count) =>
        GetHotOffsets(_readOffsets, count);

    public IReadOnlyList<(uint Offset, long Count)> GetHotWriteOffsets(int count) =>
        GetHotOffsets(_writeOffsets, count);

    private static bool IsInterruptMask(uint offset) => offset is >= InterruptMaskOffset and < InterruptMaskOffset + 4;

    private static bool IsInterruptStatus(uint offset) => offset is >= InterruptStatusOffset and < InterruptStatusOffset + 4;

    private static bool TryGetDmaLevel(uint offset, out int level)
    {
        level = (int)(offset / DmaRegisterStride);
        return level is >= 0 and < 3 && offset < 0x58;
    }

    private void TryStartDma(int level)
    {
        var baseOffset = (uint)level * DmaRegisterStride;
        var enable = ReadRegisterLong(baseOffset + DmaEnableOffset);
        var mode = ReadRegisterLong(baseOffset + DmaModeOffset);
        if ((enable & (DmaEnableBit | DmaManualStartBit)) != (DmaEnableBit | DmaManualStartBit)
            || (mode & DmaIndirectBit) != 0
            || _dmaBus is null)
        {
            return;
        }

        var readAddress = ReadRegisterLong(baseOffset + DmaReadAddressOffset) & 0x07FF_FFFF;
        var writeAddress = ReadRegisterLong(baseOffset + DmaWriteAddressOffset) & 0x07FF_FFFF;
        var byteCountMask = level == 0 ? 0x000F_FFFFu : 0x0000_0FFFu;
        var byteCount = ReadRegisterLong(baseOffset + DmaByteCountOffset) & byteCountMask;
        if (byteCount == 0)
        {
            byteCount = level == 0 ? 0x0010_0000u : 0x0000_1000u;
        }

        var addressAdd = ReadRegisterLong(baseOffset + DmaAddressAddOffset);
        var readIncrement = ((addressAdd >> 8) & 1) != 0;
        var writeAdd = addressAdd & 7;
        if (!readIncrement || writeAdd != 1)
        {
            return;
        }

        var source = readAddress;
        var destination = writeAddress;
        for (uint index = 0; index < byteCount; index++)
        {
            _dmaBus.WriteByte(destination, _dmaBus.ReadByte(source));
            source++;
            destination++;
        }

        if ((mode & DmaReadUpdateBit) != 0)
        {
            WriteRegisterLong(baseOffset + DmaReadAddressOffset, source);
        }

        if ((mode & DmaWriteUpdateBit) != 0)
        {
            WriteRegisterLong(baseOffset + DmaWriteAddressOffset, destination);
        }

        LastDmaTransfer = new ScuDmaTransfer(level, readAddress, writeAddress, byteCount, addressAdd, mode);
        CompletedDmaCount++;
        InterruptStatus |= level switch
        {
            0 => Dma0EndBit,
            1 => Dma1EndBit,
            _ => Dma2EndBit,
        };
    }

    private static byte ReadWordByte(uint value, uint byteOffset)
    {
        var shift = (int)((3 - byteOffset) * 8);
        return (byte)(value >> shift);
    }

    private static uint WriteWordByte(uint current, uint byteOffset, byte value)
    {
        var shift = (int)((3 - byteOffset) * 8);
        var mask = 0xFFu << shift;
        return (current & ~mask) | ((uint)value << shift);
    }

    private uint ReadRegisterLong(uint offset) =>
        ((uint)(_registers.TryGetValue(offset, out var b0) ? b0 : 0) << 24)
        | ((uint)(_registers.TryGetValue(offset + 1, out var b1) ? b1 : 0) << 16)
        | ((uint)(_registers.TryGetValue(offset + 2, out var b2) ? b2 : 0) << 8)
        | (uint)(_registers.TryGetValue(offset + 3, out var b3) ? b3 : 0);

    private void WriteRegisterLong(uint offset, uint value)
    {
        _registers[offset] = (byte)(value >> 24);
        _registers[offset + 1] = (byte)(value >> 16);
        _registers[offset + 2] = (byte)(value >> 8);
        _registers[offset + 3] = (byte)value;
    }

    private static void RecordOffset(Dictionary<uint, long> offsets, uint offset)
    {
        offsets.TryGetValue(offset, out var count);
        offsets[offset] = count + 1;
    }

    private static IReadOnlyList<(uint Offset, long Count)> GetHotOffsets(Dictionary<uint, long> offsets, int count) =>
        offsets
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .Take(count)
            .Select(static pair => (pair.Key, pair.Value))
            .ToArray();
}

public sealed record ScuDmaTransfer(
    int Level,
    uint ReadAddress,
    uint WriteAddress,
    uint ByteCount,
    uint AddressAdd,
    uint Mode);
