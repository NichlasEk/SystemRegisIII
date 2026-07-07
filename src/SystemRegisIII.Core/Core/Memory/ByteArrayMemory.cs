using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Core.Core.Memory;

public sealed class ByteArrayMemory : IMainMemory, IBusDevice
{
    private readonly byte[] _memory;

    public ByteArrayMemory(string name, int sizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        Name = name;
        _memory = new byte[sizeBytes];
    }

    public string Name { get; }
    public int SizeBytes => _memory.Length;
    public Span<byte> Span => _memory;

    public byte ReadByte(uint offset) => _memory[Wrap(offset)];

    public void WriteByte(uint offset, byte value)
    {
        _memory[Wrap(offset)] = value;
    }

    public void Clear()
    {
        Array.Clear(_memory);
    }

    private int Wrap(uint offset) => (int)(offset % (uint)_memory.Length);
}
