namespace SystemRegisIII.Core.Core.CdBlock;

public sealed class CueDiscImage : IDiscImage, IDiscTableOfContents, IDisposable
{
    private const int RawSectorSize = 2352;
    private const int Mode1UserDataOffset = 16;
    private const int Mode2UserDataOffset = 24;
    private readonly FileStream _trackStream;
    private readonly int _userDataOffset;

    public CueDiscImage(string cuePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cuePath);

        var parsedCue = ParseCue(cuePath);
        var dataTrack = parsedCue.DataTrack;
        _trackStream = File.Open(dataTrack.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _userDataOffset = dataTrack.UserDataOffset;
        Tracks = parsedCue.Tracks;
        LeadoutFad = parsedCue.LeadoutFad;
        DiscType = parsedCue.DiscType;
        Name = Path.GetFileName(cuePath);
    }

    public string Name { get; }
    public long LengthBytes => SectorCount * SectorSize;
    public int SectorSize => RawDiscImage.DefaultSectorSize;
    public long SectorCount => _trackStream.Length / RawSectorSize;
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

        var bytesToRead = Math.Min(destination.Length, SectorSize);
        _trackStream.Position = logicalBlockAddress * RawSectorSize + _userDataOffset;
        return _trackStream.Read(destination[..bytesToRead]);
    }

    public void Dispose() => _trackStream.Dispose();

    private static ParsedCue ParseCue(string cuePath)
    {
        var cueDirectory = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";
        string? currentFile = null;
        byte currentTrackNumber = 0;
        byte currentControlAdr = 0;
        DataTrack? dataTrack = null;
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
                if (dataTrack is null && line.Contains("MODE1/2352", StringComparison.OrdinalIgnoreCase))
                {
                    dataTrack = new DataTrack(Path.Combine(cueDirectory, currentFile), Mode1UserDataOffset);
                }
                else if (dataTrack is null && line.Contains("MODE2/2352", StringComparison.OrdinalIgnoreCase))
                {
                    dataTrack = new DataTrack(Path.Combine(cueDirectory, currentFile), Mode2UserDataOffset);
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
                    ParseMsf(line[9..])));
            }
        }

        if (dataTrack is null)
        {
            throw new InvalidOperationException($"No supported 2352-byte data track found in '{cuePath}'.");
        }

        var fileBases = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        uint nextFileBase = 0;
        foreach (var track in parsedTracks)
        {
            if (!fileBases.ContainsKey(track.Path))
            {
                fileBases[track.Path] = nextFileBase;
                nextFileBase += (uint)(new FileInfo(track.Path).Length / RawSectorSize);
            }
        }

        var tracks = parsedTracks
            .Select(track => new CdTrackInfo(track.Number, track.ControlAdr, 150 + fileBases[track.Path] + track.IndexSectors))
            .ToArray();
        return new ParsedCue(dataTrack.Value, tracks, 150 + nextFileBase, discType);
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

    private readonly record struct DataTrack(string Path, int UserDataOffset);
    private readonly record struct ParsedTrack(byte Number, byte ControlAdr, string Path, uint IndexSectors);
    private readonly record struct ParsedCue(DataTrack DataTrack, IReadOnlyList<CdTrackInfo> Tracks, uint LeadoutFad, byte DiscType);
}
