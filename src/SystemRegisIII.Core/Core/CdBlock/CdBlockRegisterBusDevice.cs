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
    private const ushort HirqEndFileSystem = 0x0200;
    private const ushort HirqMountedStatusReady = 0x4658;
    private const byte CdStatusPeriodic = 0x20;
    private const byte CdRomStatusBit = 0x80;
    private const byte DataTrackControlAdr = 0x41;
    private const byte FirstTrackNumber = 0x01;
    private const byte FirstTrackIndex = 0x01;
    private const uint FirstTrackFad = 150;

    private readonly IDiscImage? _discImage;
    private readonly Dictionary<uint, long> _readOffsets = [];
    private readonly Dictionary<uint, long> _writeOffsets = [];
    private readonly Dictionary<uint, ushort> _writtenWords = [];

    private ushort _hirq;
    private ushort _hirqMask;
    private bool _statusMode;
    private byte _status;
    private bool _hasExecutedCommand;
    private ushort _cr1 = 0x0043;
    private ushort _cr2 = 0x4442;
    private ushort _cr3 = 0x4C4F;
    private ushort _cr4 = 0x434B;

    public CdBlockRegisterBusDevice(
        IDiscImage? discImage = null,
        CdBlockDriveStatus? mountedDiscInitialStatus = null)
    {
        _discImage = discImage;
        _status = (byte)(discImage is null
            ? CdBlockDriveStatus.NoDisc
            : mountedDiscInitialStatus ?? CdBlockDriveStatus.Standby);
    }

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
    public byte LastCommandCode { get; private set; }
    public bool HasDisc => _discImage is not null;
    public string? DiscName => _discImage?.Name;
    public long DiscSectorCount => _discImage?.SectorCount ?? 0;
    public ushort ResponseCr1 => _cr1;
    public ushort ResponseCr2 => _cr2;
    public ushort ResponseCr3 => _cr3;
    public ushort ResponseCr4 => _cr4;

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
                _hirq = _hirq == 0 ? HirqCmok : (ushort)(_hirq & value);
                if (!_hasExecutedCommand)
                {
                    EnterStatusMode();
                }
                else if ((_hirq & HirqCmok) == 0)
                {
                    EnterPeriodicStatusMode();
                }

                break;
            case HirqMaskOffset:
                _hirqMask = value;
                break;
            case Cr1Offset:
                LastCommandCr1 = value;
                LastCommandCode = (byte)(value >> 8);
                break;
            case Cr2Offset:
                LastCommandCr2 = value;
                break;
            case Cr3Offset:
                LastCommandCr3 = value;
                break;
            case Cr4Offset:
                LastCommandCr4 = value;
                ExecuteCommand();
                break;
        }
    }

    private void ExecuteCommand()
    {
        _hasExecutedCommand = true;
        switch (LastCommandCode)
        {
            case 0x00:
                GetCurrentStatus();
                break;
            case 0x01:
                GetHardwareInfo();
                break;
            case 0x75:
                AbortFile();
                break;
            default:
                EnterStatusMode();
                break;
        }

        _hirq |= HirqCmok;
        if (_discImage is not null && LastCommandCode == 0x00)
        {
            _hirq |= HirqMountedStatusReady;
        }
        else if (_discImage is not null && LastCommandCode == 0x75)
        {
            _hirq |= HirqEndFileSystem;
        }
    }

    private void GetCurrentStatus()
    {
        _statusMode = true;
        _status &= unchecked((byte)~CdStatusPeriodic);
        WriteStatusResponse(_discImage is null ? _status : (byte)(_status | CdStatusPeriodic));
    }

    private void GetHardwareInfo()
    {
        _statusMode = true;
        _status &= unchecked((byte)~CdStatusPeriodic);
        _cr1 = (ushort)(_status << 8);
        _cr2 = 0x0201;
        _cr3 = 0x0000;
        _cr4 = 0x0400;
    }

    private void AbortFile()
    {
        _statusMode = true;
        WriteStatusResponse(_discImage is null ? _status : (byte)(_status | CdStatusPeriodic));
    }

    private void EnterStatusMode()
    {
        if (_statusMode)
        {
            return;
        }

        _statusMode = true;
        _cr1 = (ushort)(CdStatusPeriodic << 8);
        _cr2 = 0;
        _cr3 = 0;
        _cr4 = 0;
    }

    private void EnterPeriodicStatusMode()
    {
        _statusMode = true;
        WriteStatusResponse((byte)(_status | CdStatusPeriodic));
    }

    private void WriteStatusResponse() => WriteStatusResponse(_status);

    private void WriteStatusResponse(byte status)
    {
        if (_discImage is null)
        {
            _cr1 = (ushort)(status << 8);
            _cr2 = 0;
            _cr3 = 0;
            _cr4 = 0;
            return;
        }

        _cr1 = (ushort)((status << 8) | CdRomStatusBit);
        _cr2 = (ushort)((DataTrackControlAdr << 8) | FirstTrackNumber);
        _cr3 = (ushort)((FirstTrackIndex << 8) | (FirstTrackFad >> 16));
        _cr4 = (ushort)FirstTrackFad;
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
