using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Core.Core.Vdp1;

public sealed class Vdp1RegisterBusDevice : DebugMemoryBusDevice
{
    private const uint PlotTriggerModeOffset = 0x04;
    private const uint EndStatusOffset = 0x10;
    private bool _automaticStart;
    private bool _automaticDrawPending;

    public Vdp1RegisterBusDevice()
        : base("VDP1 Registers", 0x20)
    {
    }

    public event Action? DrawCompleted;

    public long CompletedDrawCount { get; private set; }
    public long ManualStartCount { get; private set; }
    public long AutomaticStartCount { get; private set; }
    public ushort PlotTriggerMode => ReadBigEndianWord(PlotTriggerModeOffset);

    public override byte ReadByte(uint offset)
    {
        var value = base.ReadByte(offset);
        return offset switch
        {
            EndStatusOffset => 0,
            EndStatusOffset + 1 => 2,
            _ => value,
        };
    }

    public override void WriteByte(uint offset, byte value)
    {
        base.WriteByte(offset, value);
        if (offset != PlotTriggerModeOffset + 1)
        {
            return;
        }

        var mode = ReadBigEndianWord(PlotTriggerModeOffset) & 3;
        _automaticStart = mode == 2;
        if (mode == 1)
        {
            ManualStartCount++;
            CompleteDraw();
        }
    }

    public void NotifyVBlankIn()
    {
        if (_automaticStart)
        {
            _automaticDrawPending = true;
        }
    }

    public void NotifyVBlankOut()
    {
        if (_automaticDrawPending)
        {
            _automaticDrawPending = false;
            AutomaticStartCount++;
            CompleteDraw();
        }
    }

    private void CompleteDraw()
    {
        CompletedDrawCount++;
        DrawCompleted?.Invoke();
    }
}
