namespace SystemRegisIII.Core.Core.CdBlock;

public sealed class CueDiscImage : IDiscImage, IDiscTableOfContents, IDisposable
{
    private const int RawSectorSize = 2352;
    private const int Mode1UserDataOffset = 16;
    private const int Mode2UserDataOffset = 24;
    private readonly CueFileStream[] _files;

    public CueDiscImage(string cuePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cuePath);

        var parsedCue = ParseCue(cuePath);
        _files = new CueFileStream[parsedCue.Files.Count];
        try
        {
            for (var index = 0; index < parsedCue.Files.Count; index++)
            {
                var file = parsedCue.Files[index];
                _files[index] = new CueFileStream(
                    File.Open(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read),
                    file.BaseSector,
                    file.SectorCount,
                    file.Tracks);
            }
        }
        catch
        {
            foreach (var file in _files)
            {
                file?.Stream.Dispose();
            }

            throw;
        }

        Tracks = parsedCue.Tracks;
        LeadoutFad = parsedCue.LeadoutFad;
        DiscType = parsedCue.DiscType;
        Name = Path.GetFileName(cuePath);
    }

    public string Name { get; }
    public long LengthBytes => SectorCount * SectorSize;
    public int SectorSize => RawDiscImage.DefaultSectorSize;
    public long SectorCount => LeadoutFad - 150;
    public IReadOnlyList<CdTrackInfo> Tracks { get; }
    public uint LeadoutFad { get; }
    public byte DiscType { get; }

    public int ReadSector(long logicalBlockAddress, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(logicalBlockAddress);
        if (logicalBlockAddress >= SectorCount)
        {
            return 0;
        }

        foreach (var file in _files)
        {
            if (logicalBlockAddress < file.BaseSector
                || logicalBlockAddress >= file.BaseSector + file.SectorCount)
            {
                continue;
            }

            var fileSector = checked((uint)(logicalBlockAddress - file.BaseSector));
            var userDataOffset = file.Tracks[0].UserDataOffset;
            foreach (var track in file.Tracks)
            {
                if (track.IndexSectors > fileSector)
                {
                    break;
                }

                userDataOffset = track.UserDataOffset;
            }

            var bytesToRead = Math.Min(destination.Length, SectorSize);
            file.Stream.Position = (long)fileSector * RawSectorSize + userDataOffset;
            return file.Stream.Read(destination[..bytesToRead]);
        }

        return 0;
    }

    public void Dispose()
    {
        foreach (var file in _files)
        {
            file.Stream.Dispose();
        }
    }

    private static ParsedCue ParseCue(string cuePath)
    {
        var cueDirectory = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";
        string? currentFile = null;
        byte currentTrackNumber = 0;
        byte currentControlAdr = 0;
        int currentUserDataOffset = 0;
        var hasDataTrack = false;
        var parsedTracks = new List<ParsedTrack>();
        byte discType = 0;
        foreach (var rawLine in File.ReadLines(cuePath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
            {
                currentFile = ParseCueFileName(line);
                continue;
            }

            if (line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase) && currentFile is not null)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                currentTrackNumber = byte.Parse(parts[1]);
                currentControlAdr = line.Contains("AUDIO", StringComparison.OrdinalIgnoreCase) ? (byte)0x01 : (byte)0x41;
                if (line.Contains("MODE1/2352", StringComparison.OrdinalIgnoreCase))
                {
                    currentUserDataOffset = Mode1UserDataOffset;
                    hasDataTrack = true;
                }
                else if (line.Contains("MODE2/2352", StringComparison.OrdinalIgnoreCase))
                {
                    currentUserDataOffset = Mode2UserDataOffset;
                    hasDataTrack = true;
                }
                else
                {
                    currentUserDataOffset = 0;
                }

                if (line.Contains("MODE2/2352", StringComparison.OrdinalIgnoreCase))
                {
                    discType = 0x20;
                }

                continue;
            }

            if (line.StartsWith("INDEX 01 ", StringComparison.OrdinalIgnoreCase) && currentFile is not null)
            {
                parsedTracks.Add(new ParsedTrack(
                    currentTrackNumber,
                    currentControlAdr,
                    Path.Combine(cueDirectory, currentFile),
                    ParseMsf(line[9..]),
                    currentUserDataOffset));
            }
        }

        if (!hasDataTrack)
        {
            throw new InvalidOperationException($"No supported 2352-byte data track found in '{cuePath}'.");
        }

        var fileBases = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var parsedFiles = new List<ParsedFile>();
        uint nextFileBase = 0;
        foreach (var track in parsedTracks)
        {
            if (!fileBases.ContainsKey(track.Path))
            {
                fileBases[track.Path] = nextFileBase;
                var sectorCount = (uint)(new FileInfo(track.Path).Length / RawSectorSize);
                parsedFiles.Add(new ParsedFile(
                    track.Path,
                    nextFileBase,
                    sectorCount,
                    parsedTracks.Where(candidate => string.Equals(candidate.Path, track.Path, StringComparison.OrdinalIgnoreCase)).ToArray()));
                nextFileBase += sectorCount;
            }
        }

        var tracks = parsedTracks
            .Select(track => new CdTrackInfo(track.Number, track.ControlAdr, 150 + fileBases[track.Path] + track.IndexSectors))
            .ToArray();
        return new ParsedCue(parsedFiles, tracks, 150 + nextFileBase, discType);
    }

    private static uint ParseMsf(string value)
    {
        var parts = value.Split(':');
        return checked(uint.Parse(parts[0]) * 60 * 75 + uint.Parse(parts[1]) * 75 + uint.Parse(parts[2]));
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

    private sealed record CueFileStream(
        FileStream Stream,
        uint BaseSector,
        uint SectorCount,
        IReadOnlyList<ParsedTrack> Tracks);

    private readonly record struct ParsedTrack(
        byte Number,
        byte ControlAdr,
        string Path,
        uint IndexSectors,
        int UserDataOffset);

    private readonly record struct ParsedFile(
        string Path,
        uint BaseSector,
        uint SectorCount,
        IReadOnlyList<ParsedTrack> Tracks);

    private readonly record struct ParsedCue(
        IReadOnlyList<ParsedFile> Files,
        IReadOnlyList<CdTrackInfo> Tracks,
        uint LeadoutFad,
        byte DiscType);
}
