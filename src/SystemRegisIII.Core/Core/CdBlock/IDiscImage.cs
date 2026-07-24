namespace SystemRegisIII.Core.Core.CdBlock;

public interface IDiscImage : IDisposable
{
    string Name { get; }
    long LengthBytes { get; }
    int SectorSize { get; }
    long SectorCount { get; }
    int ReadSector(long logicalBlockAddress, Span<byte> destination);
}

public interface ICdSectorSubheaderSource
{
    bool TryReadSectorSubheader(long logicalBlockAddress, out CdSectorSubheader subheader);
}

public readonly record struct CdSectorSubheader(byte File, byte Channel, byte Submode, byte CodingInfo);
