using DtxCS;
using DtxCS.DataTypes;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static partial class App
{
    const string TemporaryExtractionDirectoryPrefix = "amp2beatweaver-";
    const int OggScanBufferSize = 8192;
    const int OggPageHeaderSize = 27;
    const int VorbisIdentificationPacketLength = 16;
    const int TargetSampleRate = 48_000;
    const int ReencodeBitRatePerChannel = 128_000;
    const int DecodeFrameChunkSize = 4096;
    const int SilentVorbisChunkSize = 8192;
    const float SilentVorbisQuality = 0.1f;

    public static int Run(string[] args)
    {
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
            var ambienceTrack = moggTracks.Where(t => t.Name == "bg_click").FirstOrDefault();
            moggTracks = moggTracks.Where(t => t.Name != "freestyle" && t.Name != "bg_click").ToList();
            List<float> moggVolumes = GetFloatArray(moggSong, "vols");

            string outputSongDirectory = Path.Combine(outputRootDirectory, normalizedSongId);
            Directory.CreateDirectory(outputSongDirectory);

            string outputMidi = Path.Combine(outputSongDirectory, $"{normalizedSongId}.mid");
            MidiConversionResult midiConversion = ConvertMidi(amplitudeMidi, outputMidi, normalizedSongId, moggTracks, songEndPosition);
            List<MoggTrack> playableTracks = moggTracks.Take(midiConversion.PlayableTrackCount).ToList();

            if (amplitudeMogg is not null)
            {
                string outputOgg = Path.Combine(outputSongDirectory, $"{normalizedSongId}.ogg");
                CopyMoggAsOgg(amplitudeMogg, outputOgg);
                EnsureOggDurationAtLeast(outputOgg, midiConversion.Duration);
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
                    Ambience = ambienceTrack?.Channels ?? null,
                    AmbienceVolume = 0,
                    Volume = moggVolumes,
                    OutroTracks = playableTracks.Select((track, index) => index).ToList(),
                    TransitionTracks = playableTracks.Select((track, index) => index).ToList(),
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
    }
}
