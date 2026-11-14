using HarmonyLib;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Services.InRaid;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;

namespace CustomRpTweaks;

// When a raid ends, SPT replaces the server's PMC skills with the ones the game client sends (serverPmcProfile.Skills = postRaidProfile.Skills).
// The client's copy was loaded before the raid, so anything the server granted in the meantime is lost: crafts that CraftQueue collects on the
// server during the raid, hideout management XP from the generator running. This remembers the skills when a raid starts, works out how much
// the server added by the time the raid ends, and puts exactly that back on top of the client's skills after SPT's merge.
// The amounts are whatever really ended up in the profile (after every multiplier), so nothing is multiplied or capped twice.
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad + 6)]
public class RaidSkillKeeper(ProfileHelper profileHelper, ISptLogger<RaidSkillKeeper> logger, ModHelper modHelper) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var cfg = modHelper.GetJsonDataFromFile<TweaksConfig>(modPath, "config.json").RaidSkills ?? new RaidSkillsConfig();
        if (!cfg.Enabled)
        {
            return Task.CompletedTask;
        }

        var start = AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.StartLocalRaidAsync));
        var end = AccessTools.Method(typeof(LocationLifecycleService), "HandlePostRaidPmc");
        if (start is null || end is null)
        {
            logger.Warning("[CustomRpTweaks] Raid skills: LocationLifecycleService.StartLocalRaidAsync or HandlePostRaidPmc was not found, server skill XP earned during a raid will be lost");
            return Task.CompletedTask;
        }

        RaidSkillPatches.ProfileHelper = profileHelper;
        RaidSkillPatches.Logger = logger;

        var harmony = new Harmony("com.rpan87.customrptweaks.raidskills");
        harmony.Patch(start, prefix: new HarmonyMethod(typeof(RaidSkillPatches), nameof(RaidSkillPatches.StartPrefix)));
        harmony.Patch(end,
            prefix: new HarmonyMethod(typeof(RaidSkillPatches), nameof(RaidSkillPatches.EndPrefix)),
            postfix: new HarmonyMethod(typeof(RaidSkillPatches), nameof(RaidSkillPatches.EndPostfix)));

        logger.Info("[CustomRpTweaks] Raid skills: skill XP the server grants during a raid is kept through the post-raid merge");
        return Task.CompletedTask;
    }
}

public static class RaidSkillPatches
{
    internal static ProfileHelper? ProfileHelper;
    internal static ISptLogger<RaidSkillKeeper>? Logger;

    // session id -> skill progress when that player's last raid started
    private static readonly ConcurrentDictionary<string, Dictionary<SkillTypes, double>> Starts = new();

    // The progress of every skill, by id
    internal static Dictionary<SkillTypes, double> Snapshot(IEnumerable<CommonSkill>? skills) =>
        (skills ?? []).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Progress);

    // What the server added: skills whose progress is higher now than at the start. Never negative.
    internal static Dictionary<SkillTypes, double> Gained(Dictionary<SkillTypes, double> start, IEnumerable<CommonSkill>? now)
    {
        var gained = new Dictionary<SkillTypes, double>();
        foreach (var skill in now ?? [])
        {
            if (start.TryGetValue(skill.Id, out var before) && skill.Progress - before > 1e-9)
            {
                gained[skill.Id] = skill.Progress - before;
            }
        }

        return gained;
    }

    public static void StartPrefix(MongoId sessionId)
    {
        try
        {
            var skills = ProfileHelper?.GetPmcProfile(sessionId)?.Skills?.Common;
            if (skills is not null)
            {
                Starts[sessionId.ToString()] = Snapshot(skills);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[CustomRpTweaks] Raid skills start prefix failed: {e}");
        }
    }

    public static void EndPrefix(MongoId sessionId, SptProfile fullServerProfile, out Dictionary<SkillTypes, double>? __state)
    {
        __state = null;
        try
        {
            if (Starts.TryRemove(sessionId.ToString(), out var start))
            {
                var gained = Gained(start, fullServerProfile.CharacterData?.PmcData?.Skills?.Common);
                __state = gained.Count > 0 ? gained : null;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[CustomRpTweaks] Raid skills end prefix failed: {e}");
        }
    }

    public static void EndPostfix(SptProfile fullServerProfile, Dictionary<SkillTypes, double>? __state)
    {
        try
        {
            var pmc = fullServerProfile.CharacterData?.PmcData;
            if (__state is null || pmc?.Skills?.Common is null)
            {
                return;
            }

            foreach (var (id, gained) in __state)
            {
                var skill = pmc.Skills.Common.FirstOrDefault(s => s.Id == id);
                if (skill is null)
                {
                    continue;
                }

                var before = skill.Progress;
                var target = Math.Min(CommonSkill.MaxSkillProgress, before + gained);

                // the PMC level is already the post-raid one here, so the cap applies to the merged profile
                if (SkillCapPatches.Enabled && pmc.Info?.Level is { } level)
                {
                    target = Math.Min(target, Math.Max(before, SkillCapPatches.MaxProgress(level, SkillCapPatches.Overshoot)));
                }

                if (target <= before)
                {
                    continue;
                }

                skill.Progress = target;
                Logger?.Info($"[CustomRpTweaks] Raid skills: kept {target - before:0.##} {id} XP the server granted during the raid");
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[CustomRpTweaks] Raid skills end postfix failed: {e}");
        }
    }
}

public class RaidSkillsConfig
{
    // keep the skill XP the server grants while a raid is running (CraftQueue collecting crafts, generator XP) through the post-raid merge
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}
