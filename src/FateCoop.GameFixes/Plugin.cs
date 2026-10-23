using System;
using System.Linq;
using BepInEx;
using BepInEx.Unity.Mono;
using FateCoop.Core;
using HarmonyLib;
using Tableflip;
using Object = UnityEngine.Object;

namespace FateCoop.GameFixes;

[BepInPlugin(Guid, "FATE Co-Op Game Fixes", SuiteVersion.Value)]
[BepInDependency(CorePlugin.Guid)]
public sealed partial class Plugin : BaseUnityPlugin
{
    public const string Guid = "local.fatecoop.gamefixes";
    private Harmony? harmony;

    private void Awake()
    {
        harmony = new Harmony(Guid);
        try
        {
            InstallGameFixHooks();
            InstallBagSortHooks();
        }
        catch (Exception e)
        {
            Logger.LogError(e);
            harmony.UnpatchSelf();
            return;
        }
        if (!TestChannel.Enabled) return;
        TestChannel.Commands["test-gem-drag"] = parts =>
        {
            var dragger = Player.PlayerChar;
            var armed = dragger.inventory.First(i => i.equipped && i.IsInMainHand);
            armed.SetIdentified(true);
            armed.sockets = Math.Max(armed.sockets, (armed.sockedtedItems?.Count ?? 0) + 1);
            var dragGem = dragger.inventory.Last(i => i.itemType == ItemType.Gem);
            var gemUi = Object.FindObjectsOfType<ItemUI>().First(ui => ui.currentItem == dragGem);
            CollidingSlotsRef(gemUi).Clear();
            CollidingSlotsRef(gemUi).Add(Object.FindObjectsOfType<EquipmentSlotUI>().First(slot => !slot.IsOpen && slot.GetCurrentItemUI.currentItem == armed));
            InventoryUI.draggedItemUI = gemUi;
            gemUi.OnEndDrag(null);
            Logger.LogInfo($"Gem drag: socketed {armed.sockedtedItems?.Contains(dragGem)}, in bag {dragger.inventory.Contains(dragGem)}, on ground {Object.FindObjectsOfType<DroppedItem>().Count(d => d.Item == dragGem)}");
        };
    }

    private void OnDestroy() => harmony?.UnpatchSelf();
}
