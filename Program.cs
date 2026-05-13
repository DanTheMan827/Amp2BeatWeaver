using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

if (args.Length < 2)
{
    PrintUsage();
    return 1;
}

string inputSongDirectory = Path.GetFullPath(args[0]);
string outputRootDirectory = Path.GetFullPath(args[1]);

if (!Directory.Exists(inputSongDirectory))
{
    Console.Error.WriteLine($"Input directory not found: {inputSongDirectory}");
    return 1;
}

string? songNameOverride = null;
string instrumentName = "guitar";
int transpose = -12;
Dictionary<int, int> noteMap = new();

for (int i = 2; i < args.Length; i++)
{
    string current = args[i];
    if (current.Equals("--name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        songNameOverride = args[++i];
    }
    else if (current.Equals("--instrument", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        instrumentName = args[++i];
    }
    else if (current.Equals("--transpose", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out transpose))
        {
            Console.Error.WriteLine("Invalid --transpose value. Expected an integer.");
            return 1;
        }
    }
    else if (current.Equals("--note-map", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        string mapText = args[++i];
        if (!TryParseNoteMap(mapText, noteMap))
        {
            Console.Error.WriteLine("Invalid --note-map value. Example: --note-map \"96:84,97:85\"");
            return 1;
        }
    }
    else
    {
        Console.Error.WriteLine($"Unknown or incomplete argument: {current}");
        PrintUsage();
        return 1;
    }
}

string? amplitudeMidi = Directory.EnumerateFiles(inputSongDirectory, "*.mid", SearchOption.TopDirectoryOnly).FirstOrDefault();
if (amplitudeMidi is null)
{
    Console.Error.WriteLine("No .mid file found in input directory.");
    return 1;
}

string? amplitudeMoggSong = Directory.EnumerateFiles(inputSongDirectory, "*.moggsong", SearchOption.TopDirectoryOnly).FirstOrDefault();
if (amplitudeMoggSong is null)
{
    Console.Error.WriteLine("No .moggsong file found in input directory.");
    return 1;
}

string? amplitudeMogg = Directory.EnumerateFiles(inputSongDirectory, "*.mogg", SearchOption.TopDirectoryOnly).FirstOrDefault();

Dictionary<string, string> moggSongValues = ParseMoggSong(amplitudeMoggSong);
string baseSongName = songNameOverride
    ?? GetFirstValue(moggSongValues, "short_title", "title", "name")
    ?? Path.GetFileNameWithoutExtension(amplitudeMidi);

string normalizedSongName = NormalizeName(baseSongName);
string normalizedInstrumentName = NormalizeName(instrumentName);

if (string.IsNullOrWhiteSpace(normalizedSongName))
{
    Console.Error.WriteLine("Unable to determine a valid output song name.");
    return 1;
}

string outputSongDirectory = Path.Combine(outputRootDirectory, normalizedSongName);
Directory.CreateDirectory(outputSongDirectory);

string outputMidi = Path.Combine(outputSongDirectory, $"{normalizedSongName}.mid");
ConvertMidi(amplitudeMidi, outputMidi, normalizedSongName, normalizedInstrumentName, transpose, noteMap);

if (amplitudeMogg is not null)
{
    string outputMogg = Path.Combine(outputSongDirectory, $"{normalizedSongName}.mogg");
    File.Copy(amplitudeMogg, outputMogg, overwrite: true);
}

string outputSongJson = Path.Combine(outputSongDirectory, $"{normalizedSongName}.json");
var beatWeaverSong = BuildBeatWeaverSong(moggSongValues, normalizedSongName, amplitudeMogg is not null);
File.WriteAllText(outputSongJson, JsonSerializer.Serialize(beatWeaverSong, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"Converted song directory created at: {outputSongDirectory}");
return 0;

static void PrintUsage()
{
    Console.WriteLine("Amp2BeatWeaver usage:");
    Console.WriteLine("  Amp2BeatWeaver <amplitude-song-directory> <output-root-directory> [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --name <name>             Override output song name before normalization.");
    Console.WriteLine("  --instrument <name>       MIDI instrument name (default: guitar).");
    Console.WriteLine("  --transpose <semitones>   Note transpose amount (default: -12).");
    Console.WriteLine("  --note-map <a:b,c:d>      Explicit note remap pairs (applied before transpose).");
}

static void ConvertMidi(
    string inputPath,
    string outputPath,
    string normalizedTrackName,
    string normalizedInstrumentName,
    int transpose,
    IReadOnlyDictionary<int, int> noteMap)
{
    var midi = MidiFile.Read(inputPath);

    foreach (TrackChunk trackChunk in midi.GetTrackChunks())
    {
        bool hasNotes = trackChunk.Events.Any(e => e is NoteOnEvent);
        if (!hasNotes)
        {
            continue;
        }

        for (int eventIndex = trackChunk.Events.Count - 1; eventIndex >= 0; eventIndex--)
        {
            if (trackChunk.Events[eventIndex] is SequenceTrackNameEvent or InstrumentNameEvent)
            {
                trackChunk.Events.RemoveAt(eventIndex);
            }
        }

        trackChunk.Events.Insert(0, new SequenceTrackNameEvent(normalizedTrackName));
        trackChunk.Events.Insert(1, new InstrumentNameEvent(normalizedInstrumentName));

        using var notesManager = trackChunk.ManageNotes();
        foreach (Note note in notesManager.Objects)
        {
            int originalNote = note.NoteNumber;
            int mappedNote = noteMap.TryGetValue(originalNote, out int explicitNote)
                ? explicitNote
                : originalNote + transpose;

            mappedNote = Math.Clamp(mappedNote, 0, 127);
            note.NoteNumber = (SevenBitNumber)mappedNote;
        }
    }

    midi.Write(outputPath, overwriteFile: true);
}

static bool TryParseNoteMap(string text, Dictionary<int, int> noteMap)
{
    foreach (string pairText in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        string[] pieces = pairText.Split(':', StringSplitOptions.TrimEntries);
        if (pieces.Length != 2
            || !int.TryParse(pieces[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int source)
            || !int.TryParse(pieces[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int target)
            || source < 0 || source > 127
            || target < 0 || target > 127)
        {
            return false;
        }

        noteMap[source] = target;
    }

    return true;
}

static Dictionary<string, string> ParseMoggSong(string moggSongPath)
{
    string text = File.ReadAllText(moggSongPath);
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    foreach (Match match in Regex.Matches(text, "\\((?<key>[a-zA-Z0-9_]+)\\s+\"(?<value>[^\"]*)\"\\)"))
    {
        values[match.Groups["key"].Value] = match.Groups["value"].Value;
    }

    foreach (Match match in Regex.Matches(text, "\\((?<key>[a-zA-Z0-9_]+)\\s+(?<value>[a-zA-Z0-9_./:-]+)\\)"))
    {
        if (!values.ContainsKey(match.Groups["key"].Value))
        {
            values[match.Groups["key"].Value] = match.Groups["value"].Value;
        }
    }

    return values;
}

static string? GetFirstValue(IReadOnlyDictionary<string, string> values, params string[] keys)
{
    foreach (string key in keys)
    {
        if (values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
    }

    return null;
}

static int ParseInt(IReadOnlyDictionary<string, string> values, int fallback, params string[] keys)
{
    string? value = GetFirstValue(values, keys);
    if (value is null)
    {
        return fallback;
    }

    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
        ? parsed
        : fallback;
}

static decimal? ParseDecimal(IReadOnlyDictionary<string, string> values, params string[] keys)
{
    string? value = GetFirstValue(values, keys);
    if (value is null)
    {
        return null;
    }

    return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)
        ? parsed
        : null;
}

static BeatWeaverSong BuildBeatWeaverSong(IReadOnlyDictionary<string, string> moggSongValues, string normalizedSongName, bool hasMogg)
{
    return new BeatWeaverSong
    {
        SongId = normalizedSongName,
        Title = GetFirstValue(moggSongValues, "title", "short_title", "name") ?? normalizedSongName,
        Artist = GetFirstValue(moggSongValues, "artist", "short_artist") ?? "unknown",
        Charter = GetFirstValue(moggSongValues, "charter"),
        Description = GetFirstValue(moggSongValues, "description"),
        PreviewStartMs = ParseInt(moggSongValues, 0, "preview_start_ms", "preview_start"),
        PreviewLengthMs = ParseInt(moggSongValues, 30000, "preview_length_ms", "preview_length"),
        Bpm = ParseDecimal(moggSongValues, "bpm"),
        AudioFile = hasMogg ? $"{normalizedSongName}.mogg" : null,
        MidiFile = $"{normalizedSongName}.mid"
    };
}

static string NormalizeName(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    var builder = new StringBuilder(value.Length);
    foreach (char ch in value.ToLowerInvariant())
    {
        if (char.IsLetterOrDigit(ch))
        {
            builder.Append(ch);
        }
        else if (char.IsWhiteSpace(ch) || ch is '-' or '_')
        {
            builder.Append('_');
        }
    }

    string normalized = Regex.Replace(builder.ToString(), "_+", "_").Trim('_');
    return normalized;
}

file sealed class BeatWeaverSong
{
    public required string SongId { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string? Charter { get; init; }
    public string? Description { get; init; }
    public int PreviewStartMs { get; init; }
    public int PreviewLengthMs { get; init; }
    public decimal? Bpm { get; init; }
    public string? AudioFile { get; init; }
    public required string MidiFile { get; init; }
}
