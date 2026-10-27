using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using System.Reflection;
using System.Text.Json.Serialization;

namespace CustomRpTweaks;

// The water collector drains its filter at a fixed 0.00333 per second of craft time, whatever the recipe says, so SVM's "water
// filter usage per craft" setting (the recipe's resource requirement) has no effect and the real drain only follows the craft
// time. This patches HideoutHelper.GetWaterFilterDrainRate (not virtual, hence Harmony) so the drain per second becomes
// recipe resource / recipe production time: one craft then uses exactly that many filter points, at any craft time. The skill
// reductions the vanilla method applies stay as they are. Both recipe values are read on each call, so they can come from SVM.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 3)]
public class WaterFilterFix(HideoutTable hideoutTable, ISptLogger<WaterFilterFix> logger, ModHelper modHelper) : IOnLoad
{
    private const string WaterRecipeId = "5d5589c1f934db045e6c5492";
    // the vanilla method's constant: 100 points in 8 h 20 min
    private const double VanillaDrainPerSecond = 0.00333d;

    public static WaterFilterFix? Instance { get; private set; }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var cfg = modHelper.GetJsonDataFromFile<TweaksConfig>(modPath, "config.json").WaterFilter ?? new WaterFilterConfig();
        if (!cfg.Enabled)
        {
            return Task.CompletedTask;
        }

        Instance = this;
        new Harmony("com.rpan87.customrptweaks.waterfilter").Patch(
            AccessTools.Method(typeof(HideoutHelper), "GetWaterFilterDrainRate"),
            postfix: new HarmonyMethod(typeof(WaterFilterPatches), nameof(WaterFilterPatches.DrainPostfix)));

        logger.Info("[CustomRpTweaks] Water filter drain follows the recipe's filter usage per craft");
        return Task.CompletedTask;
    }

    // factor to apply to the vanilla drain, 1 when the recipe can't be read
    public double Factor()
    {
        var recipe = hideoutTable.Production?.Recipes?.FirstOrDefault(r => r.Id == new MongoId(WaterRecipeId));
        var resource = recipe?.Requirements?.FirstOrDefault(r => r.Type == "Resource")?.Resource;
        var seconds = recipe?.ProductionTime;
        if (resource is null or <= 0 || seconds is null or <= 0)
        {
            return 1;
        }

        return resource.Value / seconds.Value / VanillaDrainPerSecond;
    }
}

public static class WaterFilterPatches
{
    public static void DrainPostfix(ref double __result)
    {
        try { __result *= WaterFilterFix.Instance?.Factor() ?? 1; }
        catch (Exception e) { Console.Error.WriteLine($"[CustomRpTweaks] Water filter patch failed: {e}"); }
    }
}

public class WaterFilterConfig
{
    // one craft uses exactly the recipe's filter usage (SVM "water filter usage"), whatever the craft time is
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
}
