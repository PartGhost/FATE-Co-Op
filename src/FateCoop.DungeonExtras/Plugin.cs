using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Unity.Mono;
using FateCoop.Core;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace FateCoop.DungeonExtras;

[BepInPlugin(Guid, "FATE Co-Op Dungeon Extras", SuiteVersion.Value)]
[BepInDependency(CorePlugin.Guid)]
public sealed partial class Plugin : BaseUnityPlugin
{
    public const string Guid = "local.fatecoop.dungeonextras";
    private Harmony? harmony;

    [Serializable] private sealed class Status
    {
        public string fixtures = "";
    }

    private void Awake()
    {
        harmony = new Harmony(Guid);
        try
        {
            InstallDungeonPropHooks();
            InstallExperienceGemHooks();
        }
        catch (Exception e)
        {
            Logger.LogError(e);
            harmony.UnpatchSelf();
            return;
        }
        GameAssets.Ready += PrepareContent;
        SharedContent.Register(Info.Metadata);
        if (TestChannel.Enabled) AddTestHooks();
    }

    private void PrepareContent(FateDB db)
    {
        try { PrepareDungeonProps(db); }
        catch (Exception e) { Logger.LogError($"Couldn't set up the dungeon props: {e}"); }
        try { PrepareExperienceGems(db); }
        catch (Exception e)
        {
            Logger.LogError($"Couldn't set up the Sagestones, turning Dungeon Extras off: {e}");
            harmony?.UnpatchSelf();
        }
    }

    private void AddTestHooks()
    {
        TestChannel.Commands["test-fixture"] = parts =>
        {
            var visitor = Player.PlayerChar;
            var kind = (FixtureKind)Enum.Parse(typeof(FixtureKind), parts[1]);
            var nearest = FixtureObjects().Where(f => f.kind == kind && f.usable).OrderBy(f => (f.obj.transform.position - visitor.transform.position).sqrMagnitude).FirstOrDefault().obj
                ?? throw new InvalidOperationException("No usable " + kind + " here.");
            NavMesh.SamplePosition(nearest.transform.position, out var nearFixture, 4f, NavMesh.AllAreas);
            visitor.GetComponent<NavMeshAgent>().Warp(nearFixture.position);
            if (parts.Length > 2) return;
            if (nearest is ContainerDestroyable crystal) crystal.Destroyed();
            else ((Cleverous.VaultInventory.InteractableSceneObject)nearest).Interact(visitor);
        };
        TestChannel.Commands["test-templates"] = parts =>
            File.WriteAllLines(Path.Combine(TestChannel.Root, "dump.txt"), SingletonBehaviour<FateDB>.Instance.allTemplates.Where(t => t != null).Select(t =>
                $"{t.realmId} | {t.templateName} | {string.Join(", ", t.props.Where(p => p != null && p.modelPrefab != null).Select(p => p.modelPrefab.name).Where(n => FixturesByProp.ContainsKey(n) || n.Contains("Campfire")).Distinct())}"));
        TestChannel.Status.Add(() => new Status { fixtures = string.Join(" ", FixtureObjects().GroupBy(f => f.kind).Select(g => $"{g.Key}={g.Count(f => f.usable)}/{g.Count()}")) });
    }

    private static IEnumerable<(Component obj, FixtureKind kind, bool usable)> FixtureObjects() =>
        Object.FindObjectsOfType<ContainerDestroyable>().Where(c => FixtureOf(c.Item) != null).Select(c => ((Component)c, FixtureKind.Crystal, !c.IsDestroyed))
            .Concat(Object.FindObjectsOfType<ContainerInteractable>().Where(c => FixtureOf(c.Item) != null).Select(c => ((Component)c, FixtureKind.GoldCart, !c.IsOpen)))
            .Concat(Object.FindObjectsOfType<FountainInteractable>().Where(f => Campfires.Contains(FountainNameRef(f))).Select(f => ((Component)f, FixtureKind.Campfire, !f.IsUsed)));

    private void OnDestroy()
    {
        GameAssets.Ready -= PrepareContent;
        harmony?.UnpatchSelf();
    }
}
