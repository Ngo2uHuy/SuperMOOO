namespace SuperMOOO.Media.Ffmpeg;

/// <summary>Số/chuỗi cho filter graph: luôn InvariantCulture để máy locale dấu phẩy không làm hỏng lệnh.</summary>
public static class Fmt
{
    public static string N(double value, int digits = 6) =>
        Math.Round(value, digits).ToString("0." + new string('#', digits), CultureInfo.InvariantCulture);

    public static string Sec(TimeSpan t) => N(t.TotalSeconds, 3);

    public static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);

    /// <summary>atempo chỉ nhận 0.5–2.0 mỗi lần: tách thành chuỗi, độ chính xác 6 chữ số.</summary>
    public static string TempoChain(double tempo)
    {
        if (!double.IsFinite(tempo) || tempo <= 0) throw new ArgumentOutOfRangeException(nameof(tempo));
        var parts = new List<string>();
        while (tempo > 2) { parts.Add("atempo=2"); tempo /= 2; }
        while (tempo < 0.5) { parts.Add("atempo=0.5"); tempo /= 0.5; }
        parts.Add("atempo=" + N(tempo));
        return string.Join(',', parts);
    }
}
