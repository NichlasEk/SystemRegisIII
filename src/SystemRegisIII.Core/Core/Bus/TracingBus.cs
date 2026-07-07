using SystemRegisIII.Core.Tools.TraceViewer;

namespace SystemRegisIII.Core.Core.Bus;

public sealed class TracingBus(ISaturnBus inner, ITraceEventSink trace) : ISaturnBus
{
    public byte ReadByte(uint address)
    {
        var value = inner.ReadByte(address);
        WriteTrace(address, 1, value, isWrite: false);
        return value;
    }

    public ushort ReadWord(uint address)
    {
        var value = inner.ReadWord(address);
        WriteTrace(address, 2, value, isWrite: false);
        return value;
    }

    public uint ReadLong(uint address)
    {
        var value = inner.ReadLong(address);
        WriteTrace(address, 4, value, isWrite: false);
        return value;
    }

    public void WriteByte(uint address, byte value)
    {
        inner.WriteByte(address, value);
        WriteTrace(address, 1, value, isWrite: true);
    }

    public void WriteWord(uint address, ushort value)
    {
        inner.WriteWord(address, value);
        WriteTrace(address, 2, value, isWrite: true);
    }

    public void WriteLong(uint address, uint value)
    {
        inner.WriteLong(address, value);
        WriteTrace(address, 4, value, isWrite: true);
    }

    private void WriteTrace(uint address, int sizeBytes, uint value, bool isWrite)
    {
        var op = isWrite ? "write" : "read";
        trace.Write(new TraceEvent("Bus", 0, $"{op}{sizeBytes * 8} 0x{address:X8}=0x{value:X8}"));
    }
}
