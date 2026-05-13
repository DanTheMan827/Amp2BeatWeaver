using System.Buffers.Binary;
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
using NVorbis;
using OggVorbisEncoder;

const string TemporaryExtractionDirectoryPrefix = "amp2beatweaver-";
const int OggScanBufferSize = 8192;
const int OggPageHeaderSize = 27;
const int VorbisIdentificationPacketLength = 16;
const int TargetSampleRate = 48_000;
const int ReencodeBitRatePerChannel = 128_000;
const int DecodeFrameChunkSize = 4096;
const int SilentVorbisChunkSize = 8192;
const float SilentVorbisQuality = 0.1f;

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

static void CopyMoggAsOgg(string inputPath, string outputPath)
{
    using var input = File.OpenRead(inputPath);
    using var output = File.Create(outputPath);

    long oggOffset = FindOggStreamOffset(input);
    if (oggOffset < 0)
    {
        Console.Error.WriteLine($"Warning: no Ogg stream signature found in {inputPath}; copying the file unchanged.");
        input.Position = 0;
    }
    else
    {
        input.Position = oggOffset;
    }

    input.CopyTo(output);
}

static void EnsureOggDurationAtLeast(string outputPath, TimeSpan minimumDuration)
{
    if (!TryReadOggVorbisStreamInfo(outputPath, out OggVorbisStreamInfo info))
    {
        Console.Error.WriteLine($"Warning: unable to inspect {outputPath} as Ogg Vorbis; skipping silence padding.");
        return;
    }

    if (info.SampleRate != TargetSampleRate)
    {
        ReencodeOggTo48Khz(outputPath, minimumDuration);
        return;
    }

    long missingSamples = GetMissingSampleCount(info.SampleRate, info.Duration, minimumDuration);
    if (missingSamples <= 0)
    {
        return;
    }

    AppendSilentVorbisChain(outputPath, info.Channels, info.SampleRate, missingSamples);
}

static long GetMissingSampleCount(int sampleRate, TimeSpan currentDuration, TimeSpan minimumDuration)
{
    double missingSeconds = (minimumDuration - currentDuration).TotalSeconds;
    if (missingSeconds <= 0)
    {
        return 0;
    }

    return (long)Math.Ceiling(missingSeconds * sampleRate);
}

static bool TryReadOggVorbisStreamInfo(string path, out OggVorbisStreamInfo info)
{
    info = default;

    try
    {
        using var input = File.OpenRead(path);
        using var packetBuffer = new MemoryStream();

        int? serialNumber = null;
        int sampleRate = 0;
        int channels = 0;
        long lastGranulePosition = -1;
        bool foundIdentificationPacket = false;

        while (TryReadOggPage(input, out OggPageData page))
        {
            serialNumber ??= page.SerialNumber;
            if (page.SerialNumber != serialNumber.Value)
            {
                continue;
            }

            if (page.GranulePosition >= 0)
            {
                lastGranulePosition = page.GranulePosition;
            }

            if (!foundIdentificationPacket)
            {
                int bodyOffset = 0;
                foreach (byte segmentLength in page.SegmentTable)
                {
                    packetBuffer.Write(page.Body, bodyOffset, segmentLength);
                    bodyOffset += segmentLength;
                    if (segmentLength == byte.MaxValue)
                    {
                        continue;
                    }

                    foundIdentificationPacket = TryParseVorbisIdentificationPacket(packetBuffer.ToArray(), out channels, out sampleRate);
                    break;
                }
            }
        }

        if (!foundIdentificationPacket || channels <= 0 || sampleRate <= 0 || lastGranulePosition < 0)
        {
            return false;
        }

        info = new OggVorbisStreamInfo(channels, sampleRate, TimeSpan.FromSeconds(lastGranulePosition / (double)sampleRate));
        return true;
    }
    catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException)
    {
        return false;
    }
}

static bool TryReadOggPage(Stream input, out OggPageData page)
{
    page = default;

    int firstByte = input.ReadByte();
    if (firstByte < 0)
    {
        return false;
    }

    byte[] header = new byte[OggPageHeaderSize];
    header[0] = (byte)firstByte;
    input.ReadExactly(header, 1, OggPageHeaderSize - 1);

    if (!header.AsSpan(0, 4).SequenceEqual("OggS"u8))
    {
        throw new InvalidDataException("Invalid Ogg page signature.");
    }

    byte[] segmentTable = new byte[header[26]];
    input.ReadExactly(segmentTable);

    int bodyLength = segmentTable.Sum(length => length);
    byte[] body = new byte[bodyLength];
    input.ReadExactly(body);

    page = new OggPageData(
        BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(14, 4)),
        BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(6, 8)),
        segmentTable,
        body);
    return true;
}

static bool TryParseVorbisIdentificationPacket(byte[] packet, out int channels, out int sampleRate)
{
    channels = 0;
    sampleRate = 0;

    if (packet.Length < VorbisIdentificationPacketLength)
    {
        return false;
    }

    ReadOnlySpan<byte> span = packet;
    if (span[0] != 0x01 || !span.Slice(1, 6).SequenceEqual("vorbis"u8))
    {
        return false;
    }

    channels = span[11];
    sampleRate = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(12, 4));
    return channels > 0 && sampleRate > 0;
}

static void AppendSilentVorbisChain(string outputPath, int channels, int sampleRate, long sampleCount)
{
    if (sampleCount <= 0)
    {
        return;
    }

    VorbisInfo info = VorbisInfo.InitVariableBitRate(channels, sampleRate, SilentVorbisQuality);
    ProcessingState processingState = ProcessingState.Create(info);
    var comments = new Comments();
    comments.AddTag("ENCODER", "Amp2BeatWeaver");

    using var output = new FileStream(outputPath, FileMode.Append, FileAccess.Write, FileShare.None);
    var oggStream = new OggStream(Random.Shared.Next(1, int.MaxValue));

    oggStream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
    oggStream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(comments));
    oggStream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));
    WritePendingPages(oggStream, output, force: true);

    float[][] silence = Enumerable.Range(0, channels)
        .Select(_ => new float[SilentVorbisChunkSize])
        .ToArray();

    while (sampleCount > 0)
    {
        int chunkSize = (int)Math.Min(sampleCount, SilentVorbisChunkSize);
        processingState.WriteData(silence, chunkSize, 0);
        sampleCount -= chunkSize;
        WritePendingPackets(processingState, oggStream, output);
    }

    processingState.WriteEndOfStream();
    WritePendingPackets(processingState, oggStream, output);
    WritePendingPages(oggStream, output, force: true);
}

static void ReencodeOggTo48Khz(string outputPath, TimeSpan minimumDuration)
{
    string temporaryPath = $"{outputPath}.tmp";
    File.Delete(temporaryPath);

    try
    {
        using var reader = new VorbisReader(outputPath);
        using var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None);

        VorbisInfo info = CreateVorbisInfo(reader.Channels, TargetSampleRate, reader.Channels * ReencodeBitRatePerChannel);
        ProcessingState processingState = ProcessingState.Create(info);
        var comments = new Comments();
        comments.AddTag("ENCODER", "Amp2BeatWeaver");

        var oggStream = new OggStream(Random.Shared.Next(1, int.MaxValue));
        oggStream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        oggStream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(comments));
        oggStream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));
        WritePendingPages(oggStream, output, force: true);

        long encodedFrames = WriteResampledVorbisAudio(reader, processingState, oggStream, output, TargetSampleRate);
        long minimumFrames = (long)Math.Ceiling(minimumDuration.TotalSeconds * TargetSampleRate);
        if (encodedFrames < minimumFrames)
        {
            WriteSilentFrames(processingState, oggStream, output, reader.Channels, minimumFrames - encodedFrames);
        }

        processingState.WriteEndOfStream();
        WritePendingPackets(processingState, oggStream, output);
        WritePendingPages(oggStream, output, force: true);
    }
    catch
    {
        File.Delete(temporaryPath);
        throw;
    }

    File.Move(temporaryPath, outputPath, overwrite: true);
}

static VorbisInfo CreateVorbisInfo(int channels, int sampleRate, int targetBitRate)
{
    VorbisInfo? bestInfo = null;
    int closestDifference = int.MaxValue;

    for (int step = 0; step <= 110; step++)
    {
        float quality = -0.1f + (step / 100f);
        VorbisInfo candidate = VorbisInfo.InitVariableBitRate(channels, sampleRate, quality);
        int difference = Math.Abs(candidate.BitRateNominal - targetBitRate);
        if (difference >= closestDifference)
        {
            continue;
        }

        closestDifference = difference;
        bestInfo = candidate;
    }

    return bestInfo ?? VorbisInfo.InitVariableBitRate(channels, sampleRate, SilentVorbisQuality);
}

static long WriteResampledVorbisAudio(VorbisReader reader, ProcessingState processingState, OggStream oggStream, Stream output, int targetSampleRate)
{
    int channels = reader.Channels;
    double sourceStep = reader.SampleRate / (double)targetSampleRate;
    float[] decodedInterleaved = new float[DecodeFrameChunkSize * channels];
    float[][] sourceBuffer = Enumerable.Range(0, channels)
        .Select(_ => new float[DecodeFrameChunkSize * 2])
        .ToArray();
    float[][] encodedBuffer = Enumerable.Range(0, channels)
        .Select(_ => new float[SilentVorbisChunkSize])
        .ToArray();

    int bufferedFrames = 0;
    long sourceBaseFrame = 0;
    double nextOutputSourceFrame = 0.0;
    int encodedFrameBufferCount = 0;
    long encodedFrames = 0;

    while (true)
    {
        int samplesRead = reader.ReadSamples(decodedInterleaved, 0, decodedInterleaved.Length);
        int framesRead = samplesRead / channels;
        if (framesRead == 0)
        {
            break;
        }

        EnsureSourceBufferCapacity(sourceBuffer, bufferedFrames + framesRead + 1);
        AppendInterleavedFrames(decodedInterleaved, framesRead, channels, sourceBuffer, bufferedFrames);
        bufferedFrames += framesRead;
        PumpResampler(
            sourceBuffer,
            ref bufferedFrames,
            ref sourceBaseFrame,
            ref nextOutputSourceFrame,
            sourceStep,
            encodedBuffer,
            ref encodedFrameBufferCount,
            ref encodedFrames,
            processingState,
            oggStream,
            output);
    }

    if (bufferedFrames > 0)
    {
        EnsureSourceBufferCapacity(sourceBuffer, bufferedFrames + 1);
        for (int channel = 0; channel < channels; channel++)
        {
            sourceBuffer[channel][bufferedFrames] = sourceBuffer[channel][bufferedFrames - 1];
        }

        bufferedFrames++;
        PumpResampler(
            sourceBuffer,
            ref bufferedFrames,
            ref sourceBaseFrame,
            ref nextOutputSourceFrame,
            sourceStep,
            encodedBuffer,
            ref encodedFrameBufferCount,
            ref encodedFrames,
            processingState,
            oggStream,
            output);
    }

    FlushEncodedFrames(encodedBuffer, ref encodedFrameBufferCount, processingState, oggStream, output);
    return encodedFrames;
}

static void EnsureSourceBufferCapacity(float[][] sourceBuffer, int requiredCapacity)
{
    if (sourceBuffer[0].Length >= requiredCapacity)
    {
        return;
    }

    int newCapacity = Math.Max(requiredCapacity, sourceBuffer[0].Length * 2);
    for (int channel = 0; channel < sourceBuffer.Length; channel++)
    {
        Array.Resize(ref sourceBuffer[channel], newCapacity);
    }
}

static void AppendInterleavedFrames(float[] interleaved, int framesRead, int channels, float[][] sourceBuffer, int destinationOffset)
{
    for (int frame = 0; frame < framesRead; frame++)
    {
        int interleavedOffset = frame * channels;
        for (int channel = 0; channel < channels; channel++)
        {
            sourceBuffer[channel][destinationOffset + frame] = interleaved[interleavedOffset + channel];
        }
    }
}

static void PumpResampler(
    float[][] sourceBuffer,
    ref int bufferedFrames,
    ref long sourceBaseFrame,
    ref double nextOutputSourceFrame,
    double sourceStep,
    float[][] encodedBuffer,
    ref int encodedFrameBufferCount,
    ref long encodedFrames,
    ProcessingState processingState,
    OggStream oggStream,
    Stream output)
{
    while (nextOutputSourceFrame + 1 < sourceBaseFrame + bufferedFrames)
    {
        double sourceOffset = nextOutputSourceFrame - sourceBaseFrame;
        int leftFrame = (int)Math.Floor(sourceOffset);
        double fraction = sourceOffset - leftFrame;

        for (int channel = 0; channel < sourceBuffer.Length; channel++)
        {
            float leftSample = sourceBuffer[channel][leftFrame];
            float rightSample = sourceBuffer[channel][leftFrame + 1];
            encodedBuffer[channel][encodedFrameBufferCount] = (float)(leftSample + ((rightSample - leftSample) * fraction));
        }

        encodedFrameBufferCount++;
        encodedFrames++;
        nextOutputSourceFrame += sourceStep;

        if (encodedFrameBufferCount == SilentVorbisChunkSize)
        {
            FlushEncodedFrames(encodedBuffer, ref encodedFrameBufferCount, processingState, oggStream, output);
        }
    }

    int discardFrames = Math.Max(0, (int)Math.Floor(nextOutputSourceFrame - sourceBaseFrame) - 1);
    if (discardFrames <= 0)
    {
        return;
    }

    int remainingFrames = bufferedFrames - discardFrames;
    for (int channel = 0; channel < sourceBuffer.Length; channel++)
    {
        Array.Copy(sourceBuffer[channel], discardFrames, sourceBuffer[channel], 0, remainingFrames);
    }

    bufferedFrames = remainingFrames;
    sourceBaseFrame += discardFrames;
}

static void FlushEncodedFrames(
    float[][] encodedBuffer,
    ref int encodedFrameBufferCount,
    ProcessingState processingState,
    OggStream oggStream,
    Stream output)
{
    if (encodedFrameBufferCount <= 0)
    {
        return;
    }

    processingState.WriteData(encodedBuffer, encodedFrameBufferCount, 0);
    WritePendingPackets(processingState, oggStream, output);
    encodedFrameBufferCount = 0;
}

static void WriteSilentFrames(ProcessingState processingState, OggStream oggStream, Stream output, int channels, long sampleCount)
{
    float[][] silence = Enumerable.Range(0, channels)
        .Select(_ => new float[SilentVorbisChunkSize])
        .ToArray();

    while (sampleCount > 0)
    {
        int chunkSize = (int)Math.Min(sampleCount, SilentVorbisChunkSize);
        processingState.WriteData(silence, chunkSize, 0);
        sampleCount -= chunkSize;
        WritePendingPackets(processingState, oggStream, output);
    }
}

static void WritePendingPackets(ProcessingState processingState, OggStream oggStream, Stream output)
{
    while (processingState.PacketOut(out OggPacket packet))
    {
        oggStream.PacketIn(packet);
        WritePendingPages(oggStream, output, force: false);
    }
}

static void WritePendingPages(OggStream oggStream, Stream output, bool force)
{
    while (oggStream.PageOut(out OggPage page, force))
    {
        output.Write(page.Header, 0, page.Header.Length);
        output.Write(page.Body, 0, page.Body.Length);
    }
}

static long FindOggStreamOffset(Stream input)
{
    ReadOnlySpan<byte> oggSignature = "OggS"u8;
    byte[] buffer = new byte[OggScanBufferSize + oggSignature.Length - 1];
    int overlap = 0;
    long bytesRead = 0;

    while (true)
    {
        int read = input.Read(buffer, overlap, buffer.Length - overlap);
        if (read == 0)
        {
            return -1;
        }

        int bufferLength = overlap + read;
        int signatureIndex = buffer.AsSpan(0, bufferLength).IndexOf(oggSignature);
        if (signatureIndex >= 0)
        {
            return bytesRead - overlap + signatureIndex;
        }

        bytesRead += read;
        overlap = Math.Min(oggSignature.Length - 1, bufferLength);
        buffer.AsSpan(bufferLength - overlap, overlap).CopyTo(buffer);
    }
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

file static class RegexHelpers
{
    public static readonly Regex RepeatedUnderscores = new("_+", RegexOptions.Compiled);
    public static readonly Regex TrailingDigits = new(@"\d+$", RegexOptions.Compiled);
}
