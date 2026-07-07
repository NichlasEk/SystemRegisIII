using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Core.Core.CdBlock;

public sealed class CdBlockRegisterBusDevice : IInspectableBusDevice
{
    private const uint HirqOffset = 0x090008;
    private const uint HirqMaskOffset = 0x09000C;
    private const uint Cr1Offset = 0x090018;
    private const uint Cr2Offset = 0x09001C;
    private const uint Cr3Offset = 0x090020;
    private const uint Cr4Offset = 0x090024;

    private const ushort HirqCmok = 0x0001;
    private const ushort CdStatusBusyOrPeriodic = 0x2000;

    private readonly Dictionary<uint, long> _readOffsets = [];
    private readonly Dictionary<uint, long> _writeOffsets = [];
    private readonly Dictionary<uint, ushort> _writtenWords = [];

    private ushort _hirq;
    private ushort _hirqMask;
    private bool _statusMode;
    private ushort _cr1 = 0x0043;
    private ushort _cr2 = 0x4442;
    private ushort _cr3 = 0x4C4F;
    private ushort _cr4 = 0x434B;

    public string Name => "CD Block Register Mirror";
    public long ReadCount { get; private set; }
    public long WriteCount { get; private set; }
    public uint? FirstReadOffset { get; private set; }
    public uint? LastReadOffset { get; private set; }
    public uint? FirstWriteOffset { get; private set; }
    public uint? LastWriteOffset { get; private set; }
    public ushort LastCommandCr1 { get; private set; }
    public ushort LastCommandCr2 { get; private set; }
    public ushort LastCommandCr3 { get; private set; }
    public ushort LastCommandCr4 { get; private set; }

    public byte ReadByte(uint offset)
    {
        ReadCount++;
        FirstReadOffset ??= offset;
        LastReadOffset = offset;
        RecordOffset(_readOffsets, offset);

        var word = ReadWordRegister(offset & ~1u);
        return (offset & 1) == 0 ? (byte)(word >> 8) : (byte)word;
    }

    public void WriteByte(uint offset, byte value)
    {
        WriteCount++;
        FirstWriteOffset ??= offset;
        LastWriteOffset = offset;
        RecordOffset(_writeOffsets, offset);

        var wordOffset = offset & ~1u;
        _writtenWords.TryGetValue(wordOffset, out var word);
        word = (offset & 1) == 0
            ? (ushort)((word & 0x00FF) | (value << 8))
            : (ushort)((word & 0xFF00) | value);
        _writtenWords[wordOffset] = word;

        if ((offset & 1) != 0)
        {
            WriteWordRegister(wordOffset, word);
        }
    }

    public IReadOnlyList<(uint Offset, long Count)> GetHotReadOffsets(int count) =>
        GetHotOffsets(_readOffsets, count);

    public IReadOnlyList<(uint Offset, long Count)> GetHotWriteOffsets(int count) =>
        GetHotOffsets(_writeOffsets, count);

    private ushort ReadWordRegister(uint offset) =>
        offset switch
        {
            HirqOffset => _hirq,
            HirqMaskOffset => _hirqMask,
            Cr1Offset => _cr1,
            Cr2Offset => _cr2,
            Cr3Offset => _cr3,
            Cr4Offset => _cr4,
            _ => 0,
        };

    private void WriteWordRegister(uint offset, ushort value)
    {
        switch (offset)
        {
            case HirqOffset:
                EnterStatusMode();
                _hirq = _hirq == 0 ? HirqCmok : (ushort)(_hirq & value);
                break;
            case HirqMaskOffset:
                _hirqMask = value;
                break;
            case Cr1Offset:
                LastCommandCr1 = value;
                EnterStatusMode();
                _hirq |= HirqCmok;
                break;
            case Cr2Offset:
                LastCommandCr2 = value;
                break;
            case Cr3Offset:
                LastCommandCr3 = value;
                break;
            case Cr4Offset:
                LastCommandCr4 = value;
                EnterStatusMode();
                _hirq |= HirqCmok;
                break;
        }
    }

    private void EnterStatusMode()
    {
        if (_statusMode)
        {
            return;
        }

        _statusMode = true;
        _cr1 = CdStatusBusyOrPeriodic;
        _cr2 = 0;
        _cr3 = 0;
        _cr4 = 0;
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
