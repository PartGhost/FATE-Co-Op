using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Tableflip;
using UnityEngine;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float QuestInterval = 1f;
    private const int QuestRealm = (int)RealmData.RealmId.Grove;
    private static readonly FieldInfo SideQuestsField = AccessTools.Field(typeof(QuestManager), "allSideQuests");
    private static readonly MethodInfo TargetCountSetter = AccessTools.PropertySetter(typeof(Quest), "TargetCount");
    private string sentQuests = "", sharedQuestsJson = "", reportedProgress = "";
    private List<Quest>? ownSideQuests;
    private float nextQuestSync;
    private int questRewardsSent, questRewardsTaken, claimRequester = Unassigned;

    [Serializable] private sealed class QuestList
    {
        public List<QuestSaveData> quests = new List<QuestSaveData>();
    }

    [Serializable] private sealed class QuestProgress
    {
        public string giver = "";
        public int questId, targetCount;
        public Quest.QuestStatus status;
    }

    [Serializable] private sealed class ProgressList
    {
        public List<QuestProgress> entries = new List<QuestProgress>();
    }

    [Serializable] private sealed class QuestReward
    {
        public string quest = "";
        public int gold, experience, fame;
        public ItemSaveData item = null!;
        public List<ItemSaveData> questItems = new List<ItemSaveData>();
    }

    private void InstallQuestHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(Quest), "ClaimRewards"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeClaimRewards)));
        harmony.Patch(AccessTools.Method(typeof(LevelGenerator), "ValidateQuestsPostLoad"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeQuestValidation)));
        harmony.Patch(AccessTools.Method(typeof(WardInteractable), "Interact"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeWardInteract)));
        harmony.Patch(AccessTools.Method(typeof(QuestManager), "LoadSave"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterQuestLoad)));
        harmony.Patch(AccessTools.Method(typeof(QuestManager), "FlushSave"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeQuestSave)), finalizer: new HarmonyMethod(typeof(Plugin), nameof(AfterQuestSave)));
        harmony.Patch(AccessTools.Method(typeof(QuestManager), "AcceptQuest"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeAcceptQuest)));
        harmony.Patch(AccessTools.Method(typeof(QuestManager), "CancelQuest"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeCancelQuest)));
        harmony.Patch(AccessTools.Method(typeof(QuestManager), "ClaimRewards"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeClaimAtGiver)));
        harmony.Patch(AccessTools.Method(typeof(PlayerStatus), "RemoveQuestItem"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeRemoveQuestItem)));
    }

    private static string QuestTitle(Quest quest) => string.Format(quest.QuestName, quest.QuestGiverDisplayName);

    private static QuestManager Quests => SingletonBehaviour<QuestManager>.Instance;

    private static Dictionary<int, List<Quest>> SideQuests => (Dictionary<int, List<Quest>>)SideQuestsField.GetValue(Quests);

    private bool MirroringQuests => !isHost && ownSideQuests != null;

    private static QuestSaveData ToSave(Quest quest)
    {
        var save = new QuestSaveData
        {
            status = quest.Status, questId = quest.QuestId, questgiver = quest.QuestGiverName, questLevel = quest.QuestLevel, goldReward = quest.GoldReward, xpReward = quest.XPReward, fameReward = quest.FameReward,
            heroType = quest.heroItemType, questItem = quest.questItem, targetCount = quest.TargetCount, originalTargetCount = quest.OriginalTargetCount,
            minionMonsterId = quest.MinionData != null ? quest.MinionData.displayName : null, wardInfo = quest.questWard, rewardItem = quest.ItemReward?.GetItemSave(), hasPlayedCompleteSound = quest.hasPlayedCompleteSound
        };
        if (quest.uniqueQuestMonster != null && (quest.questMonsters == null || quest.questMonsters.Count == 0)) save.monsterInfo = new[] { quest.uniqueQuestMonster };
        else if (quest.questMonsters != null && quest.questMonsters.Count > 0) save.monsterInfo = quest.questMonsters.ToArray();
        return save;
    }

    private static QuestProgress ProgressOf(Quest quest) => new QuestProgress { giver = quest.QuestGiverName, questId = quest.QuestId, targetCount = quest.TargetCount, status = quest.Status };

    private static bool Advance(Quest quest, QuestProgress progress)
    {
        if (quest.QuestGiverName != progress.giver || quest.QuestId != progress.questId) return false;
        var changed = false;
        if (quest.Status == Quest.QuestStatus.Accepted && progress.targetCount < quest.TargetCount)
        {
            TargetCountSetter.Invoke(quest, new object[] { progress.targetCount });
            changed = true;
        }
        if (quest.Status == Quest.QuestStatus.Accepted && progress.status == Quest.QuestStatus.Completed)
        {
            quest.Status = Quest.QuestStatus.Completed;
            changed = true;
        }
        return changed;
    }

    private void SyncQuests()
    {
        if (Time.unscaledTime < nextQuestSync || !SingletonBehaviour<QuestManager>.InstanceExists() || !Quests.HasLoaded) return;
        nextQuestSync = Time.unscaledTime + QuestInterval;
        if (isHost)
        {
            var list = JsonUtility.ToJson(new QuestList { quests = (SideQuests.TryGetValue(QuestRealm, out var side) ? side : new List<Quest>()).Where(q => q != null).Select(ToSave).ToList() });
            if (list == sentQuests) return;
            Send("quests", list);
            sentQuests = list;
            return;
        }
        if (!MirroringQuests) return;
        var shared = JsonUtility.FromJson<QuestList>(sharedQuestsJson).quests;
        var ahead = SideQuests[QuestRealm].Where(q => q != null && shared.Any(s => s.questgiver == q.QuestGiverName && s.questId == q.QuestId && (q.TargetCount < s.targetCount || (q.Status == Quest.QuestStatus.Completed && s.status == Quest.QuestStatus.Accepted)))).Select(ProgressOf).ToList();
        var report = JsonUtility.ToJson(new ProgressList { entries = ahead });
        if (ahead.Count == 0 || report == reportedProgress) return;
        Send("quest-progress", report, HostId);
        reportedProgress = report;
    }

    private void ReceiveQuests(string body)
    {
        if (isHost) return;
        var shared = JsonUtility.FromJson<QuestList>(body) ?? throw new InvalidOperationException("Invalid quest list.");
        if (shared.quests.Count > 16) throw new InvalidOperationException("Quest list is too long.");
        sharedQuestsJson = body;
        InstallSharedQuests();
    }

    private void InstallSharedQuests()
    {
        if (isHost || sharedQuestsJson.Length == 0 || !SingletonBehaviour<QuestManager>.InstanceExists() || !Quests.HasLoaded) return;
        var side = SideQuests;
        side.TryGetValue(QuestRealm, out var current);
        if (ownSideQuests == null) ownSideQuests = current ?? new List<Quest>();
        var mirror = JsonUtility.FromJson<QuestList>(sharedQuestsJson).quests.Select(save => new Quest(save, RealmData.RealmId.Grove, isRealmQuest: false)).ToList();
        if (current != null && current != ownSideQuests)
            foreach (var quest in mirror)
                foreach (var local in current.Where(q => q != null)) Advance(quest, ProgressOf(local));
        side[QuestRealm] = mirror;
    }

    private void RestoreOwnQuests()
    {
        if (ownSideQuests != null && SingletonBehaviour<QuestManager>.InstanceExists()) SideQuests[QuestRealm] = ownSideQuests;
        ownSideQuests = null;
        sharedQuestsJson = sentQuests = reportedProgress = "";
    }

    private void MergeProgress(string body)
    {
        if (!isHost) return;
        var reported = JsonUtility.FromJson<ProgressList>(body) ?? throw new InvalidOperationException("Invalid quest progress.");
        foreach (var entry in reported.entries)
            foreach (var quest in SideQuests[QuestRealm].Where(q => q != null)) Advance(quest, entry);
    }

    private static void AfterQuestLoad()
    {
        var plugin = Active;
        if (plugin == null || plugin.isHost || plugin.sharedQuestsJson.Length == 0) return;
        plugin.ownSideQuests = null;
        plugin.InstallSharedQuests();
    }

    private static void BeforeQuestSave(out List<Quest>? __state)
    {
        var plugin = Active;
        __state = plugin != null && plugin.MirroringQuests ? SideQuests[QuestRealm] : null;
        if (__state != null) SideQuests[QuestRealm] = plugin!.ownSideQuests!;
    }

    private static Exception? AfterQuestSave(Exception __exception, List<Quest>? __state)
    {
        if (__state != null) SideQuests[QuestRealm] = __state;
        return __exception;
    }

    private static void BeforeAcceptQuest(Quest quest)
    {
        var plugin = Active;
        if (plugin != null && plugin.MirroringQuests) plugin.Send("quest-accept", JsonUtility.ToJson(ToSave(quest)), HostId);
    }

    private static void BeforeCancelQuest(Quest quest)
    {
        var plugin = Active;
        if (plugin != null && plugin.MirroringQuests) plugin.Send("quest-cancel", JsonUtility.ToJson(ProgressOf(quest)), HostId);
    }

    private static void BeforeClaimAtGiver(string questGiver)
    {
        var plugin = Active;
        var quest = plugin != null && plugin.MirroringQuests ? SideQuests[QuestRealm].FirstOrDefault(q => q != null && q.QuestGiverName == questGiver) : null;
        if (quest != null && quest.Status == Quest.QuestStatus.Completed) plugin!.Send("quest-claim", JsonUtility.ToJson(ProgressOf(quest)), HostId);
    }

    private void TakeQuestAccept(string body)
    {
        if (!isHost) return;
        var save = JsonUtility.FromJson<QuestSaveData>(body);
        if (save == null || string.IsNullOrEmpty(save.questgiver) || Quests.HasQuestFromGiver(save.questgiver)) return;
        Quests.AcceptQuest(new Quest(save, RealmData.RealmId.Grove, isRealmQuest: false), save.questgiver);
    }

    private void TakeQuestCancel(string body)
    {
        if (!isHost) return;
        var progress = JsonUtility.FromJson<QuestProgress>(body);
        var quest = SideQuests[QuestRealm].FirstOrDefault(q => q != null && q.QuestGiverName == progress.giver && q.QuestId == progress.questId);
        if (quest != null) Quests.CancelQuest(quest);
    }

    private void TakeQuestClaim(Partner partner, string body)
    {
        if (!isHost) return;
        var progress = JsonUtility.FromJson<QuestProgress>(body);
        var quest = SideQuests[QuestRealm].FirstOrDefault(q => q != null && q.QuestGiverName == progress.giver && q.QuestId == progress.questId);
        if (quest == null) return;
        Advance(quest, progress);
        claimRequester = partner.Id;
        try { Quests.ClaimRewards(progress.giver); }
        finally { claimRequester = Unassigned; }
    }

    private static void BeforeClaimRewards(Quest __instance)
    {
        var plugin = Active;
        if (plugin == null || !plugin.isHost || !plugin.Connected || __instance.Status != Quest.QuestStatus.Completed || __instance.realmQuestData != null) return;
        var reward = JsonUtility.ToJson(new QuestReward
        {
            quest = QuestTitle(__instance), gold = __instance.GoldReward, experience = __instance.XPReward, fame = __instance.FameReward,
            item = __instance.ItemReward != null ? __instance.ItemReward.GetItemSave() : new ItemSaveData(),
            questItems = new[] { __instance.questItem?.itemSaveData, __instance.uniqueQuestMonster?.targetItem?.itemSaveData }.OfType<ItemSaveData>().Where(data => !string.IsNullOrEmpty(data.baseName)).ToList()
        });
        foreach (var partner in plugin.partners.Values.Where(p => p.Id != plugin.claimRequester)) plugin.Send("quest-reward", reward, partner.Id);
        plugin.questRewardsSent++;
    }

    private static bool BeforeRemoveQuestItem(ItemSaveData targetItem)
    {
        var plugin = Active;
        var player = Player.PlayerChar;
        if (plugin == null || !plugin.Connected || player == null) return true;
        var pet = player.GetPet();
        var stored = SaveData.CurrentSaveData.stashStorage?.items;
        var townStash = UnityEngine.Object.FindFirstObjectByType<Stash>();
        return player.inventory.Any(item => targetItem.MatchesItem(item)) || (pet != null && pet.inventory.Any(item => targetItem.MatchesItem(item)))
            || (townStash != null ? townStash.HasItem(targetItem, out _) : stored != null && stored.Any(item => item.MatchesOtherSave(targetItem)));
    }

    private static bool BeforeQuestValidation()
    {
        var plugin = Active;
        if (plugin == null || (plugin.OwnsWorld && plugin.pendingWorld == null && plugin.joiningWorld == null)) return true;
        LevelGenerator.onQuestValidationComplete?.Invoke();
        return false;
    }

    private static bool BeforeWardInteract(WardInteractable __instance)
    {
        var plugin = Active;
        if (plugin == null || plugin.OwnsWorld || !(__instance.GetComponent<Monster>() is Monster ward) || !plugin.monsterIds.TryGetValue(ward, out var id)) return true;
        plugin.SendToOwner("use-ward", id.ToString(CultureInfo.InvariantCulture));
        return false;
    }

    private void TakeQuestReward(string body)
    {
        var reward = JsonUtility.FromJson<QuestReward>(body);
        if (reward == null || reward.gold < 0 || reward.experience < 0 || reward.fame < 0) throw new InvalidOperationException("Invalid quest reward.");
        var player = Player.PlayerChar;
        if (player == null) return;
        SingletonBehaviour<PlayerStatus>.Instance.AddGold((uint)reward.gold);
        player.AwardExperience(reward.experience, fromMaster: false);
        player.AwardFame((uint)reward.fame);
        SingletonBehaviour<AudioManager>.Instance.PlayAudioClip(AudioData.AudioClipName.Reward, randomizePitch: false);
        if (!string.IsNullOrEmpty(reward.item?.baseName))
        {
            var item = ToItem(reward.item);
            if (!player.AddItemToInventory(item, AddItemReason.Loot))
            {
                dropLocally = true;
                try { item.Drop(player.transform.position); }
                finally { dropLocally = false; }
            }
        }
        foreach (var questItem in reward.questItems.Where(data => player.inventory.Any(owned => data.MatchesItem(owned))))
            SingletonBehaviour<PlayerStatus>.Instance.RemoveQuestItem(questItem);
        questRewardsTaken++;
        Notify("Party quest done: " + reward.quest + ". You got the reward too.");
    }

    private void UseWard(string body)
    {
        if (!OwnsWorld) return;
        if (monsters.TryGetValue(ParseInt(body), out var ward) && ward != null && ward.TryGetComponent<WardInteractable>(out var interactable)) interactable.Interact(Player.PlayerChar);
    }
}
