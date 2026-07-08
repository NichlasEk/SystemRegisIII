using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Core.Core.CdBlock;

public sealed class CdBlockRegisterBusDevice : IInspectableBusDevice
{
    private const uint DataTransferOffset = 0x090000;
    private const uint HirqOffset = 0x090008;
    private const uint HirqMaskOffset = 0x09000C;
    private const uint Cr1Offset = 0x090018;
    private const uint Cr2Offset = 0x09001C;
    private const uint Cr3Offset = 0x090020;
    private const uint Cr4Offset = 0x090024;

    private const ushort HirqCmok = 0x0001;
    private const ushort HirqDataReady = 0x0002;
    private const ushort HirqEndHostIo = 0x0080;
    private const ushort HirqEndFileSystem = 0x0200;
    private const ushort HirqMountedStatusReady = 0x4658;
    private const byte CdStatusPeriodic = 0x20;
    private const byte CdStatusDataTransferRequest = 0x40;
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
    private bool _dataTransferActive;
    private bool _endHostIoCompleted;
    private ushort _dataTransferWordCount;
    private ushort _dataTransferWordsRead;
    private ushort[] _dataTransferWords = [];
    private bool _dataTransferLowByteLatched;
    private byte _dataTransferLowByte;
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

        var wordOffset = offset & ~1u;
        if (wordOffset == DataTransferOffset)
        {
            return ReadDataTransferByte((offset & 1) != 0);
        }

        var word = ReadWordRegister(wordOffset);
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
            DataTransferOffset => ReadDataTransferWord(),
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
        _endHostIoCompleted = false;
        switch (LastCommandCode)
        {
            case 0x00:
                GetCurrentStatus();
                break;
            case 0x01:
                GetHardwareInfo();
                break;
            case 0x02:
                GetTableOfContents();
                break;
            case 0x03:
                GetSessionInfo();
                break;
            case 0x06:
                EndDataTransfer();
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
        else if (_discImage is not null && LastCommandCode == 0x02)
        {
            _hirq |= HirqDataReady;
        }
        else if (_discImage is not null && LastCommandCode == 0x06 && _endHostIoCompleted)
        {
            _hirq |= HirqEndHostIo;
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

    private void GetTableOfContents()
    {
        if (_discImage is null)
        {
            GetCurrentStatus();
            return;
        }

        _statusMode = true;
        _dataTransferActive = true;
        _dataTransferWords = BuildTableOfContents();
        _dataTransferWordCount = (ushort)_dataTransferWords.Length;
        _dataTransferWordsRead = 0;
        _dataTransferLowByteLatched = false;
        _cr1 = (ushort)(CdStatusDataTransferRequest << 8 | CdRomStatusBit);
        _cr2 = _dataTransferWordCount;
        _cr3 = 0;
        _cr4 = 0;
    }

    private void GetSessionInfo()
    {
        if (_discImage is null)
        {
            GetCurrentStatus();
            return;
        }

        var session = LastCommandCr1 & 0x00FF;
        uint fad;
        byte resultStatus;
        if (session == 0)
        {
            fad = FirstTrackFad + (uint)Math.Min(_discImage.SectorCount, 0x7F_FFFF);
            resultStatus = 0x01;
        }
        else if (session == 1)
        {
            fad = FirstTrackFad;
            resultStatus = 0x01;
        }
        else
        {
            fad = 0xFF_FFFF;
            resultStatus = 0xFF;
        }

        _statusMode = true;
        _cr1 = (ushort)((_status << 8) | CdRomStatusBit);
        _cr2 = 0;
        var sessionWord = ((uint)resultStatus << 8) | ((fad >> 16) & 0xFF);
        _cr3 = (ushort)sessionWord;
        _cr4 = (ushort)fad;
    }

    private void EndDataTransfer()
    {
        _statusMode = true;
        var transferredWords = _dataTransferActive ? _dataTransferWordsRead : (ushort)0;
        _dataTransferActive = false;
        _endHostIoCompleted = transferredWords > 0;
        _dataTransferWordCount = 0;
        _dataTransferWordsRead = 0;
        _dataTransferWords = [];
        _dataTransferLowByteLatched = false;
        _cr1 = (ushort)(((_discImage is null ? _status : (byte)(_status | CdStatusPeriodic)) << 8)
            | ((transferredWords >> 16) & 0xFF));
        _cr2 = transferredWords;
        _cr3 = 0;
        _cr4 = 0;
    }

    private void AbortFile()
    {
        _statusMode = true;
        _dataTransferActive = false;
        _dataTransferWordCount = 0;
        _dataTransferWordsRead = 0;
        _dataTransferWords = [];
        _dataTransferLowByteLatched = false;
        WriteStatusResponse(_discImage is null ? _status : (byte)(_status | CdStatusPeriodic));
    }

    private byte ReadDataTransferByte(bool lowByte)
    {
        if (lowByte && _dataTransferLowByteLatched)
        {
            _dataTransferLowByteLatched = false;
            return _dataTransferLowByte;
        }

        var word = ReadDataTransferWord();
        _dataTransferLowByte = (byte)word;
        _dataTransferLowByteLatched = !lowByte;
        return lowByte ? (byte)word : (byte)(word >> 8);
    }

    private ushort ReadDataTransferWord()
    {
        if (!_dataTransferActive || _dataTransferWordsRead >= _dataTransferWordCount)
        {
            return 0;
        }

        return _dataTransferWords[_dataTransferWordsRead++];
    }

    private ushort[] BuildTableOfContents()
    {
        var words = new ushort[0x00CC];
        Array.Fill(words, (ushort)0xFFFF);

        WriteTocEntry(words, entryIndex: 0, DataTrackControlAdr, FirstTrackFad);
        WriteTocPoint(words, entryIndex: 99, DataTrackControlAdr, FirstTrackNumber, 0x00, 0x00);
        WriteTocPoint(words, entryIndex: 100, DataTrackControlAdr, FirstTrackNumber, 0x00, 0x00);
        WriteTocEntry(
            words,
            entryIndex: 101,
            DataTrackControlAdr,
            FirstTrackFad + (uint)Math.Min(_discImage?.SectorCount ?? 0, 0x7F_FFFF));

        return words;
    }

    private static void WriteTocEntry(ushort[] words, int entryIndex, byte controlAdr, uint fad)
    {
        var wordIndex = entryIndex * 2;
        words[wordIndex] = (ushort)(((uint)controlAdr << 8) | ((fad >> 16) & 0xFF));
        words[wordIndex + 1] = (ushort)fad;
    }

    private static void WriteTocPoint(ushort[] words, int entryIndex, byte controlAdr, byte point, byte parameter1, byte parameter2)
    {
        var wordIndex = entryIndex * 2;
        words[wordIndex] = (ushort)((controlAdr << 8) | point);
        words[wordIndex + 1] = (ushort)(((uint)parameter1 << 8) | parameter2);
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
