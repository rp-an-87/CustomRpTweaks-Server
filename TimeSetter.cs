using System.Collections;
using System.Reflection;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Utils;

namespace CustomRpTweaks;

// Sets the raid clock from the client plugin's F12 buttons, in the menu as well as in a raid. Applies to raids started after it.
// SPT derives the clock from the real time (see TimeMath), so this re-aims weatherConfig.Acceleration and drops the cached
// weather forecast, whose times were worked out with the old value. A server restart goes back to the normal clock.
[Injectable(InjectionType.Singleton)]
public class TimeSetter(WeatherConfig weatherConfig, RaidWeatherService raidWeatherService, TimeUtil timeUtil, ISptLogger<TimeSetter> logger)
{
    public static readonly int[] Hours = [0, 3, 6, 12, 18, 21];

    // the acceleration the server started with
    private double? _normal;

    internal string SetHour(int hour)
    {
        if (!Hours.Contains(hour))
        {
            return JsonSerializer.Serialize(new { ok = false, error = $"hour {hour} not offered" });
        }

        try
        {
            _normal ??= weatherConfig.Acceleration ?? 7;
            var now = timeUtil.GetTimeStamp();
            weatherConfig.Acceleration = TimeMath.AccelerationFor(now, hour, _normal.Value);

            var forecast = typeof(RaidWeatherService).GetField("WeatherForecast", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(raidWeatherService) as IList;
            if (forecast is null)
            {
                logger.Warning("[CustomRpTweaks] Time setter: weather forecast list not found, the weather may still show the old time");
            }
            else
            {
                forecast.Clear();
            }

            var clock = TimeSpan.FromSeconds(TimeMath.ClockSeconds(now, weatherConfig.Acceleration.Value));
            logger.Info($"[CustomRpTweaks] Server time set to {hour:00}:00 (clock now {clock:hh\\:mm\\:ss}, acceleration {weatherConfig.Acceleration:0.000000})");
            return JsonSerializer.Serialize(new { ok = true, hour });
        }
        catch (Exception e)
        {
            logger.Error($"[CustomRpTweaks] Time setter failed: {e}");
            return JsonSerializer.Serialize(new { ok = false, error = e.Message });
        }
    }
}

// /customrptweaks/settime/<hour>, one route per hour the client offers
[Injectable]
public class TimeSetterRouter : StaticRouter
{
    private static TimeSetter? _setter;

    public TimeSetterRouter(JsonUtil jsonUtil, TimeSetter setter) : base(jsonUtil, GetRoutes())
    {
        _setter = setter;
    }

    private static List<RouteAction> GetRoutes() =>
        TimeSetter.Hours.Select(hour => new RouteAction(
            $"/customrptweaks/settime/{hour}",
            async (url, request, sessionId, _, _) => await Task.FromResult(_setter!.SetHour(hour)))).ToList();
}
