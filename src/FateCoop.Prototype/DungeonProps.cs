using System;
using System.Collections.Generic;
using System.Linq;
using Cleverous.VaultInventory;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const int CrystalGemChance = 35, CartGoldMin = 2, CartGoldMax = 6, CampfireRestorePercent = 50;
    private const float TitleClearance = 1f, CampfireReach = 2.5f, SamePlace = 0.01f;
    private const string BreakParticle = "PS_BoulderBreak", CloneSuffix = "(Clone)";
    private const AudioData.AudioClipName BreakClip = AudioData.AudioClipName.BoulderImpact;
    private static readonly (string Mesh, FixtureKind Kind, string Title)[] PropFixtures =
    {
        ("SM_CrystalCluster", FixtureKind.Crystal, "Crystal Cluster"), ("SM_VulcanoCrystal_1", FixtureKind.Crystal, "Volcanic Crystal"),
        ("SM_VulcanoCrystal_2", FixtureKind.Crystal, "Volcanic Crystal"), ("SM_MineCartGold_01", FixtureKind.GoldCart, "Gold Cart"), ("SM_CampfirePot", FixtureKind.Campfire, "Campfire")
    };
    private static readonly Dictionary<string, Fixture> FixturesByProp = new Dictionary<string, Fixture>();
    private static readonly HashSet<string> Campfires = new HashSet<string>();
    private static readonly string[] CampfireEffects = { "HEALTHMAX", "MANAMAX" };
    private static readonly AccessTools.FieldRef<LevelGenerator, GameObject> PropsParentRef = AccessTools.FieldRefAccess<LevelGenerator, GameObject>("activeGameObjectsParent");
    private static readonly Func<LevelGenerator, int> ItemDepth = AccessTools.MethodDelegate<Func<LevelGenerator, int>>(AccessTools.PropertyGetter(typeof(LevelGenerator), "GetAdjustedItemDepth"));
    private static readonly AccessTools.FieldRef<InteractableTooltipBase, InteractableTooltipCanvas> TooltipCanvasRef = AccessTools.FieldRefAccess<InteractableTooltipBase, InteractableTooltipCanvas>("tooltipCanvas");
    private static readonly AccessTools.FieldRef<FountainInteractable, string> FountainNameRef = AccessTools.FieldRefAccess<FountainInteractable, string>("fountainName");
    private static readonly AccessTools.FieldRef<FountainInteractable, EffectData[]> FountainEffectsRef = AccessTools.FieldRefAccess<FountainInteractable, EffectData[]>("fountainEffects");
    private static readonly AccessTools.FieldRef<FountainInteractable, GameObject[]> FountainToggledRef = AccessTools.FieldRefAccess<FountainInteractable, GameObject[]>("toggledOnInteract");
    private static readonly System.Reflection.MethodInfo InteractRangeSetter = AccessTools.PropertySetter(typeof(InteractableSceneObject), "InteractRange");

    private enum FixtureKind { Crystal, GoldCart, Campfire }

    private sealed class Fixture
    {
        internal FixtureKind Kind;
        internal ItemData Item = null!;
        internal GameObject Container = null!;
    }

    private void InstallDungeonPropHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(LevelGenerator), "OnFinishCreation"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterLevelFinished)));
        harmony.Patch(AccessTools.Method(typeof(ContainerInteractable), "Init"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterChestChanged)));
        harmony.Patch(AccessTools.Method(typeof(ContainerInteractable), "Interact"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterChestChanged)));
        harmony.Patch(AccessTools.Method(typeof(FountainInteractable), "Awake"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterFountainAwake)));
    }

    private void PrepareDungeonProps(FateDB db)
    {
        var breakable = db.containerPrefabs.First(p => p.TryGetComponent<ContainerDestroyable>(out _));
        var chest = db.containerPrefabs.First(p => p.TryGetComponent<ContainerInteractable>(out _));
        var props = Resources.FindObjectsOfTypeAll<PropData>().Where(p => p.modelPrefab != null).ToList();
        var fountain = props.Select(p => p.modelPrefab.GetComponent<FountainInteractable>()).First(f => f != null && CampfireEffects.All(name => FountainEffectsRef(f).Any(e => e.name == name)));
        var items = new List<ItemData>();
        foreach (var prefab in props.Select(p => p.modelPrefab).Distinct())
        {
            var meshes = prefab.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null).Select(f => f.sharedMesh.name).ToList();
            var fixture = PropFixtures.FirstOrDefault(f => meshes.Contains(f.Mesh));
            if (fixture.Mesh == null) continue;
            if (fixture.Kind == FixtureKind.Campfire)
            {
                var campfire = MakeCampfire(prefab, fountain, fixture.Title);
                foreach (var prop in props.Where(p => p.modelPrefab == prefab)) prop.modelPrefab = campfire;
                continue;
            }
            var suffix = prefab.name.Replace("P_", "").Replace("_", "");
            var item = CopyItem(db.AllItems.First(i => i.itemType == (fixture.Kind == FixtureKind.Crystal ? ItemType.Container : ItemType.ChestMedium)), "Item_" + suffix, fixture.Title);
            item.reserved = true;
            items.Add(item);
            var container = fixture.Kind == FixtureKind.Crystal ? MakeCrystal(prefab, breakable, "P_" + suffix) : MakeGoldCart(prefab, chest, "P_" + suffix);
            FixturesByProp[prefab.name] = new Fixture { Kind = fixture.Kind, Item = item, Container = container };
        }
        AddItems(db, items);
        db.containerPrefabs = db.containerPrefabs.Concat(FixturesByProp.Values.Select(f => f.Container)).ToArray();
    }

    private GameObject MakeCrystal(GameObject prop, GameObject breakable, string name)
    {
        var crystal = CopyAsset(prop, name);
        crystal.layer = breakable.layer;
        var template = breakable.GetComponent<ContainerDestroyable>();
        var material = crystal.GetComponent<MeshRenderer>().sharedMaterial;
        var size = crystal.GetComponent<MeshFilter>().sharedMesh.bounds.extents.y / breakable.GetComponent<MeshFilter>().sharedMesh.bounds.extents.y;
        var destroyable = crystal.AddComponent<ContainerDestroyable>();
        destroyable.simulatedObject = Shards(template.simulatedObject, material, size);
        Object.Instantiate(SingletonBehaviour<FateDB>.Instance.allSpells.Where(s => s != null).Select(s => s.particleImpactPrefab).First(p => p != null && p.name == BreakParticle), destroyable.simulatedObject.transform);
        destroyable.simulatedObject.AddComponent<BreakSound>().Clip = BreakClip;
        destroyable.destroyedObject = Shards(template.destroyedObject, material, size);
        AddTitle<ContainerDestroyableTooltip>(crystal, breakable);
        return crystal;
    }

    private GameObject Shards(GameObject source, Material material, float size)
    {
        var shards = CopyAsset(source);
        shards.transform.localScale *= size;
        foreach (var renderer in shards.GetComponentsInChildren<Renderer>(true)) renderer.sharedMaterials = renderer.sharedMaterials.Select(_ => material).ToArray();
        return shards;
    }

    private GameObject MakeGoldCart(GameObject prop, GameObject chest, string name)
    {
        var cart = CopyAsset(prop, name);
        cart.layer = chest.layer;
        cart.AddComponent<ContainerInteractable>();
        AddTitle<ContainerTooltip>(cart, chest);
        return cart;
    }

    private GameObject MakeCampfire(GameObject prop, FountainInteractable wellness, string title)
    {
        var campfire = CopyAsset(prop);
        campfire.layer = wellness.gameObject.layer;
        var fountain = campfire.AddComponent<FountainInteractable>();
        FountainNameRef(fountain) = "Item_" + prop.name.Replace("P_", "");
        ModText[FountainNameRef(fountain)] = title;
        Campfires.Add(FountainNameRef(fountain));
        FountainEffectsRef(fountain) = FountainEffectsRef(wellness).Where(e => CampfireEffects.Contains(e.name))
            .Select(e => new EffectData(e) { value = CampfireRestorePercent, valueBonus = 0f }).ToArray();
        FountainToggledRef(fountain) = campfire.GetComponentsInChildren<ParticleSystem>(true).Where(p => p.transform.parent == campfire.transform).Select(p => p.gameObject).ToArray();
        AddTitle<InteractableTooltip>(campfire, wellness.gameObject);
        return campfire;
    }

    private static void AddTitle<T>(GameObject target, GameObject source) where T : InteractableTooltipBase
    {
        var canvas = Object.Instantiate(source.GetComponentInChildren<InteractableTooltipCanvas>(true).gameObject, target.transform);
        canvas.transform.localPosition = Vector3.up * (target.GetComponent<MeshFilter>().sharedMesh.bounds.max.y + TitleClearance);
        TooltipCanvasRef(target.AddComponent<T>()) = canvas.GetComponent<InteractableTooltipCanvas>();
    }

    private static Fixture? FixtureOf(Item? item) => item == null ? null : FixturesByProp.Values.FirstOrDefault(f => f.Item.displayName == item.itemBaseName);

    private static void AfterLevelFinished(LevelGenerator __instance)
    {
        var containers = __instance.GetActiveContainersParent;
        var placed = containers.GetComponentsInChildren<ContainerDestroyable>().Select(c => (c.transform.position, c.Item))
            .Concat(containers.GetComponentsInChildren<ContainerInteractable>().Select(c => (c.transform.position, c.Item)))
            .Where(c => FixtureOf(c.Item) != null).Select(c => c.position).ToList();
        foreach (var prop in PropsParentRef(__instance).transform.Cast<Transform>().ToList())
        {
            if (!FixturesByProp.TryGetValue(prop.name.Replace(CloneSuffix, ""), out var fixture)) continue;
            if (!placed.Any(position => (position - prop.position).sqrMagnitude < SamePlace)) PlaceFixture(__instance, fixture, prop);
            Object.Destroy(prop.gameObject);
        }
    }

    private static void PlaceFixture(LevelGenerator level, Fixture fixture, Transform prop)
    {
        var placed = Object.Instantiate(fixture.Container, prop.position, prop.rotation, level.GetActiveContainersParent);
        if (fixture.Kind == FixtureKind.Crystal)
        {
            var crystal = placed.GetComponent<ContainerDestroyable>();
            crystal.Item = new Item(fixture.Item);
            crystal.Init(false, 0, TrapType.TrapNone, Random.Range(0, 100) < CrystalGemChance ? new List<Item> { new Item(level.GetGemItemDataRandom()) } : new List<Item>());
            return;
        }
        var depth = ItemDepth(level);
        var cart = placed.GetComponent<ContainerInteractable>();
        cart.Item = new Item(fixture.Item);
        cart.Init(false, Random.Range(CartGoldMin * depth, CartGoldMax * depth + 1), TrapType.TrapNone, new List<Item>(), false);
    }

    private static void AfterChestChanged(ContainerInteractable __instance)
    {
        if (!__instance.IsOpen || FixtureOf(__instance.Item)?.Kind != FixtureKind.GoldCart) return;
        foreach (Transform part in __instance.transform) part.gameObject.SetActive(false);
    }

    private static void AfterFountainAwake(FountainInteractable __instance)
    {
        if (Campfires.Contains(FountainNameRef(__instance))) InteractRangeSetter.Invoke(__instance, new object[] { CampfireReach });
    }
}

public sealed class BreakSound : MonoBehaviour
{
    public AudioData.AudioClipName Clip;

    private void Awake() => SingletonBehaviour<AudioManager>.Instance.PlayAudioClip(Clip, randomizePitch: true);
}
