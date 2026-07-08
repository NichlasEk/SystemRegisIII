namespace SystemRegisIII.Core.Core.CdBlock;

public sealed class CueDiscImage : IDiscImage, IDisposable
{
    private const int RawSectorSize = 2352;
    private const int Mode1UserDataOffset = 16;
    private const int Mode2UserDataOffset = 24;
    private readonly FileStream _trackStream;
    private readonly int _userDataOffset;

    public CueDiscImage(string cuePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cuePath);

        var dataTrack = FindFirstDataTrack(cuePath);
        _trackStream = File.Open(dataTrack.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _userDataOffset = dataTrack.UserDataOffset;
        Name = Path.GetFileName(cuePath);
    }

    public string Name { get; }
    public long LengthBytes => SectorCount * SectorSize;
    public int SectorSize => RawDiscImage.DefaultSectorSize;
    public long SectorCount => _trackStream.Length / RawSectorSize;

    public int ReadSector(long logicalBlockAddress, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(logicalBlockAddress);
        if (logicalBlockAddress >= SectorCount)
        {
            return 0;
        }

        var bytesToRead = Math.Min(destination.Length, SectorSize);
        _trackStream.Position = logicalBlockAddress * RawSectorSize + _userDataOffset;
        return _trackStream.Read(destination[..bytesToRead]);
    }

    public void Dispose() => _trackStream.Dispose();

    private static DataTrack FindFirstDataTrack(string cuePath)
    {
        var cueDirectory = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";
        string? currentFile = null;
        foreach (var rawLine in File.ReadLines(cuePath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
            {
                currentFile = ParseCueFileName(line);
                continue;
            }

            if (!line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase) || currentFile is null)
            {
                continue;
            }

            if (line.Contains("MODE1/2352", StringComparison.OrdinalIgnoreCase))
            {
                return new DataTrack(Path.Combine(cueDirectory, currentFile), Mode1UserDataOffset);
            }

            if (line.Contains("MODE2/2352", StringComparison.OrdinalIgnoreCase))
            {
                return new DataTrack(Path.Combine(cueDirectory, currentFile), Mode2UserDataOffset);
            }
        }

        throw new InvalidOperationException($"No supported 2352-byte data track found in '{cuePath}'.");
    }

    private static string ParseCueFileName(string line)
    {
        var firstQuote = line.IndexOf('"');
        if (firstQuote >= 0)
        {
            var secondQuote = line.IndexOf('"', firstQuote + 1);
            if (secondQuote > firstQuote)
            {
                return line[(firstQuote + 1)..secondQuote];
            }
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new InvalidOperationException($"Invalid CUE FILE line: '{line}'.");
        }

        return parts[1];
    }

    private readonly record struct DataTrack(string Path, int UserDataOffset);
}
