using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;

namespace CustomRpTweaks;

// The client plugin already stops skills from levelling past your PMC level, but only for experience the game client hands out in raid.
// Everything the server grants itself (hideout crafts, hideout management, repairs, quest and prestige rewards, mod-pushed XP) never
// passes through it. This patches ProfileHelper.AddSkillPointsToPlayer, which all of that goes through, and cuts the profile's progress
// back to the same limit afterwards. A postfix, so it sees the final amount after every other mod's multiplier. Not virtual, hence Harmony.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 5)]
public class SkillCapApplier(ISptLogger<SkillCapApplier> logger, ModHelper modHelper) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var cfg = modHelper.GetJsonDataFromFile<TweaksConfig>(modPath, "config.json").SkillCap ?? new SkillCapConfig();
        if (!cfg.Enabled)
        {
            return Task.CompletedTask;
        }

        // The overload with five parameters is the one the four-parameter one forwards to
        var target = typeof(ProfileHelper).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == nameof(ProfileHelper.AddSkillPointsToPlayer) && m.GetParameters().Length == 5);
        if (target is null)
        {
            logger.Warning("[CustomRpTweaks] Skill cap: ProfileHelper.AddSkillPointsToPlayer (5 parameters) was not found, the server skill cap is off");
            return Task.CompletedTask;
        }

        SkillCapPatches.Enabled = true;
        SkillCapPatches.Overshoot = Math.Clamp(cfg.LevelsAbovePmcLevel, 0, 50);
        SkillCapPatches.Logger = logger;

        new Harmony("com.rpan87.customrptweaks.skillcap").Patch(
            target,
            prefix: new HarmonyMethod(typeof(SkillCapPatches), nameof(SkillCapPatches.Prefix)),
            postfix: new HarmonyMethod(typeof(SkillCapPatches), nameof(SkillCapPatches.Postfix)));

        logger.Info($"[CustomRpTweaks] Skill cap: server-granted skill XP is capped at PMC level + {SkillCapPatches.Overshoot}");
        return Task.CompletedTask;
    }
}

public static class SkillCapPatches
{
    private const double PointsPerLevel = 100;

    internal static bool Enabled;
    internal static int Overshoot;
    internal static ISptLogger<SkillCapApplier>? Logger;

    private static readonly ConcurrentDictionary<string, long> LastLogged = new();

    // Same formula as the client throttle: a skill may reach level (PMC level + overshoot), so its progress stays just under the next level
    public static double MaxProgress(int pmcLevel, int overshoot) => (pmcLevel + overshoot + 1) * PointsPerLevel - 0.01;

    private static CommonSkill? Find(PmcData? profile, SkillTypes skill) => profile?.Skills?.Common?.FirstOrDefault(s => s.Id == skill);

    public static void Prefix(PmcData pmcProfile, SkillTypes skill, out double __state)
    {
        __state = double.NaN;
        try
        {
            __state = Find(pmcProfile, skill)?.Progress ?? double.NaN;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[CustomRpTweaks] Skill cap prefix failed: {e}");
        }
    }

    public static void Postfix(PmcData pmcProfile, SkillTypes skill, double __state)
    {
        try
        {
            if (!Enabled || double.IsNaN(__state) || pmcProfile?.Info?.Side == "Savage" || pmcProfile?.Info?.Level is not { } level)
            {
                return;
            }

            var entry = Find(pmcProfile, skill);
            if (entry is null)
            {
                return;
            }

            // a skill that was already above the limit is never lowered
            var limit = Math.Max(__state, MaxProgress(level, Overshoot));
            if (entry.Progress <= limit)
            {
                return;
            }

            var discarded = entry.Progress - limit;
            entry.Progress = limit;
            entry.PointsEarnedDuringSession = Math.Max(0, entry.PointsEarnedDuringSession - discarded);
            Log(skill, discarded, level);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[CustomRpTweaks] Skill cap postfix failed: {e}");
        }
    }

    // at most once every 30 seconds per skill, a session can hold many grants
    private static void Log(SkillTypes skill, double discarded, int level)
    {
        var now = Environment.TickCount64;
        var key = skill.ToString();
        if (LastLogged.TryGetValue(key, out var last) && now - last < 30_000)
        {
            return;
        }

        LastLogged[key] = now;
        Logger?.Info($"[CustomRpTweaks] Skill cap: {skill} gained {discarded:0.##} XP past PMC level {level} + {Overshoot}, discarded");
    }
}

public class SkillCapConfig
{
    // cap the skill experience the server itself grants (hideout, repairs, quests...) to PMC level + levelsAbovePmcLevel
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    // keep in step with the client setting "Allowed levels above PMC level"
    [JsonPropertyName("levelsAbovePmcLevel")] public int LevelsAbovePmcLevel { get; set; }
}
