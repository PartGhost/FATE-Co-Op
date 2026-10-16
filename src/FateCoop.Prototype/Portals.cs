using System.Linq;
using System.Reflection;
using Cleverous.VaultInventory;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float TownPortalSpacing = 2.5f;
    private static readonly MethodInfo PortalSpawnPointMethod = AccessTools.Method(typeof(Town), "GetPortalSpawnPoint");
    private static readonly MethodInfo InteractableNameSetter = AccessTools.PropertySetter(typeof(InteractableSceneObject), "InteractableName");
    private string localPortalWorld = "";

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
        if (SceneLoad.CurrentScene == Tableflip.Scene.Dungeon && LevelGenerator.currentInstance != null && LevelGenerator.currentInstance.GetLevelDepth() == entry.levelDepth && CurrentWorldKey().Length > 0)
            localPortalWorld = CurrentWorldKey();
    }

    private void ShowPartnerPortal(Partner partner)
    {
        var frame = partner.Frame;
        var inDungeon = frame != null && frame.portalWorld.Length > 0 && frame.portalWorld == CurrentWorldKey() && FinitePosition(frame.portalPosition);
        partner.Portal = PlacePortal(partner.Portal, inDungeon ? frame!.portalPosition : (Vector3?)null, partner);
        var inTown = frame != null && frame.portalDepth > 0 && SceneLoad.CurrentScene == Tableflip.Scene.Town_Grove && SingletonBehaviour<Town>.InstanceExists();
        partner.TownPortal = PlacePortal(partner.TownPortal, inTown ? TownPortalSpot(partner) : (Vector3?)null, partner);
        if (partner.TownPortal == null) return;
        var portal = partner.TownPortal.GetComponent<EnterPortal>();
        portal.destinationRealm = RealmData.RealmId.Grove;
        InteractableNameSetter.Invoke(portal, new object[] { partner.Name + ": Floor " + frame!.portalDepth });
        portal.onDestinationSet?.Invoke();
    }

    private Vector3 TownPortalSpot(Partner partner)
    {
        var town = SingletonBehaviour<Town>.Instance;
        var spot = town.possibleSpawnPoints[(int)PortalSpawnPointMethod.Invoke(town, new object[] { RealmData.RealmId.Grove })].position;
        var slot = partners.Keys.ToList().IndexOf(partner.Id) + 1;
        return spot + Vector3.right * TownPortalSpacing * slot;
    }

    private static GameObject? PlacePortal(GameObject? existing, Vector3? position, Partner partner)
    {
        if (position == null)
        {
            if (existing != null) Object.Destroy(existing);
            return null;
        }
        if (existing != null && (existing.transform.position - position.Value).sqrMagnitude < 1f) return existing;
        if (existing != null) Object.Destroy(existing);
        var portal = Object.Instantiate(SingletonBehaviour<FateDB>.Instance.portalPrefab, ActiveLevel.AdjustPositionToNavMesh(position.Value, position.Value, performObstructionTest: false), Quaternion.identity);
        portal.name = "FateCoop_Partner_Portal_" + partner.Id;
        return portal;
    }

    private static bool BeforePortalUse(EnterPortal __instance)
    {
        var plugin = Active;
        var partner = plugin?.partners.Values.FirstOrDefault(p => p.TownPortal != null && p.TownPortal == __instance.gameObject);
        if (partner?.Frame == null) return true;
        var depth = partner.Frame.portalDepth;
        var arrival = partner.Frame.portalPosition;
        SingletonBehaviour<PromptUI>.Instance.ShowPromptRaw("Go through " + partner.Name + "'s portal to Floor " + depth + "?", yes =>
        {
            if (!yes) return;
            plugin!.arrivePoint = arrival;
            Player.isEnteringDungeonFromTownDoor = true;
            SingletonBehaviour<PlayerStatus>.Instance.DungeonRealm = RealmData.RealmId.Grove;
            SaveData.CurrentSaveData.GetRealmSave(RealmData.RealmId.Grove).SetLastVisitedDungeonDepth(depth);
            SceneLoad.LoadAsync(Tableflip.Scene.Dungeon, true);
        }, twoButtons: true, "Yes", "No");
        return false;
    }
}
