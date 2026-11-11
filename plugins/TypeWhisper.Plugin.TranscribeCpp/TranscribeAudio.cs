using System.Buffers.Binary;

namespace TypeWhisper.Plugin.TranscribeCpp;

internal static class TranscribeAudio
{
    internal const int SampleRate = 16_000;

    internal static float[] ReadWave(byte[] wav)
    {
        var span = wav.AsSpan();
        if (span.Length < 12 || !span[..4].SequenceEqual("RIFF"u8) || !span.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Expected a complete RIFF/WAVE recording.");
        // The host's ffmpeg pipe cannot seek back to fill RIFF/data lengths. Only
        // that explicit sentinel may consume the remaining, already-buffered PCM.
        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        var pipeWave = riffSize == uint.MaxValue;
        if (!pipeWave && riffSize != (uint)(span.Length - 8))
            throw new InvalidDataException("Expected a complete RIFF/WAVE recording.");
        var formatFound = false;
        ReadOnlySpan<byte> audio = [];
        for (var offset = 12; offset < span.Length;)
        {
            if (span.Length - offset < 8) throw new InvalidDataException("Incomplete WAVE chunk header.");
            var size = BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + 4)..]);
            if (pipeWave && formatFound && size == uint.MaxValue && span.Slice(offset, 4).SequenceEqual("data"u8))
                size = (uint)(span.Length - offset - 8);
            if (size > int.MaxValue || size > span.Length - offset - 8)
                throw new InvalidDataException("Incomplete WAVE audio chunk.");
            var chunk = span.Slice(offset + 8, (int)size);
            if (span.Slice(offset, 4).SequenceEqual("fmt "u8))
            {
                if (formatFound || chunk.Length < 16 || BinaryPrimitives.ReadInt16LittleEndian(chunk) != 1
                    || BinaryPrimitives.ReadInt16LittleEndian(chunk[2..]) != 1
                    || BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]) != SampleRate
                    || BinaryPrimitives.ReadInt32LittleEndian(chunk[8..]) != SampleRate * 2
                    || BinaryPrimitives.ReadInt16LittleEndian(chunk[12..]) != 2
                    || BinaryPrimitives.ReadInt16LittleEndian(chunk[14..]) != 16)
                    throw new InvalidDataException("transcribe.cpp requires PCM16 mono audio at 16 kHz.");
                formatFound = true;
            }
            else if (span.Slice(offset, 4).SequenceEqual("data"u8))
            {
                if (!audio.IsEmpty) throw new InvalidDataException("Multiple WAVE data chunks are not supported.");
                audio = chunk;
            }
            offset = checked(offset + 8 + (int)size + ((int)size & 1));
            if (offset > span.Length) throw new InvalidDataException("Missing WAVE chunk padding.");
        }
        if (!formatFound || audio.IsEmpty || (audio.Length & 1) != 0)
            throw new InvalidDataException("The WAVE recording has no complete PCM16 audio.");
        return Pcm16ToFloat(audio);
    }

    internal static float[] Pcm16ToFloat(ReadOnlySpan<byte> pcm)
    {
        if ((pcm.Length & 1) != 0) throw new InvalidDataException("Incomplete PCM16 sample.");
        var result = new float[pcm.Length / 2];
        for (var index = 0; index < result.Length; index++)
            result[index] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(index * 2)..]) / 32768f;
        return result;
    }

    // Non-overlapping sample windows retain repeated speech. Prefer a quiet boundary to cutting a word;
    // continuous speech still advances at the bounded cut, rather than discarding audio past a native cap.
    internal static IEnumerable<ReadOnlyMemory<float>> Windows(float[] pcm, long maxAudioMs)
    {
        var maximum = maxAudioMs <= 0 ? pcm.Length
            : (int)Math.Min(pcm.Length, Math.Max(1, maxAudioMs - Math.Min(2000, maxAudioMs / 10)) * 16);
        for (var offset = 0; offset < pcm.Length;)
        {
            var length = Math.Min(maximum, pcm.Length - offset);
            if (offset + length < pcm.Length)
                length = QuietBoundary(pcm, offset, length);
            yield return pcm.AsMemory(offset, length);
            offset += length;
        }
    }

    private static int QuietBoundary(float[] pcm, int offset, int length)
    {
        const int quietSamples = SampleRate / 6;
        var quiet = 0;
        for (var index = length - 1; index > length * 3 / 4; index--)
        {
            quiet = Math.Abs(pcm[offset + index]) < .01f ? quiet + 1 : 0;
            if (quiet >= quietSamples) return index + quietSamples / 2;
        }
        return length;
    }
}
