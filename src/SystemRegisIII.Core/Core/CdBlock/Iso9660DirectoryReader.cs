namespace SystemRegisIII.Core.Core.CdBlock;

internal sealed record CdFileInfo(uint Fad, uint SizeBytes, byte UnitSize, byte GapSize, byte FileNumber, byte Attributes)
{
    public ushort[] ToWords()
    {
        return
        [
            (ushort)(Fad >> 16),
            (ushort)Fad,
            (ushort)(SizeBytes >> 16),
            (ushort)SizeBytes,
            (ushort)((UnitSize << 8) | GapSize),
            (ushort)((FileNumber << 8) | Attributes),
        ];
    }
}

internal static class Iso9660DirectoryReader
{
    private const int PrimaryVolumeDescriptorSector = 16;
    private const int SectorFadBias = 150;
    private const byte DirectoryAttribute = 0x02;

    public static IReadOnlyList<CdFileInfo> ReadRootDirectory(IDiscImage discImage)
    {
        var sector = new byte[discImage.SectorSize];
        if (discImage.ReadSector(PrimaryVolumeDescriptorSector, sector) < 190 || !IsPrimaryVolumeDescriptor(sector))
        {
            return [];
        }

        var root = ReadDirectoryRecord(sector.AsSpan(156), includeSelfAndParent: true);
        if (root is null || (root.Attributes & DirectoryAttribute) == 0)
        {
            return [];
        }

        return ReadDirectory(discImage, root);
    }

    private static IReadOnlyList<CdFileInfo> ReadDirectory(IDiscImage discImage, CdFileInfo directory)
    {
        var entries = new List<CdFileInfo>();
        var sector = new byte[discImage.SectorSize];
        var firstLba = FadToLogicalBlockAddress(directory.Fad);
        var sectorCount = Math.Max(1, (int)((directory.SizeBytes + (uint)discImage.SectorSize - 1) / (uint)discImage.SectorSize));
        for (var sectorIndex = 0; sectorIndex < sectorCount; sectorIndex++)
        {
            Array.Clear(sector);
            var bytesRead = discImage.ReadSector(firstLba + sectorIndex, sector);
            var offset = 0;
            while (offset < bytesRead)
            {
                var length = sector[offset];
                if (length == 0)
                {
                    break;
                }

                if (offset + length > bytesRead)
                {
                    break;
                }

                var record = ReadDirectoryRecord(sector.AsSpan(offset, length), includeSelfAndParent: false);
                if (record is not null)
                {
                    entries.Add(record);
                }

                offset += length;
            }
        }

        return entries;
    }

    private static CdFileInfo? ReadDirectoryRecord(ReadOnlySpan<byte> record, bool includeSelfAndParent)
    {
        if (record.Length < 34 || record[0] < 34 || record[32] == 0)
        {
            return null;
        }

        var nameLength = record[32];
        if (record.Length < 33 + nameLength || (!includeSelfAndParent && IsSelfOrParent(record.Slice(33, nameLength))))
        {
            return null;
        }

        var lba = ReadUInt32LittleEndian(record[2..6]);
        var size = ReadUInt32LittleEndian(record[10..14]);
        return new CdFileInfo(
            (uint)SectorFadBias + lba,
            size,
            record[26],
            record[27],
            0,
            (byte)(record[25] & DirectoryAttribute));
    }

    private static bool IsPrimaryVolumeDescriptor(ReadOnlySpan<byte> sector) =>
        sector[0] == 1
        && sector[1] == (byte)'C'
        && sector[2] == (byte)'D'
        && sector[3] == (byte)'0'
        && sector[4] == (byte)'0'
        && sector[5] == (byte)'1';

    private static bool IsSelfOrParent(ReadOnlySpan<byte> name) =>
        name.Length == 1 && (name[0] == 0 || name[0] == 1);

    private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> value) =>
        value[0] | ((uint)value[1] << 8) | ((uint)value[2] << 16) | ((uint)value[3] << 24);

    private static long FadToLogicalBlockAddress(uint fad) => fad <= SectorFadBias ? 0 : fad - SectorFadBias;
}
