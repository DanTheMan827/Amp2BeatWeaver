using System.Buffers.Binary;
using NVorbis;
using OggVorbisEncoder;

internal static partial class App
{
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
        ReencodeOggTo48Khz(outputPath);
        if (!TryReadOggVorbisStreamInfo(outputPath, out info))
        {
            Console.Error.WriteLine($"Warning: unable to inspect {outputPath} after re-encoding; skipping silence padding.");
            return;
        }
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

static void ReencodeOggTo48Khz(string outputPath)
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

        WriteResampledVorbisAudio(reader, processingState, oggStream, output, TargetSampleRate);

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
        float quality = -0.1f + (step / 100.0f);
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

static void WriteResampledVorbisAudio(VorbisReader reader, ProcessingState processingState, OggStream oggStream, Stream output, int targetSampleRate)
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
            processingState,
            oggStream,
            output);
    }

    FlushEncodedFrames(encodedBuffer, ref encodedFrameBufferCount, processingState, oggStream, output);
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
}
