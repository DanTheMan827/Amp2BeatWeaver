using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

// ---- ENTRY POINT ----

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Amp2BeatWeaver <amplitude-song-directory> <output-root-directory>");
    return 1;
}

string inputSongDirectory  = Path.GetFullPath(args[0]);
string outputRootDirectory = Path.GetFullPath(args[1]);

if (!Directory.Exists(inputSongDirectory))
{
    Console.Error.WriteLine($"Input directory not found: {inputSongDirectory}");
    return 1;
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

// Parse moggsong
DtaArray moggSong = DtaParser.Parse(File.ReadAllText(amplitudeMoggSong));

// Song ID: derived from the moggsong filename
string normalizedSongId = NormalizeName(Path.GetFileNameWithoutExtension(amplitudeMoggSong));
if (string.IsNullOrWhiteSpace(normalizedSongId))
{
    Console.Error.WriteLine("Unable to determine a valid output song name from the .moggsong filename.");
    return 1;
}

// Metadata
string  title   = DtaHelper.GetString(moggSong, "title")  ?? normalizedSongId;
string  artist  = DtaHelper.GetString(moggSong, "artist") ?? "Unknown";
string? bio     = DtaHelper.GetString(moggSong, "desc");
string? charter = DtaHelper.GetString(moggSong, "charter");

// Tracks and mixing data
List<MoggTrack> moggTracks    = DtaHelper.GetTracks(moggSong);
List<double>    moggVolumes   = DtaHelper.GetFloatArray(moggSong, "vols");

// Create output directory
string outputSongDirectory = Path.Combine(outputRootDirectory, normalizedSongId);
Directory.CreateDirectory(outputSongDirectory);

// Convert MIDI
string outputMidi = Path.Combine(outputSongDirectory, $"{normalizedSongId}.mid");
ConvertMidi(amplitudeMidi, outputMidi, normalizedSongId, moggTracks);

// Copy mogg as ogg
if (amplitudeMogg is not null)
    File.Copy(amplitudeMogg, Path.Combine(outputSongDirectory, $"{normalizedSongId}.ogg"), overwrite: true);

// Per-track volumes (average across each track's channels)
List<double> trackVolumes = ComputeTrackVolumes(moggTracks, moggVolumes);
bool allZero = trackVolumes.All(v => v == 0.0);

// Write BeatWeaver JSON
var beatWeaverSong = new BeatWeaverSong
{
    Metadata = new BeatWeaverMetadata
    {
        Title  = title,
        Artist = artist,
        Bio    = string.IsNullOrWhiteSpace(bio)     ? null : bio,
        Chart  = string.IsNullOrWhiteSpace(charter) ? null : charter,
    },
    Audio = new BeatWeaverAudio
    {
        Channels         = moggTracks.Select(t => t.Channels).ToList(),
        Volume           = allZero ? null : trackVolumes,
        OutroTracks      = Enumerable.Range(0, moggTracks.Count).ToList(),
        TransitionTracks = new List<int>(),
    },
};

File.WriteAllText(
    Path.Combine(outputSongDirectory, $"{normalizedSongId}.json"),
    JsonSerializer.Serialize(beatWeaverSong, new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    }));

Console.WriteLine($"Converted: {outputSongDirectory}");
return 0;

// ---- HELPERS ----

static string NormalizeName(string value)
{
    if (string.IsNullOrWhiteSpace(value)) return string.Empty;
    var sb = new StringBuilder(value.Length);
    foreach (char ch in value.ToLowerInvariant())
    {
        if (char.IsLetterOrDigit(ch))
            sb.Append(ch);
        else if (char.IsWhiteSpace(ch) || ch is '-' or '_')
            sb.Append('_');
    }
    return Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
}

/// <summary>
/// Strip trailing digits from a track name to recover the base instrument type.
/// e.g. "synth2" → "synth", "bass3" → "bass", "guitar" → "guitar"
/// </summary>
static string BaseInstrumentName(string trackName) =>
    Regex.Replace(trackName, @"\d+$", "");

static void ConvertMidi(
    string inputPath,
    string outputPath,
    string songName,
    List<MoggTrack> moggTracks)
{
    // Explicit note mapping: Amplitude → BeatWeaver
    //   Amplitude: Easy  Left=96  Middle=98  Right=100
    //              Medium Left=102 Middle=104 Right=106
    //              Hard   Left=108 Middle=110 Right=112
    //              Expert Left=114 Middle=116 Right=118
    //   BeatWeaver: Each difficulty has 4 positions (outer-left, inner-left, inner-right, outer-right)
    //               Amplitude lanes map to positions 1-3 (inner-left, inner-right, outer-right),
    //               leaving outer-left unused.
    //   Easy:   C1(24) C#1(25) D1(26) D#1(27)
    //   Medium: C2(36) C#2(37) D2(38) D#2(39)
    //   Hard:   C3(48) C#3(49) D3(50) D#3(51)
    //   Expert: C4(60) C#4(61) D4(62) D#4(63)
    var noteMap = new Dictionary<int, int>
    {
        // Easy
        { 96,  25 }, { 98,  26 }, { 100, 27 },
        // Medium
        { 102, 37 }, { 104, 38 }, { 106, 39 },
        // Hard
        { 108, 49 }, { 110, 50 }, { 112, 51 },
        // Expert
        { 114, 61 }, { 116, 62 }, { 118, 63 },
    };

    // Build unique track names: if a name already appears earlier we append an
    // incrementing counter.  Names that come pre-numbered from the moggsong
    // (synth2, synth3 …) are treated as fully distinct and pass through unchanged
    // unless they collide with another entry.
    var seen    = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var uniqueNames = new List<string>();
    foreach (var track in moggTracks)
    {
        string name = NormalizeName(track.Name);
        if (!seen.TryGetValue(name, out int count))
        {
            seen[name] = 1;
            uniqueNames.Add(name);
        }
        else
        {
            count++;
            seen[name] = count;
            uniqueNames.Add($"{name}{count}");
        }
    }

    var midi = MidiFile.Read(inputPath);

    var instrumentTracks = midi.GetTrackChunks()
        .Where(t => t.Events.Any(e => e is NoteOnEvent))
        .ToList();

    for (int i = 0; i < instrumentTracks.Count; i++)
    {
        var track = instrumentTracks[i];

        // Unique display name for this track
        string uniqueName  = i < uniqueNames.Count ? uniqueNames[i] : $"{songName}{i + 1}";

        // Instrument type (base name without trailing digits)
        string instrument  = i < moggTracks.Count
            ? NormalizeName(BaseInstrumentName(moggTracks[i].Name))
            : uniqueName;

        // Replace any existing name / instrument meta events
        for (int j = track.Events.Count - 1; j >= 0; j--)
        {
            if (track.Events[j] is SequenceTrackNameEvent or InstrumentNameEvent)
                track.Events.RemoveAt(j);
        }

        track.Events.Insert(0, new SequenceTrackNameEvent(uniqueName));
        track.Events.Insert(1, new InstrumentNameEvent(instrument));

        // Remap notes
        using var mgr = track.ManageNotes();
        foreach (Note note in mgr.Objects)
        {
            if (noteMap.TryGetValue(note.NoteNumber, out int mapped))
                note.NoteNumber = (SevenBitNumber)mapped;
        }
    }

    midi.Write(outputPath, overwriteFile: true);
}

static List<double> ComputeTrackVolumes(List<MoggTrack> moggTracks, List<double> channelVolumes)
{
    return moggTracks.Select(track =>
    {
        var validChs = track.Channels.Where(c => c < channelVolumes.Count).ToList();
        return validChs.Count > 0 ? validChs.Average(c => channelVolumes[c]) : 0.0;
    }).ToList();
}

// ---- DTA NODE TYPES ----

abstract class DtaNode { }

sealed class DtaAtom : DtaNode
{
    public string Value    { get; init; } = "";
    public bool   IsString { get; init; }
}

sealed class DtaArray : DtaNode
{
    public List<DtaNode> Children { get; } = new();
}

// ---- DTA PARSER ----

static class DtaParser
{
    public static DtaArray Parse(string text)
    {
        int pos  = 0;
        var root = new DtaArray();
        while (pos < text.Length)
        {
            SkipJunk(text, ref pos);
            if (pos >= text.Length) break;
            var node = ParseNode(text, ref pos);
            if (node is not null) root.Children.Add(node);
        }
        return root;
    }

    private static DtaNode? ParseNode(string text, ref int pos)
    {
        SkipJunk(text, ref pos);
        if (pos >= text.Length) return null;

        char c = text[pos];

        if (c == '(')
        {
            pos++;
            var arr = new DtaArray();
            while (pos < text.Length && text[pos] != ')')
            {
                SkipJunk(text, ref pos);
                if (pos < text.Length && text[pos] == ')') break;
                var child = ParseNode(text, ref pos);
                if (child is not null) arr.Children.Add(child);
            }
            if (pos < text.Length) pos++; // consume ')'
            return arr;
        }

        if (c == '"')
        {
            pos++;
            var sb = new StringBuilder();
            while (pos < text.Length && text[pos] != '"')
            {
                if (text[pos] == '\\' && pos + 1 < text.Length) { pos++; sb.Append(text[pos]); }
                else sb.Append(text[pos]);
                pos++;
            }
            if (pos < text.Length) pos++; // consume closing '"'
            return new DtaAtom { Value = sb.ToString(), IsString = true };
        }

        if (c == ')')
        {
            // Unexpected close paren – skip it and let the caller handle
            pos++;
            return null;
        }

        // Unquoted atom (symbol, number, path, …)
        {
            var sb = new StringBuilder();
            while (pos < text.Length
                   && !char.IsWhiteSpace(text[pos])
                   && text[pos] != '(' && text[pos] != ')'
                   && text[pos] != '"' && text[pos] != ';')
            {
                sb.Append(text[pos]);
                pos++;
            }
            return sb.Length > 0 ? new DtaAtom { Value = sb.ToString(), IsString = false } : null;
        }
    }

    private static void SkipJunk(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            if (char.IsWhiteSpace(text[pos])) { pos++; continue; }
            if (text[pos] == ';') { while (pos < text.Length && text[pos] != '\n') pos++; continue; }
            break;
        }
    }
}

// ---- DTA HELPER ----

static class DtaHelper
{
    /// <summary>
    /// Returns the string value of the second child of the first top-level array
    /// whose first child is an atom matching <paramref name="key"/>.
    /// </summary>
    public static string? GetString(DtaArray root, string key)
    {
        foreach (var node in FindByKey(root, key))
        {
            if (node.Children.Count >= 2 && node.Children[1] is DtaAtom atom)
                return atom.Value;
        }
        return null;
    }

    /// <summary>
    /// Returns each float value in the second child (a sub-array) of the first
    /// top-level array whose first child matches <paramref name="key"/>.
    /// </summary>
    public static List<double> GetFloatArray(DtaArray root, string key)
    {
        foreach (var node in FindByKey(root, key))
        {
            if (node.Children.Count >= 2 && node.Children[1] is DtaArray inner)
            {
                return inner.Children
                    .OfType<DtaAtom>()
                    .Select(a => double.TryParse(
                        a.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out double d) ? d : 0.0)
                    .ToList();
            }
        }
        return new List<double>();
    }

    /// <summary>
    /// Parses the track list from an Amplitude moggsong DtaArray.
    ///
    /// Expected moggsong structure:
    /// <code>
    /// (tracks
    ///   (
    ///     (trackName (ch1 ch2) optionalEvent)
    ///     ...
    ///   )
    /// )
    /// </code>
    /// </summary>
    public static List<MoggTrack> GetTracks(DtaArray root)
    {
        foreach (var tracksNode in FindByKey(root, "tracks"))
        {
            if (tracksNode.Children.Count < 2) continue;

            // Second child is the wrapping array that contains individual track arrays
            if (tracksNode.Children[1] is not DtaArray trackList) continue;

            var result = new List<MoggTrack>();
            foreach (var child in trackList.Children)
            {
                if (child is not DtaArray trackArr || trackArr.Children.Count < 1) continue;

                string name = (trackArr.Children[0] as DtaAtom)?.Value ?? "unknown";

                var channels = new List<int>();
                if (trackArr.Children.Count >= 2 && trackArr.Children[1] is DtaArray chArr)
                {
                    foreach (var chanNode in chArr.Children.OfType<DtaAtom>())
                    {
                        if (int.TryParse(chanNode.Value, out int ch))
                            channels.Add(ch);
                    }
                }

                result.Add(new MoggTrack(name, channels));
            }
            return result;
        }
        return new List<MoggTrack>();
    }

    private static IEnumerable<DtaArray> FindByKey(DtaArray root, string key)
    {
        foreach (var child in root.Children)
        {
            if (child is DtaArray arr
                && arr.Children.Count >= 1
                && arr.Children[0] is DtaAtom atom
                && atom.Value.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                yield return arr;
            }
        }
    }
}

// ---- DOMAIN TYPES ----

record MoggTrack(string Name, List<int> Channels);

sealed class BeatWeaverSong
{
    [JsonPropertyName("metadata")]
    public BeatWeaverMetadata Metadata { get; init; } = null!;

    [JsonPropertyName("audio")]
    public BeatWeaverAudio Audio { get; init; } = null!;
}

sealed class BeatWeaverMetadata
{
    [JsonPropertyName("artist")]
    public required string Artist { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("bio")]
    public string? Bio { get; init; }

    [JsonPropertyName("chart")]
    public string? Chart { get; init; }
}

sealed class BeatWeaverAudio
{
    [JsonPropertyName("channels")]
    public List<List<int>> Channels { get; init; } = null!;

    [JsonPropertyName("volume")]
    public List<double>? Volume { get; init; }

    [JsonPropertyName("outro_tracks")]
    public List<int> OutroTracks { get; init; } = null!;

    [JsonPropertyName("transition_tracks")]
    public List<int> TransitionTracks { get; init; } = null!;
}
