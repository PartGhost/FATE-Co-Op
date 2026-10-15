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
    private static readonly FieldInfo SideQuestsField = AccessTools.Field(typeof(QuestManager), "allSideQuests");
    private static readonly FieldInfo RealmQuestsField = AccessTools.Field(typeof(QuestManager), "realmQuests");
    private string sentQuests = "", partyQuests = "";
    private float nextQuestSync;
    private int questRewardsSent, questRewardsTaken;

    [Serializable] private sealed class QuestReward
    {
        public string quest = "";
        public int gold, experience, fame;
        public ItemSaveData item = null!;
    }

    private void InstallQuestHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(Quest), "ClaimRewards"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeClaimRewards)));
        harmony.Patch(AccessTools.Method(typeof(LevelGenerator), "ValidateQuestsPostLoad"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeQuestValidation)));
        harmony.Patch(AccessTools.Method(typeof(WardInteractable), "Interact"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeWardInteract)));
    }

    private static string QuestTitle(Quest quest) => string.Format(quest.QuestName, quest.QuestGiverDisplayName);

    private static void BeforeClaimRewards(Quest __instance)
    {
        var plugin = Active;
        if (plugin == null || !plugin.isHost || !plugin.peerConnected || __instance.Status != Quest.QuestStatus.Completed) return;
        plugin.Send("quest-reward", JsonUtility.ToJson(new QuestReward
        {
            quest = QuestTitle(__instance), gold = __instance.GoldReward, experience = __instance.XPReward, fame = __instance.FameReward,
            item = __instance.ItemReward != null ? __instance.ItemReward.GetItemSave() : new ItemSaveData()
        }));
        plugin.questRewardsSent++;
    }

    private static bool BeforeQuestValidation()
    {
        var plugin = Active;
        if (plugin == null || plugin.isHost || !plugin.sharedSession || (plugin.pendingWorld == null && plugin.joiningWorld == null && !plugin.InHostWorld())) return true;
        LevelGenerator.onQuestValidationComplete?.Invoke();
        return false;
    }

    private static bool BeforeWardInteract(WardInteractable __instance)
    {
        var plugin = Active;
        if (plugin == null || plugin.isHost || !(__instance.GetComponent<Monster>() is Monster ward) || !plugin.monsterIds.TryGetValue(ward, out var id)) return true;
        plugin.Send("use-ward", id.ToString(CultureInfo.InvariantCulture));
        return false;
    }

    private IEnumerable<Quest> ActiveQuests()
    {
        var manager = SingletonBehaviour<QuestManager>.Instance;
        var side = (Dictionary<int, List<Quest>>)SideQuestsField.GetValue(manager);
        var realm = (Dictionary<int, Quest>)RealmQuestsField.GetValue(manager);
        return side.Values.SelectMany(list => list).Concat(realm.Values)
            .Where(q => q != null && (q.Status == Quest.QuestStatus.Accepted || q.Status == Quest.QuestStatus.InProgress || q.Status == Quest.QuestStatus.Completed));
    }

    private void SyncQuests()
    {
        if (Time.unscaledTime < nextQuestSync || !SingletonBehaviour<QuestManager>.InstanceExists() || !SingletonBehaviour<QuestManager>.Instance.HasLoaded) return;
        nextQuestSync = Time.unscaledTime + QuestInterval;
        var text = string.Join("\n", ActiveQuests().Select(q => QuestTitle(q) + (q.Status == Quest.QuestStatus.Completed ? ": done, the host can claim it" : ": floor " + q.TargetDepth)));
        if (text == sentQuests) return;
        Send("quests", text);
        sentQuests = text;
    }

    private void ReceiveQuests(string body)
    {
        if (isHost) throw new InvalidOperationException("Only the host sends party quests.");
        if (body.Length > 4000) throw new InvalidOperationException("Party quest list is too long.");
        partyQuests = body;
    }

    private void TakeQuestReward(string body)
    {
        if (isHost) throw new InvalidOperationException("Only the guest receives shared quest rewards.");
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
        questRewardsTaken++;
        SetStatus("Party quest done: " + reward.quest + ". You got the reward too.");
    }

    private void UseWard(string body)
    {
        if (!isHost) throw new InvalidOperationException("Only the host runs ward interactions.");
        if (monsters.TryGetValue(ParseInt(body), out var ward) && ward != null && ward.TryGetComponent<WardInteractable>(out var interactable)) interactable.Interact(Player.PlayerChar);
    }

    private void ClaimQuestItem(ItemSaveData data)
    {
        if (!data.isQuestItem) return;
        SingletonBehaviour<PlayerStatus>.Instance.OnItemEnterPlayerOwnership?.Invoke(ToItem(data));
    }
}
