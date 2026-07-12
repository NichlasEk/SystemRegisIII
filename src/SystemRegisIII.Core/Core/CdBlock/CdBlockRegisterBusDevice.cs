using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Core.Core.CdBlock;

public sealed class CdBlockRegisterBusDevice : IInspectableBusDevice
{
    private const uint DataTransferOffset = 0x00;
    private const uint HirqOffset = 0x08;
    private const uint HirqMaskOffset = 0x0C;
    private const uint Cr1Offset = 0x18;
    private const uint Cr2Offset = 0x1C;
    private const uint Cr3Offset = 0x20;
    private const uint Cr4Offset = 0x24;

    private const ushort HirqCmok = 0x0001;
    private const ushort HirqDataReady = 0x0002;
    private const ushort HirqSectorStored = 0x0004;
    private const ushort HirqSubcodeReady = 0x0400;
    private const ushort HirqEndSelector = 0x0040;
    private const ushort HirqEndHostIo = 0x0080;
    private const ushort HirqEndFileSystem = 0x0200;
    private const ushort HirqMountedStatusReady = 0x4658;
    private const byte CdStatusPeriodic = 0x20;
    private const byte CdStatusDataTransferRequest = 0x40;
    private const byte CdRomStatusBit = 0x80;
    private const byte DataTrackControlAdr = 0x41;
    private const byte FirstTrackNumber = 0x01;
    private const byte FirstTrackIndex = 0x01;
    private const int PartitionCount = 0x18;
    private const int AuthStartupPollCount = 8;
    private const int InitializeTransitionPollCount = 8;
    private const int StartupFirstPeriodicInstructionCount = 50_000;
    private const int StartupPeriodicInstructionCount = 200_000;
    private const int StartupPauseInstructionCount = 8_600_000;
    private const uint FirstTrackFad = 150;
    private static readonly byte[] SaturnSecurityHeader = "SEGA SEGASATURN "u8.ToArray();

    private readonly IDiscImage? _discImage;
    private readonly Dictionary<uint, long> _readOffsets = [];
    private readonly Dictionary<uint, long> _writeOffsets = [];
    private readonly Dictionary<uint, ushort> _writtenWords = [];
    private readonly uint[] _partitionFads = new uint[PartitionCount];
    private readonly uint[] _partitionSectorCounts = new uint[PartitionCount];
    private readonly Dictionary<byte, long> _commandCounts = [];
    private readonly Queue<byte> _recentCommands = new();
    private byte _getSectorLength;
    private byte _putSectorLength;
    private IReadOnlyList<CdFileInfo> _fileInfos = [];
    private bool _fileInfosValid;

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
    private byte _authDiscType;
    private int _authStartupPollsRemaining;
    private bool _authStartupCompleted;
    private int _initializeTransitionPollsRemaining;
    private int _commandCompletionHirqReadsRemaining;
    private ushort _commandCompletionHirqBits;
    private int _postAuthStatusInstructionsRemaining;
    private int _postSessionStatusInstructionsRemaining;
    private bool _startupPeriodicActive;
    private int _startupInstructionCount;
    private int _startupNextPeriodicInstructionCount;
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
        _authDiscType = DetectAuthenticationType(discImage);
        _authStartupPollsRemaining = mountedDiscInitialStatus is null && _authDiscType == 0x04 ? AuthStartupPollCount : 0;
        _authStartupCompleted = _authStartupPollsRemaining == 0;
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
    public byte AuthenticationType => _authDiscType;
    public bool AuthStartupCompleted => _authStartupCompleted;
    public IReadOnlyList<(byte Command, long Count)> CommandCounts =>
        _commandCounts
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .Select(static pair => (pair.Key, pair.Value))
            .ToArray();

    public IReadOnlyList<byte> RecentCommands => _recentCommands.ToArray();
    public long TotalCommandCount { get; private set; }
    public long HirqWriteCount { get; private set; }
    public ushort LastHirqWrite { get; private set; }
    public ushort HirqBeforeLastWrite { get; private set; }
    public ushort HirqValue => _hirq;
    public ushort ResponseCr1 => _cr1;
    public ushort ResponseCr2 => _cr2;
    public ushort ResponseCr3 => _cr3;
    public ushort ResponseCr4 => _cr4;
    public bool DataTransferActive => _dataTransferActive;
    public ushort DataTransferWordCount => _dataTransferWordCount;
    public ushort DataTransferWordsRead => _dataTransferWordsRead;

    public bool TryLoadInitialProgram(Span<byte> workRamHigh, out uint entryAddress, out int bytesLoaded)
    {
        entryAddress = 0;
        bytesLoaded = 0;
        if (_discImage is null)
        {
            return false;
        }

        var header = new byte[_discImage.SectorSize];
        if (_discImage.ReadSector(0, header) < 0xF4)
        {
            return false;
        }

        entryAddress = ((uint)header[0xF0] << 24)
            | ((uint)header[0xF1] << 16)
            | ((uint)header[0xF2] << 8)
            | header[0xF3];
        if (entryAddress < 0x0600_0000 || entryAddress >= 0x0610_0000)
        {
            return false;
        }

        // The Saturn bootstrap leaves the disc IP area immediately below the
        // initial program.  NiGHTS enters at 0x06004000 with the first four
        // sectors mirrored at 0x06002000; several BIOS services still consult
        // that header and boot code after handing control to the program.
        const int ipDestinationOffset = 0x2000;
        const int ipBytes = 0x2000;
        if (workRamHigh.Length < ipDestinationOffset + ipBytes)
        {
            return false;
        }

        var ipSector = new byte[_discImage.SectorSize];
        var ipCopied = 0;
        for (var ipLba = 0L; ipCopied < ipBytes; ipLba++)
        {
            Array.Clear(ipSector);
            var read = _discImage.ReadSector(ipLba, ipSector);
            if (read <= 0)
            {
                return false;
            }

            var copyLength = Math.Min(ipBytes - ipCopied, read);
            ipSector.AsSpan(0, copyLength).CopyTo(workRamHigh[(ipDestinationOffset + ipCopied)..]);
            ipCopied += copyLength;
        }

        EnsureFileInfosLoaded();
        var initialProgram = _fileInfos.FirstOrDefault(static file => (file.Attributes & 0x02) == 0);
        if (initialProgram is null)
        {
            return false;
        }

        var destinationOffset = checked((int)(entryAddress - 0x0600_0000));
        var remaining = Math.Min(checked((int)initialProgram.SizeBytes), workRamHigh.Length - destinationOffset);
        if (remaining <= 0)
        {
            return false;
        }

        var sector = new byte[_discImage.SectorSize];
        var lba = (long)initialProgram.Fad - FirstTrackFad;
        while (remaining > 0)
        {
            Array.Clear(sector);
            var read = _discImage.ReadSector(lba++, sector);
            if (read <= 0)
            {
                break;
            }

            var copyLength = Math.Min(remaining, read);
            sector.AsSpan(0, copyLength).CopyTo(workRamHigh[destinationOffset..]);
            destinationOffset += copyLength;
            remaining -= copyLength;
            bytesLoaded += copyLength;
        }

        return bytesLoaded > 0;
    }

    public byte ReadByte(uint offset)
    {
        ReadCount++;
        FirstReadOffset ??= offset;
        LastReadOffset = offset;
        RecordOffset(_readOffsets, offset);

        var wordOffset = NormalizeRegisterOffset(offset & ~1u);
        if (wordOffset == DataTransferOffset)
        {
            return ReadDataTransferByte((offset & 1) != 0);
        }

        var word = ReadWordRegister(wordOffset);
        return (offset & 1) == 0 ? (byte)(word >> 8) : (byte)word;
    }

    public void AdvanceMasterInstructions(int instructionCount)
    {
        if (instructionCount <= 0)
        {
            return;
        }

        if (_postAuthStatusInstructionsRemaining > 0)
        {
            _postAuthStatusInstructionsRemaining -= instructionCount;
            if (_postAuthStatusInstructionsRemaining <= 0)
            {
                PublishPostAuthBusyStatus();
            }
        }

        if (_postSessionStatusInstructionsRemaining > 0)
        {
            _postSessionStatusInstructionsRemaining -= instructionCount;
            if (_postSessionStatusInstructionsRemaining <= 0)
            {
                PublishPostSessionStatus();
            }
        }

        if (!_startupPeriodicActive)
        {
            return;
        }

        _startupInstructionCount = checked(_startupInstructionCount + instructionCount);
        if (_status == (byte)CdBlockDriveStatus.Busy
            && _startupInstructionCount >= StartupPauseInstructionCount)
        {
            _status = (byte)CdBlockDriveStatus.Pause;
        }

        while (_startupInstructionCount >= _startupNextPeriodicInstructionCount)
        {
            PublishStartupPeriodicStatus();
            _startupNextPeriodicInstructionCount += StartupPeriodicInstructionCount;
        }
    }

    public void WriteByte(uint offset, byte value)
    {
        WriteCount++;
        FirstWriteOffset ??= offset;
        LastWriteOffset = offset;
        RecordOffset(_writeOffsets, offset);

        var rawWordOffset = offset & ~1u;
        var wordOffset = NormalizeRegisterOffset(rawWordOffset);
        _writtenWords.TryGetValue(rawWordOffset, out var word);
        word = (offset & 1) == 0
            ? (ushort)((word & 0x00FF) | (value << 8))
            : (ushort)((word & 0xFF00) | value);
        _writtenWords[rawWordOffset] = word;

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
            HirqOffset => ReadHirq(),
            HirqMaskOffset => _hirqMask,
            Cr1Offset => _cr1,
            Cr2Offset => _cr2,
            Cr3Offset => _cr3,
            Cr4Offset => _cr4,
            _ => 0,
        };

    private static uint NormalizeRegisterOffset(uint offset) =>
        (offset & 0x7FFF) < 0x1000 ? offset & 0x3F : uint.MaxValue;

    private ushort ReadHirq()
    {
        if (_commandCompletionHirqReadsRemaining > 0)
        {
            _commandCompletionHirqReadsRemaining--;
            if (_commandCompletionHirqReadsRemaining == 0)
            {
                _hirq |= _commandCompletionHirqBits;
                _commandCompletionHirqBits = 0;
            }
        }

        return _hirq;
    }

    private void WriteWordRegister(uint offset, ushort value)
    {
        switch (offset)
        {
            case HirqOffset:
                HirqWriteCount++;
                LastHirqWrite = value;
                HirqBeforeLastWrite = _hirq;
                _hirq = _hirq == 0 ? HirqCmok : (ushort)(_hirq & value);
                if (!_hasExecutedCommand)
                {
                    EnterStatusMode();
                }
                else if (_hasExecutedCommand && (_hirq & HirqCmok) == 0)
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
        _startupPeriodicActive = false;
        var delayStartupHardwareInfo = !_hasExecutedCommand
            && _discImage is not null
            && _authDiscType == 0x04
            && LastCommandCode == 0x01;
        var commandCompletionHirqByteReads = delayStartupHardwareInfo
            ? 16
            : LastCommandCode is 0x03 or 0x30 ? 16 : 0;
        if (!_hasExecutedCommand && _discImage is not null)
        {
            _hirq |= 0x0BE0;
            if (_authDiscType == 0x04 && _authStartupPollsRemaining > 0 && LastCommandCode == 0x01)
            {
                _authStartupPollsRemaining = 0;
                _authStartupCompleted = true;
                _status = (byte)CdBlockDriveStatus.Busy;
            }
        }
        _hasExecutedCommand = true;
        _endHostIoCompleted = false;
        RecordRecentCommand(LastCommandCode);
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
            case 0x04:
                InitializeCdBlock();
                break;
            case 0x06:
                EndDataTransfer();
                break;
            case 0x10:
                PlayDisc();
                break;
            case 0x30:
                SetCdDeviceConnection();
                break;
            case 0x40:
                SetFilterRange();
                break;
            case 0x48:
                ResetSelector();
                break;
            case 0x60:
                SetSectorLength();
                break;
            case 0x61:
            case 0x63:
                GetSectorData();
                break;
            case 0x62:
                DeleteSectorData();
                break;
            case 0x70:
                ChangeDirectory();
                break;
            case 0x71:
                ReadDirectory();
                break;
            case 0x72:
                GetFileSystemScope();
                break;
            case 0x73:
                GetFileInfo();
                break;
            case 0x74:
                ReadFile();
                break;
            case 0x75:
                AbortFile();
                break;
            case 0x67:
                GetCopyError();
                break;
            case 0xE0:
                AuthenticateDevice();
                break;
            case 0xE1:
                GetAuthenticationStatus();
                break;
            default:
                EnterStatusMode();
                break;
        }

        if (commandCompletionHirqByteReads > 0)
        {
            _hirq &= unchecked((ushort)~HirqCmok);
            // The register device is byte-addressed, so one SH-2 word poll
            // performs two reads here.
            _commandCompletionHirqReadsRemaining = commandCompletionHirqByteReads;
            _commandCompletionHirqBits = LastCommandCode == 0x30
                ? (ushort)(HirqCmok | HirqEndSelector)
                : HirqCmok;
        }
        else
        {
            _hirq |= HirqCmok;
        }
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
        else if (_discImage is not null && LastCommandCode == 0x40)
        {
            _hirq |= HirqEndSelector;
        }
        else if (_discImage is not null && LastCommandCode is 0x48 or 0x60)
        {
            _hirq |= HirqEndSelector;
        }
        else if (_discImage is not null && LastCommandCode is 0x61 or 0x63)
        {
            _hirq |= HirqDataReady;
        }
        else if (_discImage is not null && LastCommandCode == 0x62)
        {
            _hirq |= HirqEndHostIo;
        }
        else if (_discImage is not null && LastCommandCode is 0x70 or 0x71 or 0x74)
        {
            _hirq |= HirqEndFileSystem;
        }
        else if (_discImage is not null && LastCommandCode == 0x73)
        {
            _hirq |= HirqDataReady;
        }
        else if (_discImage is not null && LastCommandCode == 0x75)
        {
            _hirq |= HirqEndFileSystem;
        }
        else if (_discImage is not null && LastCommandCode == 0xE0)
        {
            _hirq |= HirqEndFileSystem | HirqSectorStored | HirqSubcodeReady;
        }
    }

    private void GetCurrentStatus()
    {
        _statusMode = true;
        _status &= unchecked((byte)~CdStatusPeriodic);
        if (_discImage is not null && _authStartupPollsRemaining > 0)
        {
            _authStartupPollsRemaining--;
            if (_authStartupPollsRemaining == 0)
            {
                _authStartupCompleted = true;
            }

            WriteAuthenticationStartupResponse();
            return;
        }

        if (_discImage is not null && _initializeTransitionPollsRemaining > 0)
        {
            _initializeTransitionPollsRemaining--;
            if (_initializeTransitionPollsRemaining == 0)
            {
                _status = (byte)CdBlockDriveStatus.Pause;
            }
        }

        WriteStatusResponse(_status);
    }

    private void GetHardwareInfo()
    {
        _statusMode = true;
        _status &= unchecked((byte)~CdStatusPeriodic);
        _cr1 = (ushort)(_status << 8);
        _cr2 = 0x0002;
        _cr3 = 0x0000;
        _cr4 = 0x0600;
        if (_discImage is not null && _authStartupCompleted && _status == (byte)CdBlockDriveStatus.Busy)
        {
            _startupPeriodicActive = true;
            _startupInstructionCount = 0;
            _startupNextPeriodicInstructionCount = StartupFirstPeriodicInstructionCount;
        }
    }

    private void PublishStartupPeriodicStatus()
    {
        if (_status == (byte)CdBlockDriveStatus.Busy)
        {
            _cr1 = (ushort)(CdStatusPeriodic << 8);
            _cr2 = 0xFFFF;
            _cr3 = 0xFFFF;
            _cr4 = 0xFFFF;
        }
        else
        {
            _cr1 = (ushort)(((_status | CdStatusPeriodic) << 8));
            _cr2 = (ushort)((DataTrackControlAdr << 8) | FirstTrackNumber);
            _cr3 = (ushort)((FirstTrackIndex << 8) | (FirstTrackFad >> 16));
            _cr4 = (ushort)FirstTrackFad;
        }

        _hirq |= 0x0400;
    }

    private void GetTableOfContents()
    {
        if (_discImage is null)
        {
            GetCurrentStatus();
            return;
        }

        _statusMode = true;
        StartDataTransfer(BuildTableOfContents());
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
            fad = 0;
            resultStatus = 0x01;
        }
        else
        {
            fad = 0xFF_FFFF;
            resultStatus = 0xFF;
        }

        _statusMode = true;
        _cr1 = (ushort)((_status & 0x0F) << 8);
        _cr2 = 0;
        var sessionWord = ((uint)resultStatus << 8) | ((fad >> 16) & 0xFF);
        _cr3 = (ushort)sessionWord;
        _cr4 = (ushort)fad;
        if (session == 1 && _status == (byte)CdBlockDriveStatus.Busy)
        {
            _postSessionStatusInstructionsRemaining = 11_000;
        }
    }

    private void PublishPostSessionStatus()
    {
        _cr1 = 0x2400;
        _cr2 = 0x4101;
        _cr3 = 0x0100;
        _cr4 = (ushort)(FirstTrackFad + 0x10);
    }

    private void PlayDisc()
    {
        _statusMode = true;
        _cr1 = 0x0000;
        _cr2 = 0x4101;
        _cr3 = 0x0100;
        _cr4 = (ushort)(FirstTrackFad + 0x10);
    }

    private void SetFilterRange()
    {
        var partition = LastCommandCr3 >> 8;
        if (partition >= PartitionCount)
        {
            WriteStatusResponse((byte)(_status | CdStatusPeriodic));
            return;
        }

        _partitionFads[partition] = ((uint)(LastCommandCr1 & 0x00FF) << 16) | LastCommandCr2;
        _partitionSectorCounts[partition] = ((uint)(LastCommandCr3 & 0x00FF) << 16) | LastCommandCr4;
        WriteStatusResponse(_discImage is null ? _status : (byte)(_status | CdStatusPeriodic));
    }

    private void InitializeCdBlock()
    {
        _dataTransferActive = false;
        _dataTransferWordCount = 0;
        _dataTransferWordsRead = 0;
        _dataTransferWords = [];
        _dataTransferLowByteLatched = false;
        _getSectorLength = 0;
        _putSectorLength = 0;
        if (_discImage is not null)
        {
            _status = (byte)CdBlockDriveStatus.Busy;
            _initializeTransitionPollsRemaining = InitializeTransitionPollCount;
            _statusMode = true;
            _cr1 = 0;
            _cr2 = (ushort)((DataTrackControlAdr << 8) | FirstTrackNumber);
            _cr3 = (ushort)((FirstTrackIndex << 8) | (FirstTrackFad >> 16));
            _cr4 = (ushort)(FirstTrackFad + 0x10);
            return;
        }

        WriteStatusResponse(_status);
    }

    private void SetCdDeviceConnection()
    {
        _statusMode = true;
        _cr1 = 0;
        _cr2 = (ushort)((DataTrackControlAdr << 8) | FirstTrackNumber);
        _cr3 = (ushort)((FirstTrackIndex << 8) | (FirstTrackFad >> 16));
        _cr4 = (ushort)(FirstTrackFad + 0x10);
    }

    private void ResetSelector()
    {
        var flags = LastCommandCr1 & 0x00FF;
        if (flags == 0)
        {
            var partition = LastCommandCr3 >> 8;
            if (partition >= PartitionCount)
            {
                WriteStatusResponse((byte)(_status | CdStatusPeriodic));
                return;
            }

            _partitionFads[partition] = 0;
            _partitionSectorCounts[partition] = 0;
        }
        else if ((flags & 0x04) != 0)
        {
            Array.Clear(_partitionFads);
            Array.Clear(_partitionSectorCounts);
        }

        WriteStatusResponse(_discImage is null ? _status : (byte)(_status | CdStatusPeriodic));
    }

    private void SetSectorLength()
    {
        var getSectorLength = LastCommandCr1 & 0x00FF;
        var putSectorLength = LastCommandCr2 >> 8;
        if ((getSectorLength != 0xFF && getSectorLength > 3) || (putSectorLength != 0xFF && putSectorLength > 3))
        {
            WriteStatusResponse((byte)(_status | CdStatusPeriodic));
            return;
        }

        if (getSectorLength != 0xFF)
        {
            _getSectorLength = (byte)getSectorLength;
        }

        if (putSectorLength != 0xFF)
        {
            _putSectorLength = (byte)putSectorLength;
        }

        WriteStatusResponse(_discImage is null ? _status : (byte)(_status | CdStatusPeriodic));
    }

    private void GetSectorData()
    {
        if (_discImage is null)
        {
            GetCurrentStatus();
            return;
        }

        var partition = LastCommandCr3 >> 8;
        if (partition >= PartitionCount)
        {
            WriteStatusResponse((byte)(_status | CdStatusPeriodic));
            return;
        }

        var partitionSectorCount = _partitionSectorCounts[partition];
        var sectorOffset = LastCommandCr2 == 0xFFFF
            ? partitionSectorCount == 0 ? 0 : partitionSectorCount - 1
            : (uint)LastCommandCr2;
        var sectorCount = LastCommandCr4 == 0xFFFF
            ? partitionSectorCount > sectorOffset ? partitionSectorCount - sectorOffset : 0
            : (uint)LastCommandCr4;
        if (sectorCount == 0 || sectorOffset >= partitionSectorCount)
        {
            WriteStatusResponse((byte)(_status | CdStatusPeriodic));
            return;
        }

        sectorCount = Math.Min(sectorCount, partitionSectorCount - sectorOffset);
        StartDataTransfer(BuildSectorTransfer(_partitionFads[partition] + sectorOffset, sectorCount));
    }

    private void DeleteSectorData()
    {
        WriteStatusResponse(_discImage is null ? _status : (byte)(_status | CdStatusPeriodic));
    }

    private void ChangeDirectory()
    {
        if (_discImage is null)
        {
            GetCurrentStatus();
            return;
        }

        EnsureFileInfosLoaded();
        WriteFileSystemPositionResponse();
    }

    private void ReadDirectory()
    {
        if (_discImage is null)
        {
            GetCurrentStatus();
            return;
        }

        EnsureFileInfosLoaded();
        WriteStatusResponse((byte)(_status | CdStatusPeriodic));
    }

    private void GetFileSystemScope()
    {
        if (_discImage is null)
        {
            GetCurrentStatus();
            return;
        }

        EnsureFileInfosLoaded();
        if (!_fileInfosValid)
        {
            WriteStatusResponse((byte)(_status | CdStatusPeriodic));
            return;
        }

        _statusMode = true;
        _cr1 = (ushort)((_status << 8) | CdRomStatusBit);
        _cr2 = (ushort)Math.Min(_fileInfos.Count, ushort.MaxValue);
        _cr3 = 0x0100;
        _cr4 = 0x0002;
    }

    private void GetFileInfo()
    {
        if (_discImage is null)
        {
            GetCurrentStatus();
            return;
        }

        EnsureFileInfosLoaded();
        if (!_fileInfosValid)
        {
            WriteStatusResponse((byte)(_status | CdStatusPeriodic));
            return;
        }

        var fileId = ((uint)(LastCommandCr3 & 0x00FF) << 16) | LastCommandCr4;
        var words = fileId == 0xFF_FFFF
            ? BuildFileInfoTransfer(_fileInfos)
            : BuildFileInfoTransfer(GetFileInfoById(fileId) is { } fileInfo ? [fileInfo] : []);
        if (words.Length == 0)
        {
            WriteStatusResponse((byte)(_status | CdStatusPeriodic));
            return;
        }

        StartDataTransfer(words);
    }

    private void ReadFile()
    {
        if (_discImage is null)
        {
            GetCurrentStatus();
            return;
        }

        EnsureFileInfosLoaded();
        var partition = LastCommandCr3 >> 8;
        var fileId = ((uint)(LastCommandCr3 & 0x00FF) << 16) | LastCommandCr4;
        var sectorOffset = ((uint)(LastCommandCr1 & 0x00FF) << 16) | LastCommandCr2;
        var fileInfo = GetFileInfoById(fileId);
        if (partition >= PartitionCount || fileInfo is null)
        {
            WriteStatusResponse((byte)(_status | CdStatusPeriodic));
            return;
        }

        var totalSectors = (fileInfo.SizeBytes + 2047) >> 11;
        var sectorCount = sectorOffset >= totalSectors ? 0 : totalSectors - sectorOffset;
        _partitionFads[partition] = fileInfo.Fad + sectorOffset;
        _partitionSectorCounts[partition] = sectorCount;
        WriteStatusResponse((byte)(_status | CdStatusPeriodic));
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
        if (_discImage is null)
        {
            WriteStatusResponse(_status);
        }
        else
        {
            WriteFileSystemPositionResponse();
            _postAuthStatusInstructionsRemaining = 2_000;
        }
    }

    private void WriteFileSystemPositionResponse()
    {
        _statusMode = true;
        _cr1 = 0;
        _cr2 = (ushort)((DataTrackControlAdr << 8) | FirstTrackNumber);
        _cr3 = (ushort)((FirstTrackIndex << 8) | (FirstTrackFad >> 16));
        _cr4 = (ushort)FirstTrackFad;
    }

    private void GetCopyError()
    {
        _statusMode = true;
        _cr1 = (ushort)(_status << 8);
        _cr2 = 0;
        _cr3 = 0;
        _cr4 = 0;
    }

    private void AuthenticateDevice()
    {
        if (_discImage is null)
        {
            GetCurrentStatus();
            return;
        }

        var partition = LastCommandCr3 >> 8;
        if (partition >= PartitionCount)
        {
            WriteStatusResponse((byte)(_status | CdStatusPeriodic));
            return;
        }

        _statusMode = true;
        _cr1 = (ushort)(_status << 8);
        _cr2 = (ushort)((DataTrackControlAdr << 8) | FirstTrackNumber);
        _cr3 = (ushort)((FirstTrackIndex << 8) | (FirstTrackFad >> 16));
        _cr4 = (ushort)FirstTrackFad;
    }

    private void GetAuthenticationStatus()
    {
        if (_discImage is null)
        {
            GetCurrentStatus();
            return;
        }

        _statusMode = true;
        _cr1 = 0;
        _cr2 = _authDiscType;
        _cr3 = 0;
        _cr4 = 0;
        _postAuthStatusInstructionsRemaining = 2_000;
    }

    private void PublishPostAuthBusyStatus()
    {
        _cr1 = (ushort)(CdStatusPeriodic << 8);
        _cr2 = (ushort)((DataTrackControlAdr << 8) | FirstTrackNumber);
        _cr3 = (ushort)((FirstTrackIndex << 8) | (FirstTrackFad >> 16));
        _cr4 = (ushort)FirstTrackFad;
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

    private void StartDataTransfer(ushort[] words)
    {
        _statusMode = true;
        _dataTransferActive = true;
        _dataTransferWords = words;
        _dataTransferWordCount = (ushort)Math.Min(words.Length, ushort.MaxValue);
        _dataTransferWordsRead = 0;
        _dataTransferLowByteLatched = false;
        _cr1 = (ushort)(CdStatusDataTransferRequest << 8);
        _cr2 = _dataTransferWordCount;
        _cr3 = 0;
        _cr4 = 0;
    }

    private ushort[] BuildTableOfContents()
    {
        var words = new ushort[0x00CC];
        Array.Fill(words, (ushort)0xFFFF);

        var tracks = (_discImage as IDiscTableOfContents)?.Tracks;
        if (tracks is { Count: > 0 })
        {
            foreach (var track in tracks)
            {
                WriteTocEntry(words, track.Number - 1, track.ControlAdr, track.Fad);
            }
        }
        else
        {
            WriteTocEntry(words, entryIndex: 0, DataTrackControlAdr, FirstTrackFad);
        }

        var firstTrack = tracks is { Count: > 0 } ? tracks[0] : new CdTrackInfo(FirstTrackNumber, DataTrackControlAdr, FirstTrackFad);
        var lastTrack = tracks is { Count: > 0 } ? tracks[^1] : firstTrack;
        WriteTocPoint(
            words,
            entryIndex: 99,
            firstTrack.ControlAdr,
            firstTrack.Number,
            (_discImage as IDiscTableOfContents)?.DiscType ?? 0x00,
            0x00);
        WriteTocPoint(words, entryIndex: 100, lastTrack.ControlAdr, lastTrack.Number, 0x00, 0x00);
        WriteTocEntry(
            words,
            entryIndex: 101,
            lastTrack.ControlAdr,
            (_discImage as IDiscTableOfContents)?.LeadoutFad
                ?? FirstTrackFad + (uint)Math.Min(_discImage?.SectorCount ?? 0, 0x7F_FFFF));

        return words;
    }

    private ushort[] BuildSectorTransfer(uint startFad, uint sectorCount)
    {
        if (_discImage is null || sectorCount == 0)
        {
            return [];
        }

        var wordsPerSector = _discImage.SectorSize / 2;
        var words = new ushort[Math.Min((long)sectorCount * wordsPerSector, ushort.MaxValue)];
        var sectorBytes = new byte[_discImage.SectorSize];
        var wordIndex = 0;
        for (var sector = 0u; sector < sectorCount && wordIndex < words.Length; sector++)
        {
            Array.Clear(sectorBytes);
            var lba = FadToLogicalBlockAddress(startFad + sector);
            var bytesRead = _discImage.ReadSector(lba, sectorBytes);
            for (var byteIndex = 0; byteIndex + 1 < bytesRead && wordIndex < words.Length; byteIndex += 2)
            {
                words[wordIndex++] = (ushort)((sectorBytes[byteIndex] << 8) | sectorBytes[byteIndex + 1]);
            }
        }

        return wordIndex == words.Length ? words : words[..wordIndex];
    }

    private static long FadToLogicalBlockAddress(uint fad) => fad <= FirstTrackFad ? 0 : fad - FirstTrackFad;

    private void EnsureFileInfosLoaded()
    {
        if (_fileInfosValid || _discImage is null)
        {
            return;
        }

        _fileInfos = Iso9660DirectoryReader.ReadRootDirectory(_discImage);
        _fileInfosValid = _fileInfos.Count > 0;
    }

    private CdFileInfo? GetFileInfoById(uint fileId)
    {
        if (fileId < 2)
        {
            return _fileInfos.FirstOrDefault(static fileInfo => (fileInfo.Attributes & 0x02) != 0);
        }

        var index = checked((int)(fileId - 2));
        return index >= 0 && index < _fileInfos.Count ? _fileInfos[index] : null;
    }

    private static ushort[] BuildFileInfoTransfer(IReadOnlyList<CdFileInfo> fileInfos)
    {
        var words = new ushort[fileInfos.Count * 6];
        var wordIndex = 0;
        foreach (var fileInfo in fileInfos)
        {
            foreach (var word in fileInfo.ToWords())
            {
                words[wordIndex++] = word;
            }
        }

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

    private void WriteAuthenticationStartupResponse()
    {
        _cr1 = (ushort)((CdStatusPeriodic << 8) | 0x00FF);
        _cr2 = 0xFFFF;
        _cr3 = 0xFFFF;
        _cr4 = 0xFFFF;
    }

    private static void RecordOffset(Dictionary<uint, long> offsets, uint offset)
    {
        offsets.TryGetValue(offset, out var count);
        offsets[offset] = count + 1;
    }

    private void RecordRecentCommand(byte command)
    {
        TotalCommandCount++;
        _commandCounts.TryGetValue(command, out var count);
        _commandCounts[command] = count + 1;

        if (_recentCommands.Count == 16)
        {
            _recentCommands.Dequeue();
        }

        _recentCommands.Enqueue(command);
    }

    private static byte DetectAuthenticationType(IDiscImage? discImage)
    {
        if (discImage is null || discImage.SectorCount == 0)
        {
            return 0x00;
        }

        Span<byte> sector = stackalloc byte[2048];
        if (discImage.ReadSector(0, sector) < SaturnSecurityHeader.Length)
        {
            return 0x00;
        }

        return sector[..SaturnSecurityHeader.Length].SequenceEqual(SaturnSecurityHeader) ? (byte)0x04 : (byte)0x02;
    }

    private static IReadOnlyList<(uint Offset, long Count)> GetHotOffsets(Dictionary<uint, long> offsets, int count) =>
        offsets
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .Take(count)
            .Select(static pair => (pair.Key, pair.Value))
            .ToArray();
}
