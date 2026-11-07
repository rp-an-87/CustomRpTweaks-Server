using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;

namespace CustomRpTweaks;

// Progressive Bot System (PBS) ignores the bot templates that BossGpCoinsApplier edits: it reads how much currency a bot gets, and from which
// whitelist, from its own tier data (BotEquipmentHelper.GetChancesByBotRole). This soft dependency patches that method when PBS is installed
// (found by name, no reference to its DLL; without PBS nothing happens) and gives boss and follower roles the same GP coin draw as the
// applier: one stack with the configured chance, GP coins only. The stack sizes are the ones BossGpCoinsApplier already put in the bot config.
// PBS hands out one shared object for several roles (every role it has no entry for gets "default"), so the patched copy is a clone per
// role and the shared one is never changed.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 2)]
public class PbsGpCoinsPatch(ISptLogger<PbsGpCoinsPatch> logger, ModHelper modHelper) : IOnLoad
{
    private const string PbsHelperType = "ProgressiveBotSystem.Helpers.BotEquipmentHelper";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = modHelper.GetJsonDataFromFile<TweaksConfig>(modPath, "config.json").BossGpCoins;
        if (config is null || !config.Enabled)
        {
            return Task.CompletedTask;
        }

        var helperType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(PbsHelperType, false))
            .FirstOrDefault(type => type is not null);
        if (helperType is null)
        {
            // No PBS: the bot templates edited by BossGpCoinsApplier are what the server reads
            return Task.CompletedTask;
        }

        var target = helperType.GetMethod("GetChancesByBotRole", BindingFlags.Instance | BindingFlags.Public, null, [typeof(string), typeof(int)], null);
        var resultType = target?.ReturnType;
        if (target is null || resultType?.GetProperty("Generation") is null)
        {
            logger.Warning("CustomRpTweaks: Progressive Bot System is installed but its chances method was not found (a new version?), boss GP coins will not drop.");
            return Task.CompletedTask;
        }

        PbsGpCoinsPatches.Config = config;
        PbsGpCoinsPatches.Logger = logger;
        var harmony = new Harmony("com.rpan87.customrptweaks.pbsgpcoins");
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(PbsGpCoinsPatches), nameof(PbsGpCoinsPatches.Postfix)));
        logger.Info("CustomRpTweaks: Progressive Bot System found, boss and follower GP coins are applied to its bot data.");
        return Task.CompletedTask;
    }
}

public static class PbsGpCoinsPatches
{
    private const string GpCoinId = "5d235b4d86f7742e017bc88a";

    internal static BossGpCoinsConfig Config = new();
    internal static ISptLogger<PbsGpCoinsPatch>? Logger;

    private static readonly MethodInfo CloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    // original PBS object -> role -> our copy; PBS reloading its data makes new objects, which simply get new copies
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<string, object>> Copies = new();
    private static bool _failureLogged;

    public static void Postfix(string botRole, ref object __result)
    {
        try
        {
            if (__result is null)
            {
                return;
            }

            var role = botRole.ToLowerInvariant();
            var target = role.Contains("boss") ? Config.Boss : role.Contains("follower") ? Config.Follower : null;
            if (target is null || target.Chance <= 0 || target.Max < 1)
            {
                return;
            }

            var original = __result;
            __result = Copies.GetOrCreateValue(original).GetOrAdd(role, _ => WithGpCoins(original, target));
        }
        catch (Exception e)
        {
            // Never break bot generation over this
            if (!_failureLogged)
            {
                _failureLogged = true;
                Logger?.Warning($"CustomRpTweaks: boss GP coins could not be applied to Progressive Bot System data: {e.Message}");
            }
        }
    }

    private static object Clone(object source) => CloneMethod.Invoke(source, null)!;

    // A copy of PBS's chances object (chances -> Generation -> Items -> Currency) whose currency draw is one GP coin stack with the chance
    private static object WithGpCoins(object chances, GpCoinsTarget target)
    {
        var chance = Math.Clamp(target.Chance, 0, 100);

        var generation = chances.GetType().GetProperty("Generation")!.GetValue(chances)!;
        var items = generation.GetType().GetProperty("Items")!.GetValue(generation)!;
        var currencyProperty = items.GetType().GetProperty("Currency")!;
        var currency = currencyProperty.GetValue(items)!;

        var newCurrency = Clone(currency);
        newCurrency.GetType().GetProperty("Weights")!.SetValue(newCurrency, new Dictionary<double, double> { [0] = 100 - chance, [1] = chance });
        newCurrency.GetType().GetProperty("Whitelist")!.SetValue(newCurrency, new Dictionary<MongoId, double> { [new MongoId(GpCoinId)] = 1 });

        var newItems = Clone(items);
        currencyProperty.SetValue(newItems, newCurrency);

        var newGeneration = Clone(generation);
        newGeneration.GetType().GetProperty("Items")!.SetValue(newGeneration, newItems);

        var newChances = Clone(chances);
        chances.GetType().GetProperty("Generation")!.SetValue(newChances, newGeneration);
        return newChances;
    }
}
