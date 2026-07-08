namespace SystemRegisIII.Core.Core.CdBlock;

public sealed class RawDiscImage : IDiscImage, IDisposable
{
    public const int DefaultSectorSize = 2048;
    private readonly FileStream _stream;

    public RawDiscImage(string path, int sectorSize = DefaultSectorSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sectorSize, 0);

        _stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Name = Path.GetFileName(path);
        SectorSize = sectorSize;
    }

    public string Name { get; }
    public long LengthBytes => _stream.Length;
    public int SectorSize { get; }
    public long SectorCount => LengthBytes / SectorSize;

    public int ReadSector(long logicalBlockAddress, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(logicalBlockAddress);
        if (logicalBlockAddress >= SectorCount)
        {
            return 0;
        }

        var bytesToRead = Math.Min(destination.Length, SectorSize);
        _stream.Position = logicalBlockAddress * SectorSize;
        return _stream.Read(destination[..bytesToRead]);
    }

    public void Dispose() => _stream.Dispose();
}
