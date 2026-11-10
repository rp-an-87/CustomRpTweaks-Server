using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using System.Reflection;
using System.Text.Json.Serialization;

namespace CustomRpTweaks;

// The water collector drains its filter at a fixed 0.00333 per second of craft time, whatever the recipe says, so SVM's "water
// filter usage per craft" setting (the recipe's resource requirement) has no effect. This patches
// HideoutHelper.GetWaterFilterDrainRate (not virtual, hence Harmony) so one craft uses recipe resource * (1 - Hideout Management
// reduction) filter points: the recipe value is the cost at skill level 0 and the skill lowers it once. The drain per second is
// that cost divided by the craft's real (skill-shortened) production time, so the Crafting skill and the shorter craft don't
// reduce it a second time as in vanilla. Recipe values are read on each call, so they can come from SVM.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 3)]
public class WaterFilterFix(HideoutTable hideoutTable, GlobalTable globalTable, ISptLogger<WaterFilterFix> logger, ModHelper modHelper) : IOnLoad
{
    private static readonly MongoId WaterRecipe = new("5d5589c1f934db045e6c5492");

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

        logger.Info("[CustomRpTweaks] Water filter: one craft uses the recipe's filter usage, reduced once by Hideout Management");
        return Task.CompletedTask;
    }

    // drain per second, or null when the recipe can't be read (vanilla result stays)
    public double? DrainPerSecond(PmcData pmcData)
    {
        var recipe = hideoutTable.Production?.Recipes?.FirstOrDefault(r => r.Id == WaterRecipe);
        var resource = recipe?.Requirements?.FirstOrDefault(r => r.Type == "Resource")?.Resource;
        // the craft's own time already has the skills applied; fall back to the recipe time
        var seconds = pmcData.Hideout?.Production?.GetValueOrDefault(WaterRecipe)?.ProductionTime ?? recipe?.ProductionTime;
        if (resource is null or <= 0 || seconds is null or <= 0)
        {
            return null;
        }

        var reduction = pmcData.GetSkillBonusMultipliedBySkillLevel(
            SkillTypes.HideoutManagement,
            globalTable.Configuration.SkillsSettings.HideoutManagement.ConsumptionReductionPerLevel);
        return Calc(resource.Value, seconds.Value, reduction);
    }

    public static double Calc(double resource, double craftSeconds, double reduction)
    {
        return resource * Math.Max(0, 1 - reduction) / craftSeconds;
    }
}

public static class WaterFilterPatches
{
    public static void DrainPostfix(PmcData pmcData, ref double __result)
    {
        try { __result = WaterFilterFix.Instance?.DrainPerSecond(pmcData) ?? __result; }
        catch (Exception e) { Console.Error.WriteLine($"[CustomRpTweaks] Water filter patch failed: {e}"); }
    }
}

public class WaterFilterConfig
{
    // one craft uses the recipe's filter usage (SVM "water filter usage"), reduced once by Hideout Management
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
}
