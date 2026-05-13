using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

internal static partial class App
{
static MidiConversionResult ConvertMidi(string inputPath, string outputPath, string songName, List<MoggTrack> moggTracks, string? songEndPosition)
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
    TimeSpan duration = TimeSpan.FromMilliseconds(midi.GetDuration<MetricTimeSpan>().TotalMilliseconds);
    midi.Write(outputPath, overwriteFile: true);
    return new MidiConversionResult(noteTracks.Count, duration);
}

static void AddMasterTrack(MidiFile midi, string? songEndPosition)
{
    if (!TryGetRoundedSongEndBar(songEndPosition, out long startBar))
    {
        throw new InvalidOperationException("Unable to determine BeatWeaver master track placement from moggsong data. Expected song_info.length in bar:beat:tick format.");
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

    // Amplitude song_info.length is expected as bar:beat:tick.
    // We only need the next full bar boundary, so any non-zero beat or tick
    // component rounds the position up to the next measure start.
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
}
