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

    private void AddPortalBackFace(FateDB db)
    {
        var portal = CopyAsset(db.portalPrefab);
        var front = portal.GetComponentInChildren<SkinnedMeshRenderer>(true);
        Object.Instantiate(front.gameObject, front.transform.parent).AddComponent<PortalBackFace>().Front = front;
        db.portalPrefab = portal;
    }

    private static PortalSaveData? PortalEntry()
    {
        var save = SaveData.CurrentSaveData?.GetRealmSave(RunSections[0].realm)?.portalSave?.portalSaves;
        return save?.Find(s => InRun(s.dungeonRealm) && s.levelDepth > 0);
    }

    private void TrackLocalPortal()
    {
        var entry = PortalEntry();
        if (entry == null) { localPortalWorld = ""; return; }
        if (SceneLoad.CurrentScene == Tableflip.Scene.Dungeon && RunFloorHere() == RunFloor(entry.dungeonRealm, entry.levelDepth) && CurrentWorldKey().Length > 0)
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
        InteractableNameSetter.Invoke(portal, new object[] { partner.Name + ": " + RunFloorName(frame!.portalDepth) });
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
        var runFloor = partner.Frame.portalDepth;
        var arrival = partner.Frame.portalPosition;
        SingletonBehaviour<PromptUI>.Instance.ShowPromptRaw("Go through " + partner.Name + "'s portal to " + RunFloorName(runFloor) + "?", yes =>
        {
            if (!yes) return;
            plugin!.arrivePoint = arrival;
            Player.isEnteringDungeonFromTownDoor = true;
            var (realm, depth) = RunPlace(runFloor);
            EnterFloorFromTown(realm, depth);
            SceneLoad.LoadAsync(Tableflip.Scene.Dungeon, true);
        }, twoButtons: true, "Yes", "No");
        return false;
    }
}

public sealed class PortalBackFace : MonoBehaviour
{
    public SkinnedMeshRenderer Front = null!;
    private SkinnedMeshRenderer back = null!;

    private void Awake() => back = GetComponent<SkinnedMeshRenderer>();

    private void LateUpdate()
    {
        var front = Front.transform;
        transform.localPosition = front.localPosition;
        transform.localScale = front.localScale;
        transform.rotation = Quaternion.AngleAxis(180f, Vector3.up) * front.rotation;
        back.enabled = Front.enabled;
        for (var i = 0; i < back.sharedMesh.blendShapeCount; i++) back.SetBlendShapeWeight(i, Front.GetBlendShapeWeight(i));
    }
}
