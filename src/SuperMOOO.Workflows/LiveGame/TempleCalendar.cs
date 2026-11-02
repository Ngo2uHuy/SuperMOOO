namespace SuperMOOO.Workflows.LiveGame;

internal static class TempleCalendar
{
    private const double Cycle = 29.530588853;
    private static double Sin(double degrees) => Math.Sin(degrees * Math.PI / 180);
    private static int Floor(double value) => (int)Math.Floor(value);
    private static int Julian(DateTime date) => (int)(date.Date - new DateTime(2000, 1, 1)).TotalDays + 2451545;

    // Ngày Sóc và Trung khí theo công thức thiên văn giới thiệu bởi Hồ Ngọc Đức:
    // https://honguyenviet.com/amlich/calrules.html . Dùng UTC+7, không dùng lịch Trung Quốc UTC+8.
    private static int NewMoon(int cycle)
    {
        var t = cycle / 1236.85;
        var t2 = t * t;
        var t3 = t2 * t;
        var sun = 359.2242 + 29.10535608 * cycle - 0.0000333 * t2 - 0.00000347 * t3;
        var moon = 306.0253 + 385.81691806 * cycle + 0.0107306 * t2 + 0.00001236 * t3;
        var latitude = 21.2964 + 390.67050646 * cycle - 0.0016528 * t2 - 0.00000239 * t3;
        (double Weight, double Angle)[] terms =
        [
            (0.1734 - 0.000393 * t, sun), (0.0021, 2 * sun), (-0.4068, moon), (0.0161, 2 * moon),
            (-0.0004, 3 * moon), (0.0104, 2 * latitude), (-0.0051, sun + moon), (-0.0074, sun - moon),
            (0.0004, 2 * latitude + sun), (-0.0004, 2 * latitude - sun), (-0.0006, 2 * latitude + moon),
            (0.001, 2 * latitude - moon), (0.0005, 2 * moon + sun)
        ];
        var mean = 2415020.75933 + 29.53058868 * cycle + 0.0001178 * t2 - 0.000000155 * t3
            + 0.00033 * Sin(166.56 + 132.87 * t - 0.009173 * t2);
        var delta = -0.000278 + 0.000265 * t + 0.000262 * t2;
        return Floor(mean + terms.Sum(p => p.Weight * Sin(p.Angle)) - delta + 0.5 + 7.0 / 24);
    }

    private static int SolarSector(int day)
    {
        var t = (day - 2451545.5 - 7.0 / 24) / 36525;
        var anomaly = 357.52910 + 35999.05030 * t - 0.0001559 * t * t - 0.00000048 * t * t * t;
        var longitude = 280.46645 + 36000.76983 * t + 0.0003032 * t * t
            + (1.914600 - 0.004817 * t - 0.000014 * t * t) * Sin(anomaly)
            + (0.019993 - 0.000101 * t) * Sin(2 * anomaly) + 0.000290 * Sin(3 * anomaly);
        return Floor(((longitude % 360 + 360) % 360) / 30);
    }

    private static int WinterMonth(int year)
    {
        var cycle = Floor((Julian(new DateTime(year, 12, 31)) - 2415021) / Cycle);
        var day = NewMoon(cycle);
        return SolarSector(day) >= 9 ? NewMoon(cycle - 1) : day;
    }

    private static int LeapOffset(int start)
    {
        var cycle = Floor((start - 2415021.076998695) / Cycle + 0.5);
        var sector = SolarSector(NewMoon(cycle + 1));
        for (var i = 2; i <= 14; i++)
        {
            var next = SolarSector(NewMoon(cycle + i));
            if (next == sector) return i - 1;
            sector = next;
        }
        return 13;
    }

    /// <summary>Holiday: "" ngày thường, "mung1", "ram" hoặc "le" (Tết, Nguyên tiêu, Phật Đản, Vu Lan, Hạ nguyên) để trang vẽ trang trí.</summary>
    public static (string Text, string Holiday) Describe(DateTime date)
    {
        if (date.Year is < 1900 or > 2099) return (date.ToString("dd/MM/yyyy"), "");
        var dayNumber = Julian(date);
        var cycle = Floor((dayNumber - 2415021.076998695) / Cycle);
        var monthStart = NewMoon(cycle + 1);
        if (monthStart > dayNumber) monthStart = NewMoon(cycle);
        var start = WinterMonth(date.Year);
        var end = start;
        if (start >= monthStart) start = WinterMonth(date.Year - 1);
        else end = WinterMonth(date.Year + 1);
        var offset = Floor((monthStart - start) / 29.0);
        var month = offset + 11;
        var leap = false;
        if (end - start > 365)
        {
            var extra = LeapOffset(start);
            if (offset >= extra) { month--; leap = offset == extra; }
        }
        if (month > 12) month -= 12;
        var day = dayNumber - monthStart + 1;
        var festival = !leap && day == 15 ? month switch { 1 => "Tết Nguyên tiêu", 4 => "Phật Đản", 7 => "Vu Lan", 10 => "Hạ nguyên", _ => "Ngày rằm bình an" }
            : day == 1 ? month == 1 && !leap ? "Mừng năm mới" : "Mùng một an lành" : "";
        var holiday = festival.Length == 0 ? "" : !leap && (day == 15 && month is 1 or 4 or 7 or 10 || day == 1 && month == 1) ? "le" : day == 15 ? "ram" : "mung1";
        return ($"Ngày {day}/{month}{(leap ? " nhuận" : "")} âm lịch" + (festival.Length > 0 ? " · " + festival : ""), holiday);
    }
}
