using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Core.Core.Vdp2;

public sealed class Vdp2RegisterBusDevice : DebugMemoryBusDevice
{
    private const int RegisterAreaSize = 0x40000;
    private const int LowResolutionLineClocks = 0x1AB;
    private const int LowResolutionHsyncStart = 0x15B;
    private const int VideoClockNumerator = 29;
    private const int VideoClockDenominator = 24;

    private long _videoClockRemainder;
    private int _horizontalCounter;
    private int _verticalCounter;
    private ushort _latchedHorizontalCounter;
    private ushort _latchedVerticalCounter;

    public Vdp2RegisterBusDevice()
        : base("VDP2 Registers", RegisterAreaSize)
    {
    }

    public override byte ReadByte(uint offset)
    {
        var storedValue = base.ReadByte(offset);
        switch (offset & 0x1FFFF)
        {
            case 0x02:
                LatchCounters();
                return storedValue;
            case 0x04:
                return (byte)(CurrentTvStatus() >> 8);
            case 0x05:
                return (byte)CurrentTvStatus();
            case 0x08:
                return (byte)(_latchedHorizontalCounter >> 8);
            case 0x09:
                return (byte)_latchedHorizontalCounter;
            case 0x0A:
                return (byte)(_latchedVerticalCounter >> 8);
            case 0x0B:
                return (byte)_latchedVerticalCounter;
            default:
                return storedValue;
        }
    }

    public void AdvanceMasterInstructions(int instructionCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instructionCount);

        _videoClockRemainder += (long)instructionCount * VideoClockNumerator;
        var elapsedVideoClocks = _videoClockRemainder / VideoClockDenominator;
        _videoClockRemainder %= VideoClockDenominator;
        AdvanceVideoClocks(elapsedVideoClocks);
    }

    private void AdvanceVideoClocks(long clocks)
    {
        var combined = _horizontalCounter + clocks;
        var elapsedLines = combined / LowResolutionLineClocks;
        _horizontalCounter = (int)(combined % LowResolutionLineClocks);
        _verticalCounter = (int)((_verticalCounter + elapsedLines) & 0x1FF);
    }

    private void LatchCounters()
    {
        var encodedHorizontalCounter = _horizontalCounter >= LowResolutionHsyncStart
            ? _horizontalCounter + (0x200 - LowResolutionLineClocks)
            : _horizontalCounter;
        _latchedHorizontalCounter = (ushort)(encodedHorizontalCounter << 1);
        _latchedVerticalCounter = (ushort)_verticalCounter;
    }

    private ushort CurrentTvStatus()
    {
        var displayOn = (ReadBigEndianWord(0x00) & 0x8000) != 0;
        var internalVBlank = !displayOn || _verticalCounter >= 0x0E0;
        var horizontalBlank = _horizontalCounter >= 0x140;

        return (ushort)((internalVBlank ? 1 << 3 : 0)
            | (horizontalBlank ? 1 << 2 : 0)
            | (1 << 1));
    }
}
