using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

internal static partial class App
{
    const short TargetTicksPerQuarterNote = 96;

    static MidiConversionResult ConvertMidi(string inputPath, string outputPath, string songName, List<MoggTrack> moggTracks)
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
        EnsureTicksPerQuarterNote(midi, TargetTicksPerQuarterNote);

        var noteTracks = midi.GetTrackChunks().Where(track => track.Events.Any(e => e is NoteOnEvent)).Where((t, index) => index < moggTracks.Count() && t.Events.OfType<SequenceTrackNameEvent>().First().Text.StartsWith("T")).ToList();
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
            foreach (Note note in notesManager.Objects.ToList())
            {
                if (noteMap.TryGetValue(note.NoteNumber, out int mappedNote))
                {
                    note.NoteNumber = (SevenBitNumber)mappedNote;
                }
                else
                {
                    notesManager.Objects.Remove(note);
                }
            }
        }

        foreach (var chunk in midi.Chunks.OfType<TrackChunk>().Where((c, i) => i > 0).ToList())
        {
            if (!noteTracks.Contains(chunk))
            {
                midi.Chunks.Remove(chunk);
            }
        }

        AddMasterTrack(midi, noteTracks);
        TimeSpan duration = TimeSpan.FromMilliseconds(midi.GetDuration<MetricTimeSpan>().TotalMilliseconds);
        midi.Write(outputPath, overwriteFile: true);
        return new MidiConversionResult(noteTracks.Count, duration);
    }

    static void EnsureTicksPerQuarterNote(MidiFile midi, short targetTicksPerQuarterNote)
    {
        if (midi.TimeDivision is not TicksPerQuarterNoteTimeDivision sourceDivision)
        {
            throw new InvalidOperationException("Input MIDI must use ticks-per-quarter-note time division.");
        }

        short sourceTicksPerQuarterNote = sourceDivision.TicksPerQuarterNote;
        if (sourceTicksPerQuarterNote == targetTicksPerQuarterNote)
        {
            return;
        }

        double scale = targetTicksPerQuarterNote / (double)sourceTicksPerQuarterNote;
        foreach (TrackChunk trackChunk in midi.GetTrackChunks())
        {
            List<TimedEvent> timedEvents = trackChunk.GetTimedEvents().ToList();
            trackChunk.Events.Clear();

            long previousEventTime = 0;
            foreach (TimedEvent timedEvent in timedEvents)
            {
                long scaledEventTime = (long)Math.Round(timedEvent.Time * scale, MidpointRounding.AwayFromZero);
                if (scaledEventTime < previousEventTime)
                {
                    scaledEventTime = previousEventTime;
                }

                timedEvent.Event.DeltaTime = scaledEventTime - previousEventTime;
                trackChunk.Events.Add(timedEvent.Event);
                previousEventTime = scaledEventTime;
            }
        }

        midi.TimeDivision = new TicksPerQuarterNoteTimeDivision(targetTicksPerQuarterNote);
    }

    static void AddMasterTrack(MidiFile midi, List<TrackChunk> noteTracks)
    {
        foreach (TrackChunk chunk in midi
            .GetTrackChunks()
            .Where(IsTrackNameEvent)
            .ToList())
        {
            if (chunk.Events.OfType<SequenceTrackNameEvent>().Any(e => e.Text == "master" || e.Text == "freestyle" || e.Text == "bg_click"))
            {
                midi.Chunks.Remove(chunk);
            }
        }

        TempoMap tempoMap = midi.GetTempoMap();

        // Find the tick at which the last note across all note tracks ends.
        long lastNoteTick = noteTracks
            .SelectMany(t => t.GetNotes())
            .Select(n => n.Time + n.Length)
            .DefaultIfEmpty(0)
            .Max();

        // Round up to the next full-bar boundary to get the bar where note 4 starts.
        BarBeatTicksTimeSpan lastNoteTime = TimeConverter.ConvertTo<BarBeatTicksTimeSpan>(lastNoteTick, tempoMap);
        long finalBar = lastNoteTime.Bars + (lastNoteTime.Beats > 0 || lastNoteTime.Ticks > 0 ? 1 : 0);

        long note4Start = TimeConverter.ConvertFrom(new BarBeatTicksTimeSpan(finalBar, 0, 0), tempoMap);
        long note3Start = TimeConverter.ConvertFrom(new BarBeatTicksTimeSpan(finalBar - 1, 0, 0), tempoMap);
        long note2Start = TimeConverter.ConvertFrom(new BarBeatTicksTimeSpan(finalBar - 2, 0, 0), tempoMap);
        long songStop   = TimeConverter.ConvertFrom(new BarBeatTicksTimeSpan(finalBar + 1, 0, 0), tempoMap);

        // Remove any note-track notes that start at or after note 4's start position.
        foreach (TrackChunk track in noteTracks)
        {
            using var notesManager = track.ManageNotes();
            foreach (Note note in notesManager.Objects.ToList())
            {
                if (note.Time >= note4Start)
                {
                    notesManager.Objects.Remove(note);
                }
            }
        }

        var masterTrack = new TrackChunk(new SequenceTrackNameEvent("master"));
        using (var notesManager = masterTrack.ManageNotes())
        {
            notesManager.Objects.Add(new Note((SevenBitNumber)2, note3Start - note2Start) { Time = note2Start });
            notesManager.Objects.Add(new Note((SevenBitNumber)3, note4Start - note3Start) { Time = note3Start });
            notesManager.Objects.Add(new Note((SevenBitNumber)4, songStop - note4Start)   { Time = note4Start });
        }

        midi.Chunks.Add(masterTrack);
    }

    static bool IsTrackNameEvent(TrackChunk trackChunk)
    {
        return trackChunk
            .Events
            .OfType<SequenceTrackNameEvent>()
            .Any();
    }
}
