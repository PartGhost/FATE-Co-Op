using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Linq;
using HarmonyLib;
using Tableflip;
using UnityEngine;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private static readonly AccessTools.FieldRef<ItemSelectionUI, ItemUI> ChosenItemRef = AccessTools.FieldRefAccess<ItemSelectionUI, ItemUI>("chosenItem");
    private static readonly AccessTools.FieldRef<FateChoice, ulong> FateExpCostRef = AccessTools.FieldRefAccess<FateChoice, ulong>("expCost");
    private static readonly AccessTools.FieldRef<FateChoice, uint> FateFameCostRef = AccessTools.FieldRefAccess<FateChoice, uint>("fameCost");
    private static readonly AccessTools.FieldRef<StatButton, Character> StatButtonCharacterRef = AccessTools.FieldRefAccess<StatButton, Character>("character");
    private static readonly System.Reflection.MethodInfo StatButtonEffectHandler = AccessTools.Method(typeof(StatButton), "OnAppliedEffect");
    private static readonly System.Reflection.MethodInfo TownGetter = AccessTools.PropertyGetter(typeof(SingletonBehaviour<Town>), nameof(SingletonBehaviour<Town>.Instance));
    private static readonly AccessTools.FieldRef<ItemUI, List<ItemSlotUI>> CollidingSlotsRef = AccessTools.FieldRefAccess<ItemUI, List<ItemSlotUI>>("currentCollidingSlots");
    private static bool fateTransport;

    private void InstallGameFixHooks()
    {
        harmony!.Patch(AccessTools.Constructor(typeof(Item), new[] { typeof(ItemData) }), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterItemMade)));
        harmony.Patch(AccessTools.Constructor(typeof(Item), new[] { typeof(Item) }), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterItemMade)));
        harmony.Patch(AccessTools.Method(typeof(Quest), nameof(Quest.OnUniqueMonsterKilled)), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeUniqueKilled)));
        harmony.Patch(AccessTools.Constructor(typeof(Quest), new[] { typeof(QuestSaveData), typeof(RealmData.RealmId), typeof(bool) }), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterQuestRestored)));
        harmony.Patch(AccessTools.Method(typeof(ItemSelectionUI), "OnBreakWeaponsClick"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeBreakWeapons)));
        harmony.Patch(AccessTools.Method(typeof(FateChoice), nameof(FateChoice.ShowFateChoice)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterFateChoiceShown)));
        harmony.Patch(AccessTools.Method(typeof(FateChoice), "OnSecondChoice"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeFateTransport)), finalizer: new HarmonyMethod(typeof(Plugin), nameof(AfterFateTransport)));
        harmony.Patch(AccessTools.Method(typeof(LevelGenerator), "ReplaceDungeon"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeFateReplace)) { priority = Priority.First });
        harmony.Patch(AccessTools.Method(typeof(FateChoice), "OnThirdChoice"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeFateTown)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterFateTown)));
        harmony.Patch(AccessTools.Method(typeof(StatButton), nameof(StatButton.Setup)), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeStatButtonSetup)));
        harmony.Patch(AccessTools.Method(typeof(QuestManager), nameof(QuestManager.AcceptQuest)), transpiler: new HarmonyMethod(typeof(Plugin), nameof(TownCheckWithoutCreating)));
        harmony.Patch(AccessTools.Method(typeof(ItemUI), nameof(ItemUI.OnEndDrag)), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeGemDragEnd)));
        harmony.Patch(AccessTools.Method(typeof(PortalManager), "OnAvatarSpawn"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePortalRespawn)));
    }

    private static void BeforePortalRespawn(PortalManager __instance)
    {
        if (!SingletonBehaviour<Town>.InstanceExists()) __instance.RemoveCurrentActivePortal();
    }

    private static IEnumerable<CodeInstruction> TownCheckWithoutCreating(IEnumerable<CodeInstruction> instructions) =>
        instructions.Select(instruction => instruction.Calls(TownGetter) ? new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Plugin), nameof(ExistingTown))).MoveLabelsFrom(instruction) : instruction);

    private static Town? ExistingTown() => SingletonBehaviour<Town>.InstanceExists() ? SingletonBehaviour<Town>.Instance : null;

    private static bool BeforeGemDragEnd(ItemUI __instance)
    {
        var gem = __instance.currentItem;
        if (InventoryUI.draggedItemUI != __instance || gem == null || gem.itemType != ItemType.Gem || __instance.Owner is Merchant) return true;
        var slot = CollidingSlotsRef(__instance)?.OfType<EquipmentSlotUI>().FirstOrDefault();
        if (slot == null) return true;
        InventoryUI.draggedItemUI = null;
        __instance.canvasGroup.alpha = 1f;
        __instance.canvasGroup.blocksRaycasts = true;
        var target = slot.IsOpen ? null : slot.GetCurrentItemUI;
        if (target == null || target.currentItem.sockets <= (target.currentItem.sockedtedItems?.Count ?? 0))
        {
            __instance.ReturnToOriginalSpot();
            return false;
        }
        target.currentItem.SocketItem(gem);
        target.UpdateSockets();
        if (__instance.Owner == null) SimpleSingletonBehaviour<PlayerUI>.Instance.InventoryUI.RemoveItemFromStash(gem);
        else __instance.Owner.RemoveItemFromInventory(gem, RemoveItemReason.Consumed);
        __instance.ForceClearCurrentSlots();
        __instance.gameObject.SetActive(false);
        gem.PlayLandSound();
        return false;
    }

    private static void AfterItemMade(Item __instance) =>
        __instance.itemRequirements = __instance.itemRequirements?.Select(requirement => new AttributeData { attribute = requirement.attribute, value = requirement.value }).ToList();

    private static bool BeforeUniqueKilled(Quest __instance) => __instance.uniqueQuestMonster != null;

    private static void AfterQuestRestored(Quest __instance) => __instance.questMonsters?.RemoveAll(monster => monster == null);

    private static void BeforeBreakWeapons(ItemSelectionUI __instance)
    {
        var gems = ChosenItemRef(__instance)?.currentItem?.sockedtedItems;
        var player = Player.PlayerChar;
        if (gems == null || player == null) return;
        var free = Enumerable.Range(0, player.InventoryGridHeight * player.InventoryGridWidth).Count(cell => player.IsSpaceAvailable(cell / player.InventoryGridWidth, cell % player.InventoryGridWidth, 1, 1));
        foreach (var gem in gems.ToList())
        {
            var size = gem.slotsTall * gem.slotsWide;
            if (size <= free)
            {
                free -= size;
                continue;
            }
            gems.Remove(gem);
            gem.Drop(player.transform.position);
        }
    }

    private static void AfterFateChoiceShown(FateChoice __instance)
    {
        var player = Player.PlayerChar;
        if (SingletonBehaviour<PlayerStatus>.Instance.Difficulty == Fate.Difficulty.Hardcore || player == null) return;
        var levelStart = Fate.GetExperienceNeeded(player.Level - 1);
        var rankStart = Fate.GetFameNeeded(player.fameRank - 1);
        FateExpCostRef(__instance) = Math.Min(FateExpCostRef(__instance), player.experience > levelStart ? player.experience - levelStart : 0);
        FateFameCostRef(__instance) = Math.Min(FateFameCostRef(__instance), player.fame > rankStart ? player.fame - rankStart : 0);
        __instance.firstOption.Setup(0, I2Loca.GetFormatReplaceTags("FateChoice_CurrentLocation", FateExpCostRef(__instance), FateFameCostRef(__instance)));
    }

    private static void BeforeFateTransport() => fateTransport = true;

    private static Exception? AfterFateTransport(Exception? __exception)
    {
        fateTransport = false;
        return __exception;
    }

    private static void BeforeFateReplace(ref int newLevelRealm)
    {
        if (fateTransport) newLevelRealm = (int)SingletonBehaviour<PlayerStatus>.Instance.DungeonRealm;
    }

    private static void BeforeFateTown(out ulong __state) => __state = SingletonBehaviour<PlayerStatus>.Instance.gold;

    private static void AfterFateTown(ulong __state)
    {
        if (__state <= int.MaxValue || LevelGenerator.currentInstance == null || SingletonBehaviour<PlayerStatus>.Instance.Difficulty == Fate.Difficulty.Hardcore) return;
        for (var left = __state - 1; left > 0; left -= Math.Min(left, int.MaxValue))
            ActiveLevel.SpawnGoldPile(LevelGenerator.currentInstance.RandomOpenPosition(Player.PlayerChar.myTransform.position, 1f, 2f), (int)Math.Min(left, int.MaxValue));
    }

    private static void BeforeStatButtonSetup(StatButton __instance)
    {
        var character = StatButtonCharacterRef(__instance);
        if (character == null) return;
        var handler = (Action<AppliedEffect>)Delegate.CreateDelegate(typeof(Action<AppliedEffect>), __instance, StatButtonEffectHandler);
        character.onEffectApplied -= handler;
        character.onEffectRemoved -= handler;
    }
}
