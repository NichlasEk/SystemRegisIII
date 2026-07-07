namespace SystemRegisIII.Core.Core.Memory;

public sealed class BiosImage
{
    public BiosImage(string name, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            throw new ArgumentException("BIOS image cannot be empty.", nameof(bytes));
        }

        Name = name;
        Bytes = bytes;
    }

    public string Name { get; }
    public byte[] Bytes { get; }
}
