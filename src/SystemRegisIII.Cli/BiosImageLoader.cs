using System.IO.Compression;
using SystemRegisIII.Core.Core.Memory;

namespace SystemRegisIII.Cli;

internal static class BiosImageLoader
{
    private static readonly string[] SupportedExtensions = [".bin", ".rom", ".zip"];

    public static BiosImage Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("BIOS file was not found.", path);
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".zip" => LoadZip(path),
            ".bin" or ".rom" => new BiosImage(Path.GetFileName(path), File.ReadAllBytes(path)),
            _ => throw new NotSupportedException(
                $"Unsupported BIOS extension '{extension}'. Supported: {string.Join(", ", SupportedExtensions)}."),
        };
    }

    private static BiosImage LoadZip(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.Entries
            .Where(static candidate => candidate.Length > 0)
            .Where(static candidate => IsLikelyBiosEntry(candidate.FullName))
            .OrderByDescending(static candidate => candidate.Length)
            .FirstOrDefault();

        if (entry is null)
        {
            throw new InvalidOperationException($"No .bin or .rom BIOS payload was found in '{path}'.");
        }

        using var source = entry.Open();
        using var destination = new MemoryStream((int)entry.Length);
        source.CopyTo(destination);
        return new BiosImage($"{Path.GetFileName(path)}:{entry.FullName}", destination.ToArray());
    }

    private static bool IsLikelyBiosEntry(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension is ".bin" or ".rom";
    }
}
