using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FateCoop.Core;

public static class GameAssets
{
    private static readonly Dictionary<string, string> ModText = new Dictionary<string, string>();
    private static readonly AccessTools.FieldRef<FateDB, ItemData[]> AllItemsRef = AccessTools.FieldRefAccess<FateDB, ItemData[]>("allItems");
    private static readonly AccessTools.FieldRef<FateDB, int> AllItemsVersionRef = AccessTools.FieldRefAccess<FateDB, int>("allItemsGameVersionIndex");
    private static Transform? holder;

    public static event Action<FateDB>? Ready;

    internal static void Prepare()
    {
        if (holder != null || !SingletonBehaviour<FateDB>.InstanceExists()) return;
        holder = new GameObject("FateCoop_Assets").transform;
        holder.gameObject.SetActive(false);
        Object.DontDestroyOnLoad(holder.gameObject);
        Ready?.Invoke(SingletonBehaviour<FateDB>.Instance);
    }

    public static GameObject Copy(GameObject source, string? name = null)
    {
        var copy = Object.Instantiate(source, holder);
        copy.name = name ?? source.name;
        return copy;
    }

    public static void AddItems(FateDB db, IEnumerable<ItemData> items)
    {
        AllItemsRef(db) = AllItemsRef(db).Concat(items).ToArray();
        AllItemsVersionRef(db) = -1;
    }

    public static ItemData CopyItem(ItemData source, string key, string title)
    {
        var item = Object.Instantiate(source);
        item.name = key;
        item.displayName = key;
        SetText(key, title);
        return item;
    }

    public static void SetText(string key, string text) => ModText[key] = text;

    internal static bool BeforeTextLookup(string key, ref string __result)
    {
        if (key == null || !ModText.TryGetValue(key, out var text)) return true;
        __result = text;
        return false;
    }
}
