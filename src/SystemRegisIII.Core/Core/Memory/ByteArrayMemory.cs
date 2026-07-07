using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Core.Core.Memory;

public sealed class ByteArrayMemory : IMainMemory, IBusDevice, IWriteTrackedMemory
{
    private readonly byte[] _memory;
    private uint _firstWriteOffset = uint.MaxValue;
    private uint _lastWriteOffset;

    public ByteArrayMemory(string name, int sizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        Name = name;
        _memory = new byte[sizeBytes];
    }

    public string Name { get; }
    public int SizeBytes => _memory.Length;
    public Span<byte> Span => _memory;
    public long WriteCount { get; private set; }
    public uint? FirstWriteOffset => WriteCount == 0 ? null : _firstWriteOffset;
    public uint? LastWriteOffset => WriteCount == 0 ? null : _lastWriteOffset;

    public byte ReadByte(uint offset) => _memory[Wrap(offset)];

    public void WriteByte(uint offset, byte value)
    {
        var wrappedOffset = (uint)Wrap(offset);
        _memory[wrappedOffset] = value;
        WriteCount++;
        _firstWriteOffset = Math.Min(_firstWriteOffset, wrappedOffset);
        _lastWriteOffset = Math.Max(_lastWriteOffset, wrappedOffset);
    }

    public void Clear()
    {
        Array.Clear(_memory);
        WriteCount = 0;
        _firstWriteOffset = uint.MaxValue;
        _lastWriteOffset = 0;
    }

    private int Wrap(uint offset) => (int)(offset % (uint)_memory.Length);
}
