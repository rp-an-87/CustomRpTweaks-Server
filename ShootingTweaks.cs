using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using System.Reflection;
using System.Text.Json.Serialization;

namespace CustomRpTweaks;

// Long-range shooting, from the item data and the global settings the client reads from the server:
//  - scopeZero: sights with a high enough zoom get more zeroing distances (the game works out each one from the ammo's ballistics, so any
//    distance is valid). Only distances above a scope's current longest are added; the existing ones are kept as they are.
//  - bipod: the prone limits on how far the aim can turn while a bipod is deployed (set per bipod, in its item data) are widened, never
//    narrowed, and the prone ground-angle check is loosened. The other global mounting thresholds also affect mounting that is not prone,
//    so they are only changed when the config gives a value; a value left out of the config is not touched.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 5)]
public class ShootingTweaksApplier(TemplateTable templateTable, GlobalTable globalTable, ISptLogger<ShootingTweaksApplier> logger, ModHelper modHelper) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = modHelper.GetJsonDataFromFile<TweaksConfig>(modPath, "config.json");
        ApplyScopeZero(config.ScopeZero);
        ApplyBipod(config.Bipod);
        return Task.CompletedTask;
    }

    private void ApplyScopeZero(ScopeZeroConfig? cfg)
    {
        if (cfg is not { Enabled: true } || cfg.Step <= 0)
        {
            return;
        }

        int scopes = 0, steps = 0;
        foreach (var item in templateTable.Items.Values)
        {
            var props = item.Properties;
            if (props?.CalibrationDistances is null || props.Zooms is null)
            {
                continue;
            }

            var distances = props.CalibrationDistances.Select(list => new List<double>(list)).ToList();
            var zooms = props.Zooms.ToList();
            var changed = false;
            for (var scope = 0; scope < distances.Count && scope < zooms.Count; scope++)
            {
                if (distances[scope].Count == 0 || zooms[scope].Count == 0 || zooms[scope].Max() < cfg.MinZoom)
                {
                    continue;
                }

                for (var distance = distances[scope].Max() + cfg.Step; distance <= cfg.MaxDistance; distance += cfg.Step)
                {
                    distances[scope].Add(distance);
                    steps++;
                    changed = true;
                }
            }

            if (changed)
            {
                props.CalibrationDistances = distances;
                scopes++;
            }
        }

        logger.Info($"[CustomRpTweaks] Scope zero: {scopes} sights with {cfg.MinZoom}x or more got zero distances up to {cfg.MaxDistance} m ({steps} steps added)");
    }

    private void ApplyBipod(BipodConfig? cfg)
    {
        if (cfg is not { Enabled: true })
        {
            return;
        }

        var bipods = 0;
        foreach (var item in templateTable.Items.Values)
        {
            var props = item.Properties;
            if (props?.PitchLimitProneBipod is null && props?.YawLimitProneBipod is null)
            {
                continue;
            }

            if (props!.PitchLimitProneBipod is { } pitch && (cfg.PitchMin is not null || cfg.PitchMax is not null))
            {
                props.PitchLimitProneBipod = new Vector3(Math.Min(pitch.X, (float)(cfg.PitchMin ?? pitch.X)), Math.Max(pitch.Y, (float)(cfg.PitchMax ?? pitch.Y)), pitch.Z);
            }

            if (props.YawLimitProneBipod is { } yaw && cfg.YawLimit is not null)
            {
                props.YawLimitProneBipod = new Vector3(Math.Min(yaw.X, -(float)cfg.YawLimit.Value), Math.Max(yaw.Y, (float)cfg.YawLimit.Value), yaw.Z);
            }

            bipods++;
        }

        var mounting = globalTable.Configuration.MountingSettings;
        var movement = mounting.MovementSettings;
        var detection = mounting.PointDetectionSettings;
        if (cfg.MaxProneMountAngleDotDelta is not null)
        {
            detection.MaxProneMountAngleDotDelta = Math.Min(detection.MaxProneMountAngleDotDelta, cfg.MaxProneMountAngleDotDelta.Value);
        }

        if (cfg.MaxPitchLimitExcess is not null)
        {
            movement.MaxPitchLimitExcess = Math.Max(movement.MaxPitchLimitExcess, cfg.MaxPitchLimitExcess.Value);
        }

        if (cfg.MaxYawLimitExcess is not null)
        {
            movement.MaxYawLimitExcess = Math.Max(movement.MaxYawLimitExcess, cfg.MaxYawLimitExcess.Value);
        }

        if (cfg.PitchLimitHorizontalBipod is not null)
        {
            var current = movement.PitchLimitHorizontalBipod;
            movement.PitchLimitHorizontalBipod = new Vector3(Math.Min(current.X, -(float)cfg.PitchLimitHorizontalBipod.Value), Math.Max(current.Y, (float)cfg.PitchLimitHorizontalBipod.Value), current.Z);
        }

        logger.Info($"[CustomRpTweaks] Bipod: prone limits widened on {bipods} bipods, mounting thresholds loosened");
    }
}

public class ScopeZeroConfig
{
    // off by default; turn on to add zero distances
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    // sights whose highest zoom is at least this get the extra distances
    [JsonPropertyName("minZoom")] public double MinZoom { get; set; } = 6;
    // the longest zero distance to offer, in metres
    [JsonPropertyName("maxDistance")] public double MaxDistance { get; set; } = 800;
    // metres between the added distances, counted from the sight's current longest one
    [JsonPropertyName("step")] public double Step { get; set; } = 50;
}

public class BipodConfig
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    // prone with a bipod: the aim may go down to pitchMin and up to pitchMax degrees (vanilla about -4 and 6), and yawLimit degrees to either side (10 or 15)
    [JsonPropertyName("pitchMin")] public double? PitchMin { get; set; } = -10;
    [JsonPropertyName("pitchMax")] public double? PitchMax { get; set; } = 15;
    [JsonPropertyName("yawLimit")] public double? YawLimit { get; set; } = 30;
    // a prone bipod needs the ground within this dot product of level (vanilla 0.939, about 20 degrees; 0.82 is about 35)
    [JsonPropertyName("maxProneMountAngleDotDelta")] public double? MaxProneMountAngleDotDelta { get; set; } = 0.82;
    // These three also change mounting that is not prone (any mount, side and wall mounts, a bipod on an object), so they are not touched
    // unless a value is given: how many degrees past the mounting limits the aim may be when you mount (vanilla 10 pitch, 25 yaw), and the
    // aim limit up and down for a bipod on an object (vanilla 20)
    [JsonPropertyName("maxPitchLimitExcess")] public double? MaxPitchLimitExcess { get; set; }
    [JsonPropertyName("maxYawLimitExcess")] public double? MaxYawLimitExcess { get; set; }
    [JsonPropertyName("pitchLimitHorizontalBipod")] public double? PitchLimitHorizontalBipod { get; set; }
}
