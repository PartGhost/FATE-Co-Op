using System.Collections.Generic;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private string localPortalWorld = "";
    private Vector3 localPortalPosition;

    private void InstallPortalHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(EnterPortal), "Interact"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePortalUse)));
    }

    private static PortalSaveData? PortalEntry()
    {
        var save = SaveData.CurrentSaveData?.GetRealmSave(Fate.GetDungeonTownRealm(RealmData.RealmId.Grove))?.portalSave?.portalSaves;
        var entry = save?.Find(s => s.dungeonRealm == RealmData.RealmId.Grove);
        return entry != null && entry.levelDepth > 0 ? entry : null;
    }

    private void TrackLocalPortal()
    {
        var entry = PortalEntry();
        if (entry == null) { localPortalWorld = ""; return; }
        if (SceneLoad.CurrentScene != Tableflip.Scene.Dungeon || LevelGenerator.currentInstance == null || LevelGenerator.currentInstance.GetLevelDepth() != entry.levelDepth || CurrentWorldKey().Length == 0) return;
        localPortalWorld = CurrentWorldKey();
        localPortalPosition = entry.dungeonPosition;
    }

    private void ShowPartnerPortal(Partner partner)
    {
        var wanted = partner.Frame != null && partner.Frame.portalWorld.Length > 0 && partner.Frame.portalWorld == CurrentWorldKey() && FinitePosition(partner.Frame.portalPosition);
        if (!wanted)
        {
            if (partner.Portal != null) Object.Destroy(partner.Portal);
            partner.Portal = null;
            return;
        }
        var position = partner.Frame!.portalPosition;
        if (partner.Portal != null && (partner.Portal.transform.position - position).sqrMagnitude < 1f) return;
        if (partner.Portal != null) Object.Destroy(partner.Portal);
        partner.Portal = Object.Instantiate(SingletonBehaviour<FateDB>.Instance.portalPrefab, ActiveLevel.AdjustPositionToNavMesh(position, position, performObstructionTest: false), Quaternion.identity);
        partner.Portal.name = "FateCoop_Partner_Portal_" + partner.Id;
    }

    private static void BeforePortalUse(EnterPortal __instance)
    {
        var plugin = Active;
        if (plugin == null || SceneLoad.CurrentScene != Tableflip.Scene.Dungeon || LevelGenerator.currentInstance == null) return;
        foreach (var partner in plugin.partners.Values)
        {
            if (partner.Portal == null || partner.Portal != __instance.gameObject) continue;
            var realm = SaveData.CurrentSaveData.GetRealmSave(Fate.GetDungeonTownRealm(RealmData.RealmId.Grove));
            realm.portalSave ??= new PortalSave { portalSaves = new List<PortalSaveData>() };
            realm.portalSave.portalSaves ??= new List<PortalSaveData>();
            realm.portalSave.portalSaves.RemoveAll(s => s.dungeonRealm == RealmData.RealmId.Grove);
            realm.portalSave.portalSaves.Add(new PortalSaveData { dungeonRealm = RealmData.RealmId.Grove, dungeonPosition = __instance.transform.position, levelDepth = LevelGenerator.currentInstance.GetLevelDepth() });
            return;
        }
    }
}
