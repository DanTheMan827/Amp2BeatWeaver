using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

record MoggTrack(string Name, List<int> Channels);
readonly record struct MidiConversionResult(int PlayableTrackCount, TimeSpan Duration);
readonly record struct OggVorbisStreamInfo(int Channels, int SampleRate, TimeSpan Duration);
readonly record struct OggPageData(int SerialNumber, long GranulePosition, byte[] SegmentTable, byte[] Body);

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

internal static class RegexHelpers
{
    public static readonly Regex RepeatedUnderscores = new("_+", RegexOptions.Compiled);
    public static readonly Regex TrailingDigits = new(@"\d+$", RegexOptions.Compiled);
}
