using SystemRegisIII.Core.Core.Bus;

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
    private const byte SmpcStatusValid = 0x40;
    private const byte SystemStatus0ResetDisabled = 0x40;
    private const byte JapanAreaCode = 0x01;
    private const byte SystemStatus1Default = 0x34;
    private const byte SystemStatus2Default = 0x00;
    private const byte NoPeripheralPortStatus = 0xF0;
    private readonly Dictionary<uint, long> _readOffsets = [];
    private readonly Dictionary<uint, long> _writeOffsets = [];
    private readonly Queue<byte> _recentCommands = new();
    private readonly byte[] _inputRegisters = new byte[7];
    private readonly byte[] _outputRegisters = new byte[32];

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

    public byte ReadByte(uint offset)
    {
        ReadCount++;
        FirstReadOffset ??= offset;
        LastReadOffset = offset;
        RecordOffset(_readOffsets, offset);

        if (offset == StatusRegister)
        {
            return StatusRegisterValue;
        }

        // The bringup model completes SMPC commands immediately.
        if (offset == StatusFlagRegister)
        {
            return 0;
        }

        if (TryGetRegisterIndex(offset, OutputRegisterBase, _outputRegisters.Length, out var outputIndex))
        {
            return _outputRegisters[outputIndex];
        }

        return 0;
    }

    public void WriteByte(uint offset, byte value)
    {
        WriteCount++;
        FirstWriteOffset ??= offset;
        LastWriteOffset = offset;
        RecordOffset(_writeOffsets, offset);

        if (TryGetRegisterIndex(offset, InputRegisterBase, _inputRegisters.Length, out var inputIndex))
        {
            _inputRegisters[inputIndex] = value;
            return;
        }

        if (offset == StatusFlagRegister)
        {
            return;
        }

        if (offset != CommandRegister)
        {
            return;
        }

        LastCommand = value;
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
        Array.Clear(_outputRegisters);

        var returnsSmpcStatus = (_inputRegisters[0] & 0x01) != 0;
        var returnsPeripheralData = (_inputRegisters[1] & 0x08) != 0;
        var port1Mode = (_inputRegisters[1] >> 4) & 0x03;
        var port2Mode = (_inputRegisters[1] >> 6) & 0x03;

        if (returnsPeripheralData)
        {
            StatusRegisterValue = (byte)(0xC0 | (port2Mode << 2) | port1Mode);
            var outputIndex = 0;
            if (port1Mode != 0x03)
            {
                _outputRegisters[outputIndex++] = NoPeripheralPortStatus;
            }

            if (port2Mode != 0x03 && outputIndex < _outputRegisters.Length)
            {
                _outputRegisters[outputIndex] = NoPeripheralPortStatus;
            }

            return;
        }

        StatusRegisterValue = SmpcStatusValid;

        if (!returnsSmpcStatus)
        {
            return;
        }

        _outputRegisters[0] = SystemStatus0ResetDisabled;
        _outputRegisters[9] = JapanAreaCode;
        _outputRegisters[10] = SystemStatus1Default;
        _outputRegisters[11] = SystemStatus2Default;
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
}
