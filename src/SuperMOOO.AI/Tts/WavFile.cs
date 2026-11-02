using System.Buffers.Binary;

namespace SuperMOOO.AI.Tts;

public readonly record struct WavFormat(int SampleRate, int Channels, int BitsPerSample);

/// <summary>Đọc/ghi WAV PCM tối thiểu: đủ để lấy thời lượng, bọc PCM thô và nối các khối cùng định dạng.</summary>
public static class WavFile
{
    public static (WavFormat Format, int DataOffset, int DataLength) Parse(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Không phải file WAV hợp lệ.");

        WavFormat? format = null;
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.Slice(pos, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(pos + 4, 4)), int.MaxValue);
            var body = pos + 8;
            if (id.SequenceEqual("fmt "u8) && body + 16 <= wav.Length)
                format = new WavFormat((int)BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(body + 4, 4)),
                    BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 2, 2)), BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 14, 2)));
            else if (id.SequenceEqual("data"u8))
            {
                if (format is null) break;
                // Stream chưa biết độ dài ghi 0xFFFFFFFF; lấy phần còn lại của buffer.
                var length = Math.Min(size, wav.Length - body);
                return (format.Value, body, length);
            }

            pos = body + size + (size & 1);
        }

        throw new InvalidDataException("WAV thiếu khối fmt/data.");
    }

    public static (TimeSpan Duration, int SampleRate) Describe(ReadOnlySpan<byte> wav)
    {
        var (format, _, length) = Parse(wav);
        var bytesPerSecond = format.SampleRate * format.Channels * (format.BitsPerSample / 8);
        if (bytesPerSecond <= 0 || length <= 0) throw new InvalidDataException("Audio rỗng.");
        return (TimeSpan.FromSeconds((double)length / bytesPerSecond), format.SampleRate);
    }

    public static (TimeSpan Duration, int SampleRate) DescribeFile(string path)
    {
        using var stream = File.OpenRead(path);
        // Header thường nằm trong 4 KB đầu; duration tính theo kích thước file nên không cần đọc hết.
        var head = new byte[(int)Math.Min(stream.Length, 4096)];
        stream.ReadExactly(head);
        var (format, offset, _) = Parse(head);
        var bytesPerSecond = format.SampleRate * format.Channels * (format.BitsPerSample / 8);
        var data = stream.Length - offset;
        if (bytesPerSecond <= 0 || data <= 0) throw new InvalidDataException("Audio rỗng.");
        return (TimeSpan.FromSeconds((double)data / bytesPerSecond), format.SampleRate);
    }

    public static byte[] FromPcm16(ReadOnlySpan<byte> pcm, int sampleRate, int channels = 1) => Build(pcm, new WavFormat(sampleRate, channels, 16));

    /// <summary>Nối các WAV cùng định dạng thành một WAV (thay cho ffmpeg concat).</summary>
    public static byte[] Concat(IReadOnlyList<byte[]> wavs)
    {
        if (wavs.Count == 1) return wavs[0];
        using var pcm = new MemoryStream();
        WavFormat? first = null;
        foreach (var wav in wavs)
        {
            var (format, offset, length) = Parse(wav);
            if (first is not null && first != format) throw new InvalidDataException("Các khối audio khác định dạng, không nối được.");
            first = format;
            pcm.Write(wav, offset, length);
        }

        return Build(pcm.ToArray(), first ?? throw new InvalidDataException("Không có audio để nối."));
    }

    private static byte[] Build(ReadOnlySpan<byte> pcm, WavFormat f)
    {
        var blockAlign = f.Channels * f.BitsPerSample / 8;
        var buffer = new byte[44 + pcm.Length];
        var span = buffer.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + pcm.Length);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)f.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], f.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], f.SampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], (short)f.BitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], pcm.Length);
        pcm.CopyTo(span[44..]);
        return buffer;
    }

    public static bool LooksLikeContainer(ReadOnlySpan<byte> data) =>
        data.StartsWith("ID3"u8) || data.StartsWith("OggS"u8) || data.StartsWith("fLaC"u8);
}
