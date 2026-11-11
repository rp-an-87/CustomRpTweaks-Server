namespace CustomRpTweaks;

// SPT's raid clock is (3 h + real seconds * acceleration) mod 24 h, with no stored "current hour" to set. To land on an hour we
// pick the acceleration that puts the clock exactly on it right now: acceleration * now = k * 86400 + offset, with k the whole
// number of days that keeps the acceleration closest to the normal 7. The clock then keeps running at that (practically
// unchanged) speed from the wanted hour.
public static class TimeMath
{
    private const long Day = 86400;
    private const long RussiaOffset = 3 * 3600;

    public static double AccelerationFor(long nowSeconds, int hour, double normal)
    {
        var offset = (((hour * 3600L - RussiaOffset) % Day) + Day) % Day;
        var k = (long)Math.Round((normal * nowSeconds - offset) / Day);
        // half a second past the exact hour so rounding in the double never lands the clock just before it
        return (k * Day + offset + 0.5) / nowSeconds;
    }

    // what the server's clock shows for a timestamp and acceleration (same formula as SPT's WeatherHelper.GetInRaidTime)
    public static long ClockSeconds(long nowSeconds, double acceleration) =>
        (long)(RussiaOffset + nowSeconds * acceleration) % Day;
}
