using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DtxCS;
using DtxCS.DataTypes;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

const string TemporaryExtractionPrefix = "amp2beatweaver-";

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Amp2BeatWeaver <amplitude-input> <output-root-directory>");
    Console.Error.WriteLine("  <amplitude-input> can be a song folder, a .moggsong file, or a .zip file.");
    return 1;
}

string inputPath = Path.GetFullPath(args[0]);
string outputRootDirectory = Path.GetFullPath(args[1]);
string? temporaryInputDirectory = null;

try
{
    string inputSongDirectory = ResolveInputSongDirectory(inputPath, out temporaryInputDirectory);

    string? amplitudeMidi = Directory.EnumerateFiles(inputSongDirectory, "*.mid", SearchOption.TopDirectoryOnly).FirstOrDefault();
    if (amplitudeMidi is null)
    {
        Console.Error.WriteLine($"No .mid file found in input directory: {inputSongDirectory}");
        return 1;
    }

    string? amplitudeMoggSong = Directory.EnumerateFiles(inputSongDirectory, "*.moggsong", SearchOption.TopDirectoryOnly).FirstOrDefault();
    if (amplitudeMoggSong is null)
    {
        Console.Error.WriteLine($"No .moggsong file found in input directory: {inputSongDirectory}");
        return 1;
    }

    string? amplitudeMogg = Directory.EnumerateFiles(inputSongDirectory, "*.mogg", SearchOption.TopDirectoryOnly).FirstOrDefault();

    DataArray moggSong = DTX.FromDtaString(File.ReadAllText(amplitudeMoggSong));

    string normalizedSongId = NormalizeName(Path.GetFileNameWithoutExtension(amplitudeMoggSong));
    if (string.IsNullOrWhiteSpace(normalizedSongId))
    {
        Console.Error.WriteLine("Unable to determine a valid output song name from the .moggsong filename.");
        return 1;
    }

    string title = moggSong.Array("title")?.Any(1) ?? normalizedSongId;
    string artist = moggSong.Array("artist")?.Any(1) ?? "Unknown";
    string? bio = moggSong.Array("desc")?.Any(1);
    string? chart = moggSong.Array("charter")?.Any(1);
    string? songEndPosition = moggSong.Array("song_info")?.Array("length")?.Any(1);

    List<MoggTrack> moggTracks = GetMoggTracks(moggSong);
    List<float> moggVolumes = GetFloatArray(moggSong, "vols");

    string outputSongDirectory = Path.Combine(outputRootDirectory, normalizedSongId);
    Directory.CreateDirectory(outputSongDirectory);

    string outputMidi = Path.Combine(outputSongDirectory, $"{normalizedSongId}.mid");
    int playableTrackCount = ConvertMidi(amplitudeMidi, outputMidi, normalizedSongId, moggTracks, songEndPosition);
    List<MoggTrack> playableTracks = moggTracks.Take(playableTrackCount).ToList();

    if (amplitudeMogg is not null)
    {
        File.Copy(amplitudeMogg, Path.Combine(outputSongDirectory, $"{normalizedSongId}.ogg"), overwrite: true);
    }

    List<double> trackVolumes = ComputeTrackVolumes(playableTracks, moggVolumes);
    bool allZero = trackVolumes.All(v => v == 0.0);

    var beatWeaverSong = new BeatWeaverSong
    {
        Metadata = new BeatWeaverMetadata
        {
            Title = title,
            Artist = artist,
            Bio = string.IsNullOrWhiteSpace(bio) ? null : bio,
            Chart = string.IsNullOrWhiteSpace(chart) ? null : chart,
        },
        Audio = new BeatWeaverAudio
        {
            Channels = playableTracks.Select(track => track.Channels).ToList(),
            Volume = allZero ? null : trackVolumes,
            OutroTracks = Enumerable.Range(0, playableTracks.Count).ToList(),
            TransitionTracks = GetTransitionTracks(playableTracks),
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
}
finally
{
    if (!string.IsNullOrEmpty(temporaryInputDirectory) && Directory.Exists(temporaryInputDirectory))
    {
        Directory.Delete(temporaryInputDirectory, recursive: true);
    }
}

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
    temporaryInputDirectory = Path.Combine(Path.GetTempPath(), $"{TemporaryExtractionPrefix}{Guid.NewGuid():N}");
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

    return RegexHelpers.RepeatedUnderscores.Replace(builder.ToString(), "_").Trim('_');
}

static string BaseInstrumentName(string trackName) => RegexHelpers.TrailingDigits.Replace(trackName, "");

static string NormalizeInstrumentName(string trackName)
{
    string normalized = NormalizeName(BaseInstrumentName(trackName));
    string mapped = TryMapInstrumentToken(normalized);
    if (!string.IsNullOrEmpty(mapped))
    {
        return mapped;
    }

    foreach (string token in normalized.Split('_', StringSplitOptions.RemoveEmptyEntries))
    {
        mapped = TryMapInstrumentToken(token);
        if (!string.IsNullOrEmpty(mapped))
        {
            return mapped;
        }
    }

    return "fx";
}

static string TryMapInstrumentToken(string token)
{
    return token switch
    {
        "drums" => "drums",
        "bass" => "bass",
        "guitar" => "guitar",
        "synth" => "synth",
        "vocals" => "vocals",
        "fx" => "fx",
        "strings" => "synth",
        "piano" => "guitar",
        "vox" => "vocals",
        "perc" => "fx",
        "freestyle" => "fx",
        "bg_click" => "fx",
        _ => string.Empty,
    };
}

static List<MoggTrack> GetMoggTracks(DataArray root)
{
    var tracksNode = root.Array("tracks");
    if (tracksNode is null || tracksNode.Children.Count < 2 || tracksNode.Children[1] is not DataArray trackList)
    {
        return new List<MoggTrack>();
    }

    var result = new List<MoggTrack>();
    foreach (DataNode child in trackList.Children)
    {
        if (child is not DataArray trackArray || trackArray.Children.Count < 2 || trackArray.Children[1] is not DataArray channelArray)
        {
            continue;
        }

        string name = trackArray.Name;
        var channels = new List<int>();
        for (int index = 0; index < channelArray.Children.Count; index++)
        {
            if (channelArray.Children[index] is DataAtom atom && atom.Type == DataType.INT)
            {
                channels.Add(atom.Int);
            }
        }

        result.Add(new MoggTrack(name, channels));
    }

    return result;
}

static List<float> GetFloatArray(DataArray root, string key)
{
    var node = root.Array(key);
    if (node is null || node.Children.Count < 2 || node.Children[1] is not DataArray innerArray)
    {
        return new List<float>();
    }

    var result = new List<float>();
    for (int index = 0; index < innerArray.Children.Count; index++)
    {
        if (innerArray.Children[index] is DataAtom atom)
        {
            result.Add(atom.Type switch
            {
                DataType.INT => atom.Int,
                DataType.FLOAT => atom.Float,
                _ => 0f,
            });
        }
        else
        {
            result.Add(0f);
        }
    }

    return result;
}

static int ConvertMidi(string inputPath, string outputPath, string songName, List<MoggTrack> moggTracks, string? songEndPosition)
{
    var noteMap = new Dictionary<int, int>
    {
        {  96, 25 }, {  98, 26 }, { 100, 27 },
        { 102, 37 }, { 104, 38 }, { 106, 39 },
        { 108, 49 }, { 110, 50 }, { 112, 51 },
        { 114, 61 }, { 116, 62 }, { 118, 63 },
    };

    var seenNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var uniqueNames = new List<string>();
    foreach (MoggTrack track in moggTracks)
    {
        string normalized = NormalizeName(track.Name);
        if (!seenNames.TryGetValue(normalized, out int count))
        {
            seenNames[normalized] = 1;
            uniqueNames.Add(normalized);
            continue;
        }

        count++;
        seenNames[normalized] = count;
        uniqueNames.Add($"{normalized}_{count}");
    }

    var midi = MidiFile.Read(inputPath, new ReadingSettings
    {
        InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.ReadValid,
        InvalidMetaEventParameterValuePolicy = InvalidMetaEventParameterValuePolicy.SnapToLimits,
        NotEnoughBytesPolicy = NotEnoughBytesPolicy.Ignore,
        UnknownChannelEventPolicy = UnknownChannelEventPolicy.SkipStatusByteAndOneDataByte,
    });

    var noteTracks = midi.GetTrackChunks().Where(track => track.Events.Any(e => e is NoteOnEvent)).ToList();
    for (int index = 0; index < noteTracks.Count; index++)
    {
        TrackChunk trackChunk = noteTracks[index];
        string trackName = index < uniqueNames.Count ? uniqueNames[index] : $"{songName}_{index + 1}";
        string instrumentName = index < moggTracks.Count ? NormalizeInstrumentName(moggTracks[index].Name) : "guitar";

        for (int eventIndex = trackChunk.Events.Count - 1; eventIndex >= 0; eventIndex--)
        {
            if (trackChunk.Events[eventIndex] is SequenceTrackNameEvent or InstrumentNameEvent)
            {
                trackChunk.Events.RemoveAt(eventIndex);
            }
        }

        trackChunk.Events.Insert(0, new SequenceTrackNameEvent(trackName));
        trackChunk.Events.Insert(1, new InstrumentNameEvent(instrumentName));

        using var notesManager = trackChunk.ManageNotes();
        foreach (Note note in notesManager.Objects)
        {
            if (noteMap.TryGetValue(note.NoteNumber, out int mappedNote))
            {
                note.NoteNumber = (SevenBitNumber)mappedNote;
            }
        }
    }

    AddMasterTrack(midi, songEndPosition);
    midi.Write(outputPath, overwriteFile: true);
    return noteTracks.Count;
}

static void AddMasterTrack(MidiFile midi, string? songEndPosition)
{
    if (!TryGetRoundedSongEndBar(songEndPosition, out long startBar))
    {
        throw new InvalidOperationException("Unable to determine song length from moggsong data for BeatWeaver master track generation.");
    }

    foreach (TrackChunk existingMasterTrack in midi
                 .GetTrackChunks()
                 .Where(IsMasterTrack)
                 .ToList())
    {
        midi.Chunks.Remove(existingMasterTrack);
    }

    TempoMap tempoMap = midi.GetTempoMap();
    long outroStart = TimeConverter.ConvertFrom(new BarBeatTicksTimeSpan(startBar, 0, 0), tempoMap);
    long transitionStart = TimeConverter.ConvertFrom(new BarBeatTicksTimeSpan(startBar + 1, 0, 0), tempoMap);
    long endingStart = TimeConverter.ConvertFrom(new BarBeatTicksTimeSpan(startBar + 2, 0, 0), tempoMap);
    long songStop = TimeConverter.ConvertFrom(new BarBeatTicksTimeSpan(startBar + 3, 0, 0), tempoMap);

    var masterTrack = new TrackChunk(new SequenceTrackNameEvent("master"));
    using (var notesManager = masterTrack.ManageNotes())
    {
        notesManager.Objects.Add(new Note((SevenBitNumber)2, transitionStart - outroStart) { Time = outroStart });
        notesManager.Objects.Add(new Note((SevenBitNumber)3, endingStart - transitionStart) { Time = transitionStart });
        notesManager.Objects.Add(new Note((SevenBitNumber)4, songStop - endingStart) { Time = endingStart });
    }

    midi.Chunks.Add(masterTrack);
}

static bool IsMasterTrack(TrackChunk trackChunk)
{
    return trackChunk
        .Events
        .OfType<SequenceTrackNameEvent>()
        .Any(trackName => string.Equals(trackName.Text, "master", StringComparison.OrdinalIgnoreCase));
}

static bool TryGetRoundedSongEndBar(string? songEndPosition, out long roundedBar)
{
    roundedBar = 0;
    if (string.IsNullOrWhiteSpace(songEndPosition))
    {
        return false;
    }

    string[] parts = songEndPosition.Split(':', StringSplitOptions.TrimEntries);
    if (parts.Length == 0 || !long.TryParse(parts[0], out long bar))
    {
        return false;
    }

    long beat = 0;
    long tick = 0;
    if (parts.Length > 1)
    {
        _ = long.TryParse(parts[1], out beat);
    }

    if (parts.Length > 2)
    {
        _ = long.TryParse(parts[2], out tick);
    }

    roundedBar = bar + (beat > 0 || tick > 0 ? 1 : 0);
    return true;
}

static List<int> GetTransitionTracks(List<MoggTrack> moggTracks)
{
    var transitionTracks = new List<int>();
    for (int index = 0; index < moggTracks.Count; index++)
    {
        string instrumentName = NormalizeInstrumentName(moggTracks[index].Name);
        if (instrumentName is "drums" or "fx")
        {
            transitionTracks.Add(index);
        }
    }

    return transitionTracks;
}

static List<double> ComputeTrackVolumes(List<MoggTrack> moggTracks, List<float> channelVolumes)
{
    return moggTracks
        .Select(track =>
        {
            double sum = 0.0;
            int count = 0;
            foreach (int channel in track.Channels)
            {
                if (channel < 0 || channel >= channelVolumes.Count)
                {
                    continue;
                }

                sum += channelVolumes[channel];
                count++;
            }

            return count > 0 ? sum / count : 0.0;
        })
        .ToList();
}

record MoggTrack(string Name, List<int> Channels);

sealed class BeatWeaverSong
{
    [JsonPropertyName("metadata")]
    public required BeatWeaverMetadata Metadata { get; init; }

    [JsonPropertyName("audio")]
    public required BeatWeaverAudio Audio { get; init; }
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
    public required List<List<int>> Channels { get; init; }

    [JsonPropertyName("volume")]
    public List<double>? Volume { get; init; }

    [JsonPropertyName("outro_tracks")]
    public required List<int> OutroTracks { get; init; }

    [JsonPropertyName("transition_tracks")]
    public required List<int> TransitionTracks { get; init; }
}

file static class RegexHelpers
{
    public static readonly Regex RepeatedUnderscores = new("_+", RegexOptions.Compiled);
    public static readonly Regex TrailingDigits = new(@"\d+$", RegexOptions.Compiled);
}
