using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DtxCS;
using DtxCS.DataTypes;
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

// Parse moggsong via DtxCS
DataArray moggSong = DTX.FromDtaString(File.ReadAllText(amplitudeMoggSong));

// Song ID: derived from the moggsong filename
string normalizedSongId = NormalizeName(Path.GetFileNameWithoutExtension(amplitudeMoggSong));
if (string.IsNullOrWhiteSpace(normalizedSongId))
{
    Console.Error.WriteLine("Unable to determine a valid output song name from the .moggsong filename.");
    return 1;
}

// Metadata
string  title   = moggSong.Array("title")?.Any(1)   ?? normalizedSongId;
string  artist  = moggSong.Array("artist")?.Any(1)  ?? "Unknown";
string? bio     = moggSong.Array("desc")?.Any(1);
string? charter = moggSong.Array("charter")?.Any(1);

// Tracks and per-channel volumes
List<MoggTrack> moggTracks  = GetMoggTracks(moggSong);
List<float>     moggVolumes = GetFloatArray(moggSong, "vols");

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
/// Strip trailing digits to recover a base instrument type name.
/// e.g. "synth2" -> "synth", "bass3" -> "bass", "guitar" -> "guitar"
/// </summary>
static string BaseInstrumentName(string trackName) =>
    Regex.Replace(trackName, @"\d+$", "");

/// <summary>
/// Parses the track list from an Amplitude moggsong DataArray.
///
/// Moggsong structure:
///   (tracks
///     (
///       (trackName (ch1 ch2) optionalEvent)
///       ...
///     )
///   )
/// </summary>
static List<MoggTrack> GetMoggTracks(DataArray root)
{
    var tracksNode = root.Array("tracks");
    if (tracksNode is null || tracksNode.Children.Count < 2) return new List<MoggTrack>();

    // Children[1] is the wrapping DataArray that contains individual track arrays
    if (tracksNode.Children[1] is not DataArray trackList) return new List<MoggTrack>();

    var result = new List<MoggTrack>();
    foreach (var child in trackList.Children)
    {
        if (child is not DataArray trackArr || trackArr.Children.Count < 1) continue;

        string name = trackArr.Name; // first child's Name = track identifier

        var channels = new List<int>();
        if (trackArr.Children.Count >= 2 && trackArr.Children[1] is DataArray chArr)
        {
            for (int i = 0; i < chArr.Children.Count; i++)
            {
                try { channels.Add(chArr.Int(i)); }
                catch { /* skip non-integer children */ }
            }
        }

        result.Add(new MoggTrack(name, channels));
    }
    return result;
}

/// <summary>Returns each float/int value from the inner array of a (key (v1 v2 ...)) node.</summary>
static List<float> GetFloatArray(DataArray root, string key)
{
    var node = root.Array(key);
    if (node is null || node.Children.Count < 2) return new List<float>();
    if (node.Children[1] is not DataArray inner)  return new List<float>();

    var result = new List<float>();
    for (int i = 0; i < inner.Children.Count; i++)
    {
        try { result.Add(inner.Number(i)); }
        catch { result.Add(0f); }
    }
    return result;
}

static void ConvertMidi(
    string inputPath,
    string outputPath,
    string songName,
    List<MoggTrack> moggTracks)
{
    // Explicit note mapping: Amplitude -> BeatWeaver
    //
    // Amplitude has 3 lanes per difficulty; BeatWeaver has 4.
    // Amplitude lanes map to BeatWeaver positions 1-3 (inner-left, inner-right,
    // outer-right), leaving outer-left (position 0) unused.
    //
    // Amplitude:  Easy   Left=96  Middle=98  Right=100
    //             Medium Left=102 Middle=104 Right=106
    //             Hard   Left=108 Middle=110 Right=112
    //             Expert Left=114 Middle=116 Right=118
    //
    // BeatWeaver: Easy   C1(24)  C#1(25) D1(26)  D#1(27)
    //             Medium C2(36)  C#2(37) D2(38)  D#2(39)
    //             Hard   C3(48)  C#3(49) D3(50)  D#3(51)
    //             Expert C4(60)  C#4(61) D4(62)  D#4(63)
    var noteMap = new Dictionary<int, int>
    {
        // Easy
        {  96, 25 }, {  98, 26 }, { 100, 27 },
        // Medium
        { 102, 37 }, { 104, 38 }, { 106, 39 },
        // Hard
        { 108, 49 }, { 110, 50 }, { 112, 51 },
        // Expert
        { 114, 61 }, { 116, 62 }, { 118, 63 },
    };

    // Build unique track names.
    // Names that are already distinct in the moggsong (e.g. synth, synth2,
    // synth3) pass through unchanged. If two tracks share the exact same name,
    // a counter suffix is appended to the second and beyond.
    var seen        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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

    var midi = MidiFile.Read(inputPath, new ReadingSettings
    {
        InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.ReadValid,
        InvalidMetaEventParameterValuePolicy    = InvalidMetaEventParameterValuePolicy.SnapToLimits,
        NotEnoughBytesPolicy                    = NotEnoughBytesPolicy.Ignore,
        UnknownChannelEventPolicy               = UnknownChannelEventPolicy.SkipStatusByteAndOneDataByte,
    });

    var instrumentTracks = midi.GetTrackChunks()
        .Where(t => t.Events.Any(e => e is NoteOnEvent))
        .ToList();

    for (int i = 0; i < instrumentTracks.Count; i++)
    {
        var track = instrumentTracks[i];

        // Unique display name for this MIDI track
        string uniqueName = i < uniqueNames.Count ? uniqueNames[i] : $"{songName}{i + 1}";

        // Instrument type: base name with trailing digits stripped
        string instrument = i < moggTracks.Count
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

static List<double> ComputeTrackVolumes(List<MoggTrack> moggTracks, List<float> channelVolumes)
{
    return moggTracks.Select(track =>
    {
        var validChs = track.Channels.Where(c => c < channelVolumes.Count).ToList();
        return validChs.Count > 0 ? (double)validChs.Average(c => channelVolumes[c]) : 0.0;
    }).ToList();
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
