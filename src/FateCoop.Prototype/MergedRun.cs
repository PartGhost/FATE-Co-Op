using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Tableflip;
using UnityEngine;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const int StoryFloorSpread = 2;
    private static readonly (RealmData.RealmId Realm, string EndKey)[] RunRealms =
    {
        (RealmData.RealmId.Grove, "GROVE"), (RealmData.RealmId.Druantia, "DRUANTIA"), (RealmData.RealmId.Typhon, "TYPHON"),
        (RealmData.RealmId.TempleOfFate, "TEMPLE"), (RealmData.RealmId.ChamberOfTrials, "FINALTRIAL"),
        (RealmData.RealmId.CavernsLivingFire, "VOLCANO"), (RealmData.RealmId.TkalaCrypt, "TOMB"), (RealmData.RealmId.HauntedPalace, "TKALASDEAD"),
        (RealmData.RealmId.Prometheus, "PROMETHEUS")
    };
    private static int ownedDlcs;
    private static readonly List<(RealmData.RealmId realm, int offset, int lastStory)> RunSections = new List<(RealmData.RealmId, int, int)>();
    private static readonly HashSet<string> RunStoryKeys = new HashSet<string>();
    private static readonly AccessTools.FieldRef<MoveUpOrDownDungeon, int> StairsDepthRef = AccessTools.FieldRefAccess<MoveUpOrDownDungeon, int>("levelDepth");

    private void InstallRunHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(LevelGenerator), "Create"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeRunFloorCreate)) { priority = Priority.First });
        harmony.Patch(AccessTools.Method(typeof(LevelGenerator), "UpdateRandomizers"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeRunRandomizers)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(LevelGenerator), "GetAdjustedItemDepth"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterRunItemDepth)));
        harmony.Patch(AccessTools.Method(typeof(Fate), nameof(Fate.GetDungeonTownRealm)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterRunTownRealm)));
        harmony.Patch(AccessTools.Method(typeof(MoveUpOrDownDungeon), "Interact"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeRunStairs)));
        harmony.Patch(AccessTools.Method(typeof(PortalManager), "TryCreatePortal"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterRunTownPortal)));
        harmony.Patch(AccessTools.Method(typeof(EnterPortal), nameof(EnterPortal.SetDestination)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterRunPortalDestination)));
        harmony.Patch(AccessTools.Method(typeof(TutorialStatus), nameof(TutorialStatus.TryGetContextualTip)), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeRunStoryTip)));
        harmony.Patch(AccessTools.Method(typeof(QuestManager), nameof(QuestManager.ReachedTown)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterRunTownReached)));
    }

    private static bool BeforeRunStoryTip(TutorialTipType tipType, ref string key, ref bool __result)
    {
        if (tipType != TutorialTipType.RealmQuest || !InRun(RunSections[0].realm)) return true;
        key = null!;
        __result = false;
        return false;
    }

    private void PrepareRun(FateDB db)
    {
        RunSections.Clear();
        var offset = 0;
        foreach (var (realm, endKey) in RunRealms)
        {
            var lastStory = db.realmQuests.Where(quest => quest != null && quest.targetRealm == realm && quest.keysToProgress != null && quest.keysToProgress.Contains(endKey)).Max(quest => quest.depth);
            RunSections.Add((realm, offset, lastStory));
            offset += lastStory + StoryFloorSpread;
        }
        RunStoryKeys.Clear();
        RunStoryKeys.UnionWith(db.realmQuests.Where(quest => quest != null && InRunDungeonStory(quest) && quest.keysToProgress != null).SelectMany(quest => quest.keysToProgress));
        ownedDlcs = Enum.GetValues(typeof(Fate.DLC)).Cast<Fate.DLC>().Where(dlc => dlc != Fate.DLC.Invalid && SteamManager.Instance.HasEntitlement(dlc)).Aggregate(0, (mask, dlc) => mask | DlcBit(dlc));
    }

    private static int DlcBit(Fate.DLC dlc) => 1 << (int)dlc;

    private static bool RunRealmOpen(RealmData.RealmId realm)
    {
        var (isDlc, dlc) = SingletonBehaviour<DLCManager>.Instance.GetDLCInfoForRealm(realm);
        return !isDlc || ((ownedDlcs & DlcBit(dlc)) != 0 && (Active == null || Active.partners.Values.All(partner => partner.Frame == null || (partner.Frame.dlcs & DlcBit(dlc)) != 0)));
    }

    private static bool InRunDungeonStory(RealmQuest quest) => RunSections.Any(section => section.realm == quest.targetRealm && quest.depth <= section.lastStory);

    private static bool InRunStory(RealmQuest quest) => InRunDungeonStory(quest) || (quest.depth == 0 && quest.keysRequired != null && quest.keysRequired.All(RunStoryKeys.Contains));

    private static bool RunStoryOpen(RealmQuest quest) => quest.depth > 0
        ? SaveData.CurrentSaveData.GetRealmSave(quest.targetRealm).maxDepthReached > 0
        : SingletonBehaviour<QuestManager>.Instance.HasFlags(quest.keysRequired);

    private static void AfterRunTownReached()
    {
        if (!InRun(RunSections[0].realm)) return;
        foreach (var (realm, _, _) in RunSections.Skip(1))
        {
            var quest = SingletonBehaviour<QuestManager>.Instance.GetRealmQuest(realm);
            if (quest != null && quest.QuestType == QuestData.QuestType.EscortOut) quest.OnReachedTown();
        }
    }

    private static bool InRun(RealmData.RealmId realm) => Fate.CurrentFateVer == Fate.FateVer.Original && RunSections.Any(section => section.realm == realm);

    private static int RunOffset(RealmData.RealmId realm) => RunSections.First(section => section.realm == realm).offset;

    private static int RunFloor(RealmData.RealmId realm, int depth) => InRun(realm) ? RunOffset(realm) + depth : depth;

    private static int RunFloorHere() => LevelGenerator.currentInstance != null ? RunFloor(SingletonBehaviour<PlayerStatus>.Instance.DungeonRealm, LevelGenerator.currentInstance.GetLevelDepth()) : 0;

    private static (RealmData.RealmId realm, int depth) RunPlace(int runFloor)
    {
        var section = RunSections.Where(s => s.offset < runFloor && RunRealmOpen(s.realm)).DefaultIfEmpty(RunSections[0]).Last();
        return (section.realm, runFloor - section.offset);
    }

    private static string RunFloorName(int runFloor)
    {
        var (realm, depth) = RunPlace(runFloor);
        return realm == RunSections[0].realm ? "Floor " + depth : Fate.GetRealmName(realm, preferShortName: false) + " " + depth;
    }

    private static void EnterFloorFromTown(RealmData.RealmId realm, int depth)
    {
        SingletonBehaviour<PlayerStatus>.Instance.DungeonRealm = realm;
        SaveData.CurrentSaveData.GetRealmSave(realm).SetLastVisitedDungeonDepth(depth);
    }

    private static void BeforeRunFloorCreate(ref int newLevelDepth, ref int newLevelRealm)
    {
        if (!InRun((RealmData.RealmId)newLevelRealm)) return;
        var (realm, depth) = RunPlace(RunFloor((RealmData.RealmId)newLevelRealm, newLevelDepth));
        newLevelDepth = depth;
        newLevelRealm = (int)realm;
        SingletonBehaviour<PlayerStatus>.Instance.DungeonRealm = realm;
    }

    private static void BeforeRunRandomizers()
    {
        var realm = SingletonBehaviour<PlayerStatus>.Instance.DungeonRealm;
        if (InRun(realm)) LevelGenerator.effectiveLevelDepth += RunOffset(realm);
    }

    private static void AfterRunItemDepth(ref int __result)
    {
        var status = SingletonBehaviour<PlayerStatus>.Instance;
        if (InRun(status.DungeonRealm)) __result += Mathf.CeilToInt(RunOffset(status.DungeonRealm) * Fate.DifficultyMod[status.Difficulty].itemMultiplier);
    }

    private static void AfterRunTownRealm(RealmData.RealmId dungeonRealm, ref RealmData.RealmId __result)
    {
        if (InRun(dungeonRealm)) __result = RunSections[0].realm;
    }

    private static bool BeforeRunStairs(MoveUpOrDownDungeon __instance, Character other)
    {
        var realm = SingletonBehaviour<PlayerStatus>.Instance.DungeonRealm;
        if (__instance.goesDown || StairsDepthRef(__instance) > 1 || !InRun(realm) || RunOffset(realm) == 0) return true;
        Fate.SetPause(paused: true);
        SingletonBehaviour<PromptUI>.Instance.ShowPrompt("Prompt_AscendStairs", positive =>
        {
            if (!positive)
            {
                Fate.SetPause(paused: false);
                return;
            }
            if (other.IsPlayer()) other.SetInvincible();
            Player.latestStairsUsedWasDown = false;
            LevelGenerator.currentInstance.ReplaceDungeon(0f, 0f, 0, (int)realm, goesDown: false, useSavedPosition: false);
        });
        return false;
    }

    private static void AfterRunPortalDestination(EnterPortal __instance, RealmData.RealmId dungeonRealm)
    {
        if (!InRun(dungeonRealm) || dungeonRealm == RunSections[0].realm || !SingletonBehaviour<Town>.InstanceExists()) return;
        InteractableNameSetter.Invoke(__instance, new object[] { "Return To " + Fate.GetRealmName(dungeonRealm, preferShortName: false) + " Level " + __instance.LevelDepth });
        __instance.onDestinationSet?.Invoke();
    }

    private static void AfterRunTownPortal(bool __result)
    {
        var realm = SingletonBehaviour<PlayerStatus>.Instance.DungeonRealm;
        if (!__result || !InRun(realm)) return;
        SaveData.CurrentSaveData.GetRealmSave(Fate.GetDungeonTownRealm(realm)).portalSave.portalSaves.RemoveAll(entry => entry.dungeonRealm != realm && InRun(entry.dungeonRealm));
    }
}
