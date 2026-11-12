using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Bot;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;
using System.Reflection;
using System.Text.Json.Serialization;

namespace CustomRpTweaks;

// The server rolls how much is left in a bot's medical item or food with GetRandomizedResourceValue, which returns a random decimal
// (a Calok-B with 2.6 of 3 uses). The grid badge rounds that up (3/3) while the inspect window cuts it off (2/3), and the odd value
// stops identical-looking items from stacking. This patches BotGeneratorHelper.GenerateExtraPropertiesForItem (public, but not virtual,
// hence Harmony) to round what it returns to whole numbers. A postfix runs even when another mod (Progressive Bot System randomizes
// resources too) replaces the method body, so both paths are covered.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 4)]
public class WholeResourcesFix(ISptLogger<WholeResourcesFix> logger, ModHelper modHelper) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var cfg = modHelper.GetJsonDataFromFile<TweaksConfig>(modPath, "config.json").WholeResources ?? new WholeResourcesConfig();
        if (!cfg.Enabled)
        {
            return Task.CompletedTask;
        }

        new Harmony("com.rpan87.customrptweaks.wholeresources").Patch(
            AccessTools.Method(typeof(BotGeneratorHelper), nameof(BotGeneratorHelper.GenerateExtraPropertiesForItem)),
            postfix: new HarmonyMethod(typeof(WholeResourcesPatches), nameof(WholeResourcesPatches.Postfix)));

        logger.Info("[CustomRpTweaks] Whole resources: uses left on bot medical items and food are rounded to whole numbers");
        return Task.CompletedTask;
    }
}

public static class WholeResourcesPatches
{
    public static void Postfix(Upd? __result)
    {
        try
        {
            if (__result?.MedKit?.HpResource is { } uses)
            {
                __result.MedKit.HpResource = Whole(uses);
            }

            if (__result?.FoodDrink?.HpPercent is { } left)
            {
                __result.FoodDrink.HpPercent = Whole(left);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[CustomRpTweaks] Whole resources patch failed: {e}");
        }
    }

    // nearest whole number, but never 0 for something that had some left (0 would be a used-up item)
    public static double Whole(double value)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return value > 0 ? Math.Max(1, rounded) : rounded;
    }
}

public class WholeResourcesConfig
{
    // round the uses left on bot-generated medical items and food to whole numbers
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}
