using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Core.Core.Memory;

public sealed class OverlayMemory : IMainMemory, IBusDevice, IWriteTrackedMemory
{
    private readonly ByteArrayMemory _inner;
    private readonly Dictionary<uint, byte> _readOverlay = [];

    public OverlayMemory(ByteArrayMemory inner)
    {
        _inner = inner;
    }

    public string Name => _inner.Name;
    public int SizeBytes => _inner.SizeBytes;
    public Span<byte> Span => _inner.Span;
    public long WriteCount => _inner.WriteCount;
    public uint? FirstWriteOffset => _inner.FirstWriteOffset;
    public uint? LastWriteOffset => _inner.LastWriteOffset;

    public void AddReadOnlyLong(uint offset, uint value)
    {
        _readOverlay[offset] = (byte)(value >> 24);
        _readOverlay[offset + 1] = (byte)(value >> 16);
        _readOverlay[offset + 2] = (byte)(value >> 8);
        _readOverlay[offset + 3] = (byte)value;
    }

    public byte ReadByte(uint offset) =>
        _readOverlay.TryGetValue(offset, out var value) ? value : _inner.ReadByte(offset);

    public void WriteByte(uint offset, byte value)
    {
        _inner.WriteByte(offset, value);
    }

    public void Clear()
    {
        _inner.Clear();
    }
}
