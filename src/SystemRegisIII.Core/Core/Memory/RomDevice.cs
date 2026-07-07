using SystemRegisIII.Core.Core.Bus;

namespace SystemRegisIII.Core.Core.Memory;

public sealed class RomDevice : IBusDevice
{
    private readonly byte[] _data;

    public RomDevice(string name, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("ROM data cannot be empty.", nameof(data));
        }

        Name = name;
        _data = data.ToArray();
    }

    public string Name { get; }
    public int SizeBytes => _data.Length;

    public byte ReadByte(uint offset) => _data[(int)(offset % (uint)_data.Length)];

    public void WriteByte(uint offset, byte value)
    {
    }
}
