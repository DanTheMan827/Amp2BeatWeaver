using System.IO.Compression;

internal static partial class App
{
static string ResolveInputSongDirectory(string inputPath, out string? temporaryInputDirectory)
{
    temporaryInputDirectory = null;

    if (Directory.Exists(inputPath))
    {
        return ResolveSongDirectoryFromDirectory(inputPath);
    }

    if (!File.Exists(inputPath))
    {
        throw new FileNotFoundException($"Input path not found: {inputPath}");
    }

    string extension = Path.GetExtension(inputPath).ToLowerInvariant();
    return extension switch
    {
        ".moggsong" => ResolveSongDirectoryFromDirectory(Path.GetDirectoryName(inputPath) ?? throw new InvalidOperationException("Unable to determine input directory.")),
        ".zip" => ResolveSongDirectoryFromZip(inputPath, out temporaryInputDirectory),
        _ => throw new InvalidOperationException("Input must be a song folder, .moggsong file, or .zip file."),
    };
}

static string ResolveSongDirectoryFromZip(string zipPath, out string temporaryInputDirectory)
{
    temporaryInputDirectory = Path.Combine(Path.GetTempPath(), $"{TemporaryExtractionDirectoryPrefix}{Guid.NewGuid():N}");
    ZipFile.ExtractToDirectory(zipPath, temporaryInputDirectory);
    return ResolveSongDirectoryFromDirectory(temporaryInputDirectory);
}

static string ResolveSongDirectoryFromDirectory(string inputDirectory)
{
    var directMoggSongs = Directory.EnumerateFiles(inputDirectory, "*.moggsong", SearchOption.TopDirectoryOnly).ToList();
    if (directMoggSongs.Count == 1)
    {
        return inputDirectory;
    }

    if (directMoggSongs.Count > 1)
    {
        throw new InvalidOperationException($"Multiple .moggsong files found in input directory: {inputDirectory}");
    }

    var recursiveMoggSongs = Directory.EnumerateFiles(inputDirectory, "*.moggsong", SearchOption.AllDirectories).ToList();
    if (recursiveMoggSongs.Count == 1)
    {
        return Path.GetDirectoryName(recursiveMoggSongs[0]) ?? throw new InvalidOperationException("Unable to determine extracted song directory.");
    }

    if (recursiveMoggSongs.Count == 0)
    {
        throw new InvalidOperationException($"No .moggsong file found under input path: {inputDirectory}");
    }

    throw new InvalidOperationException($"Multiple .moggsong files found under input path: {inputDirectory}");
}
}
