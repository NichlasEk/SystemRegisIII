using SystemRegisIII.Core.Core.Bus;
using SystemRegisIII.Core.Host.Input;

namespace SystemRegisIII.Core.Core.Smpc;

public sealed class SmpcRegisterBusDevice : IInspectableBusDevice
{
    private const uint CommandRegister = 0x1F;
    private const uint StatusRegister = 0x61;
    private const uint StatusFlagRegister = 0x63;
    private const uint InputRegisterBase = 0x01;
    private const uint OutputRegisterBase = 0x21;
    private const uint RegisterStride = 2;
    private const byte IntbackCommand = 0x10;
    private const byte ClockChange352Command = 0x0E;
    private const byte ClockChange320Command = 0x0F;
    private const byte SetTimeCommand = 0x16;
    private const byte SetSystemMemoryCommand = 0x17;
    private const byte ResetEnableCommand = 0x19;
    private const byte ResetDisableCommand = 0x1A;
    private const byte SmpcStatusValid = 0x40;
    private const byte RtcValidStatus = 0x80;
    private const byte SystemStatus0ResetDisabled = 0x40;
    private const byte JapanAreaCode = 0x01;
    private const byte SystemStatus1Default = 0x34;
    private const byte SystemStatus2Default = 0x00;
    private const byte NoPeripheralPortStatus = 0xF0;
    private const int CommandBusyStatusReads = 2;
    private const int ClockChangeVBlankInCount = 3;
    private static readonly byte[] DefaultRtc =
    [
        0x19, // Century, BCD.
        0x96, // Year, BCD.
        0x11, // Weekday/month: Monday, January.
        0x01, // Day.
        0x12, // Hour.
        0x00, // Minute.
        0x00, // Second.
    ];

    private readonly Dictionary<uint, long> _readOffsets = [];
    private readonly Dictionary<uint, long> _writeOffsets = [];
    private readonly Queue<byte> _recentCommands = new();
    private readonly byte[] _inputRegisters = new byte[7];
    private readonly byte[] _outputRegisters = new byte[32];
    private byte[] _digitalPadPortData;
    private readonly byte[] _rtc = new byte[7];
    private readonly byte[] _systemMemory = new byte[4];
    private bool _rtcValid = true;
    private bool _resetNmiEnabled;
    private bool _statusFlag;
    private byte _busBuffer;
    private bool _peripheralIntbackPending;
    private int _busyStatusReadsRemaining;
    private int _clockChangeVBlankInsRemaining;
    private bool _clockChangeNmiPending;

    public SmpcRegisterBusDevice(
        SaturnInputState digitalPadState = SaturnInputState.None,
        IReadOnlyList<byte>? digitalPadPeripheralData = null)
    {
        DigitalPadState = digitalPadState;
        _digitalPadPortData = digitalPadPeripheralData is null
            ? BuildDigitalPadPortData(digitalPadState)
            : CopyDigitalPadPeripheralData(digitalPadPeripheralData);
        DefaultRtc.CopyTo(_rtc, 0);
    }

    public string Name => "SMPC Registers";
    public long ReadCount { get; private set; }
    public long WriteCount { get; private set; }
    public uint? FirstReadOffset { get; private set; }
    public uint? LastReadOffset { get; private set; }
    public uint? FirstWriteOffset { get; private set; }
    public uint? LastWriteOffset { get; private set; }
    public byte LastCommand { get; private set; }
    public bool SlaveSh2Enabled { get; private set; }
    public int PendingInterrupts { get; private set; }
    public byte StatusRegisterValue { get; private set; } = SmpcStatusValid;
    public IReadOnlyList<byte> InputRegisters => _inputRegisters;
    public IReadOnlyList<byte> OutputRegisters => _outputRegisters;
    public IReadOnlyList<byte> RecentCommands => _recentCommands.ToArray();
    public SaturnInputState DigitalPadState { get; private set; }

    public byte ReadByte(uint offset)
    {
        ReadCount++;
        FirstReadOffset ??= offset;
        LastReadOffset = offset;
        RecordOffset(_readOffsets, offset);
        offset &= 0x7F;

        if (offset == StatusRegister)
        {
            return StatusRegisterValue;
        }

        if (offset == StatusFlagRegister)
        {
            if (!_statusFlag)
            {
                return (byte)(_busBuffer & 0xFE);
            }

            if (_busyStatusReadsRemaining > 0)
            {
                _busyStatusReadsRemaining--;
                return (byte)(_busBuffer | 0x01);
            }

            _statusFlag = false;
            return (byte)(_busBuffer & 0xFE);
        }

        if (TryGetRegisterIndex(offset, OutputRegisterBase, _outputRegisters.Length, out var outputIndex))
        {
            return _outputRegisters[outputIndex];
        }

        return _busBuffer;
    }

    public void WriteByte(uint offset, byte value)
    {
        WriteCount++;
        FirstWriteOffset ??= offset;
        LastWriteOffset = offset;
        RecordOffset(_writeOffsets, offset);
        _busBuffer = value;
        offset &= 0x7F;

        if (TryGetRegisterIndex(offset, InputRegisterBase, _inputRegisters.Length, out var inputIndex))
        {
            _inputRegisters[inputIndex] = value;
            if (inputIndex == 0 && _peripheralIntbackPending)
            {
                if ((value & 0x40) != 0)
                {
                    _peripheralIntbackPending = false;
                }
                else if ((value & 0x80) != 0)
                {
                    _peripheralIntbackPending = false;
                    BuildPeripheralIntbackResponse();
                    PendingInterrupts++;
                }
            }
            return;
        }

        if (offset == StatusFlagRegister)
        {
            _statusFlag = (value & 1) != 0;
            if (!_statusFlag)
            {
                _busyStatusReadsRemaining = 0;
            }
            return;
        }

        if (offset != CommandRegister)
        {
            return;
        }

        LastCommand = value;
        if (_statusFlag)
        {
            _busyStatusReadsRemaining = CommandBusyStatusReads;
        }
        _recentCommands.Enqueue(value);
        while (_recentCommands.Count > 16)
        {
            _recentCommands.Dequeue();
        }

        switch (value)
        {
            case IntbackCommand:
                BuildIntbackResponse();
                PendingInterrupts++;
                break;
            case 0x02:
                SlaveSh2Enabled = true;
                break;
            case 0x03:
                SlaveSh2Enabled = false;
                break;
            case ClockChange352Command:
            case ClockChange320Command:
                SlaveSh2Enabled = false;
                _clockChangeVBlankInsRemaining = ClockChangeVBlankInCount;
                _clockChangeNmiPending = false;
                break;
            case SetTimeCommand:
                Array.Copy(_inputRegisters, _rtc, _rtc.Length);
                _rtcValid = true;
                break;
            case SetSystemMemoryCommand:
                Array.Copy(_inputRegisters, _systemMemory, _systemMemory.Length);
                break;
            case ResetEnableCommand:
                _resetNmiEnabled = true;
                break;
            case ResetDisableCommand:
                _resetNmiEnabled = false;
                break;
        }
    }

    public bool TryConsumeInterrupt()
    {
        if (PendingInterrupts <= 0)
        {
            return false;
        }

        PendingInterrupts--;
        return true;
    }

    public void NotifyVBlankIn()
    {
        if (_clockChangeVBlankInsRemaining <= 0)
        {
            return;
        }

        _clockChangeVBlankInsRemaining--;
        if (_clockChangeVBlankInsRemaining == 0)
        {
            _clockChangeNmiPending = true;
        }
    }

    public bool TryConsumeClockChangeNmi()
    {
        if (!_clockChangeNmiPending)
        {
            return false;
        }

        _clockChangeNmiPending = false;
        return true;
    }

    public void SetDigitalPadState(SaturnInputState state)
    {
        if (DigitalPadState == state)
        {
            return;
        }

        DigitalPadState = state;
        _digitalPadPortData = BuildDigitalPadPortData(state);
    }

    public IReadOnlyList<(uint Offset, long Count)> GetHotReadOffsets(int count) =>
        GetHotOffsets(_readOffsets, count);

    public IReadOnlyList<(uint Offset, long Count)> GetHotWriteOffsets(int count) =>
        GetHotOffsets(_writeOffsets, count);

    private static void RecordOffset(Dictionary<uint, long> offsets, uint offset)
    {
        offsets.TryGetValue(offset, out var count);
        offsets[offset] = count + 1;
    }

    private void BuildIntbackResponse()
    {
        var returnsSmpcStatus = (_inputRegisters[0] & 0x01) != 0;
        var returnsPeripheralData = (_inputRegisters[1] & 0x08) != 0;

        if (returnsSmpcStatus)
        {
            StatusRegisterValue = returnsPeripheralData ? (byte)0x2F : SmpcStatusValid;
            _peripheralIntbackPending = returnsPeripheralData;

            _outputRegisters[0] = (byte)((_rtcValid ? RtcValidStatus : 0) | (!_resetNmiEnabled ? SystemStatus0ResetDisabled : 0));
            Array.Copy(_rtc, 0, _outputRegisters, 1, _rtc.Length);
            _outputRegisters[9] = JapanAreaCode;
            _outputRegisters[10] = SystemStatus1Default;
            _outputRegisters[11] = SystemStatus2Default;
            Array.Copy(_systemMemory, 0, _outputRegisters, 12, _systemMemory.Length);
            return;
        }

        _peripheralIntbackPending = false;
        if (returnsPeripheralData)
        {
            BuildPeripheralIntbackResponse();
            return;
        }

        StatusRegisterValue = SmpcStatusValid;
    }

    private void BuildPeripheralIntbackResponse()
    {
        var port1Mode = (_inputRegisters[1] >> 4) & 0x03;
        var port2Mode = (_inputRegisters[1] >> 6) & 0x03;
        StatusRegisterValue = (byte)(0xC0 | (port2Mode << 2) | port1Mode);
        var outputIndex = 0;
        if (port1Mode != 0x03)
        {
            _digitalPadPortData.CopyTo(_outputRegisters, outputIndex);
            outputIndex += _digitalPadPortData.Length;
        }

        if (port2Mode != 0x03 && outputIndex < _outputRegisters.Length)
        {
            _outputRegisters[outputIndex] = NoPeripheralPortStatus;
        }
    }

    private static bool TryGetRegisterIndex(uint offset, uint start, int count, out int index)
    {
        if (offset < start)
        {
            index = 0;
            return false;
        }

        var relative = offset - start;
        if (relative % RegisterStride != 0)
        {
            index = 0;
            return false;
        }

        index = (int)(relative / RegisterStride);
        return index < count;
    }

    private static IReadOnlyList<(uint Offset, long Count)> GetHotOffsets(Dictionary<uint, long> offsets, int count) =>
        offsets
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key)
            .Take(count)
            .Select(static pair => (pair.Key, pair.Value))
            .ToArray();

    private static byte[] BuildDigitalPadPortData(SaturnInputState state)
    {
        var bits = (ushort)state;
        return
        [
            0xF1,
            0x02,
            (byte)(0xFF & ~(bits & 0x00FF)),
            (byte)(0xFF & ~((bits >> 8) & 0x00FF)),
        ];
    }

    private static byte[] CopyDigitalPadPeripheralData(IReadOnlyList<byte> peripheralData)
    {
        if (peripheralData.Count != 4)
        {
            throw new ArgumentException(
                "Digital pad peripheral data must contain exactly four bytes: ID, size, data1, data2.",
                nameof(peripheralData));
        }

        var copy = new byte[4];
        for (var i = 0; i < copy.Length; i++)
        {
            copy[i] = peripheralData[i];
        }

        return copy;
    }
}
