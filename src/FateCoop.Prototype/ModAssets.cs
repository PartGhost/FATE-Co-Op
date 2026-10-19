using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private static readonly Dictionary<string, string> ModText = new Dictionary<string, string>();
    private static readonly AccessTools.FieldRef<FateDB, ItemData[]> AllItemsRef = AccessTools.FieldRefAccess<FateDB, ItemData[]>("allItems");
    private static readonly AccessTools.FieldRef<FateDB, int> AllItemsVersionRef = AccessTools.FieldRefAccess<FateDB, int>("allItemsGameVersionIndex");
    private Transform? assetHolder;

    private void InstallAssetHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(I2Loca), "Get", new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeTextLookup)));
    }

    private static bool BeforeTextLookup(string key, ref string __result)
    {
        if (key == null || !ModText.TryGetValue(key, out var text)) return true;
        __result = text;
        return false;
    }

    private void PrepareAssets()
    {
        if (assetHolder != null || !SingletonBehaviour<FateDB>.InstanceExists()) return;
        assetHolder = new GameObject("FateCoop_Assets").transform;
        assetHolder.gameObject.SetActive(false);
        Object.DontDestroyOnLoad(assetHolder.gameObject);
        var db = SingletonBehaviour<FateDB>.Instance;
        foreach (var (name, step, required) in new (string, Action<FateDB>, bool)[] { ("portal back face", AddPortalBackFace, false), ("dungeon props", PrepareDungeonProps, false), ("Sagestones", PrepareExperienceGems, true), ("merged run", PrepareRun, true) })
        {
            try { step(db); }
            catch (Exception e)
            {
                Logger.LogError($"Couldn't set up the {name}: {e}");
                if (!required) continue;
                Logger.LogError("FATE Co-Op is turning itself off so the game runs unmodded.");
                harmony?.UnpatchSelf();
                Active = null;
                enabled = false;
                return;
            }
        }
    }

    private GameObject CopyAsset(GameObject source, string? name = null)
    {
        var copy = Object.Instantiate(source, assetHolder);
        copy.name = name ?? source.name;
        return copy;
    }

    private static void AddItems(FateDB db, IEnumerable<ItemData> items)
    {
        AllItemsRef(db) = AllItemsRef(db).Concat(items).ToArray();
        AllItemsVersionRef(db) = -1;
    }

    private static ItemData CopyItem(ItemData source, string key, string title)
    {
        var item = Object.Instantiate(source);
        item.name = key;
        item.displayName = key;
        ModText[key] = title;
        return item;
    }
}
