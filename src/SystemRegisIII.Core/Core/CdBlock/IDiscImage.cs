namespace SystemRegisIII.Core.Core.CdBlock;

public interface IDiscImage
{
    string Name { get; }
    long LengthBytes { get; }
    int SectorSize { get; }
    long SectorCount { get; }
    int ReadSector(long logicalBlockAddress, Span<byte> destination);
}
