using System.Text;
using DtxCS.DataTypes;

internal static partial class App
{
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
}
