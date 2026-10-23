using System;
using System.Collections.Generic;
using HarmonyLib;
using Tableflip;

namespace FateCoop.GameFixes;

public sealed partial class Plugin
{
    private static bool sortingBag;
    private static readonly List<(Stash stash, Item item)> sortedOut = new List<(Stash, Item)>();

    private void InstallBagSortHooks()
    {
        var begin = new HarmonyMethod(typeof(Plugin), nameof(BeforeBagSort));
        var end = new HarmonyMethod(typeof(Plugin), nameof(AfterBagSort));
        harmony!.Patch(AccessTools.Method(typeof(InventoryUI), "AutoSortInventoryForCharacter"), prefix: begin, finalizer: end);
        harmony.Patch(AccessTools.Method(typeof(InventoryUI), "AutoSortInventoryForStash"), prefix: begin, finalizer: end);
        harmony.Patch(AccessTools.Method(typeof(Character), "RemoveItemFromInventory"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeSortedBagRemove)));
        harmony.Patch(AccessTools.Method(typeof(Stash), "OnItemMovedFromStash"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeSortedStashRemove)));
    }

    private static void BeforeBagSort() => sortingBag = true;

    private static Exception? AfterBagSort(Exception __exception)
    {
        sortingBag = false;
        foreach (var (stash, item) in sortedOut) stash.GetItems().Remove(item);
        sortedOut.Clear();
        return __exception;
    }

    private static void BeforeSortedBagRemove(ref bool releaseFromGrid)
    {
        if (sortingBag) releaseFromGrid = false;
    }

    private static bool BeforeSortedStashRemove(Stash __instance, Item item)
    {
        if (!sortingBag) return true;
        sortedOut.Add((__instance, item));
        return false;
    }
}
