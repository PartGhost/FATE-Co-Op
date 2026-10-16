using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cleverous.ThatHurtModule;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using UnityEngine.AI;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float GroundInterval = 0.25f, GroundSettleTime = 0.6f, FixtureRetryTime = 2f;
    private static readonly System.Reflection.MethodInfo ChestLockSetter = AccessTools.PropertySetter(typeof(ContainerInteractable), "IsLocked");
    private readonly Dictionary<int, Component> ground = new Dictionary<int, Component>();
    private readonly Dictionary<Component, int> groundIds = new Dictionary<Component, int>();
    private readonly Dictionary<int, float> groundSeen = new Dictionary<int, float>();
    private readonly HashSet<int> announcedGround = new HashSet<int>(), pendingTakes = new HashSet<int>();
    private readonly HashSet<string> announcedFixtures = new HashSet<string>();
    private readonly Dictionary<string, float> fixtureRetry = new Dictionary<string, float>();
    private int nextGroundId = 1, lootGiven, lootTaken, dropsSent;
    private float nextGroundSync, nextGroundCheck;
    private bool dropLocally, buildingActor, applyingFixture;

    [Serializable] private sealed class GroundItem
    {
        public int id, gold;
        public Vector3 position;
        public ItemSaveData item = null!;
    }

    private void InstallLootHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(DroppedItem), "Interact"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeItemPickup)));
        harmony.Patch(AccessTools.Method(typeof(GoldPileTrigger), "OnTriggerEnter"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeGoldPickup)));
        harmony.Patch(AccessTools.Method(typeof(Item), "Drop"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeItemDrop)));
        harmony.Patch(AccessTools.Method(typeof(ContainerInteractable), "Interact"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeChestOpen)));
        harmony.Patch(AccessTools.Method(typeof(ContainerDestroyable), "Destroyed"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeBarrelBreak)));
        harmony.Patch(AccessTools.Method(typeof(ActiveLevel), "SpawnGoldPile"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeGoldSpawn)));
    }

    private static bool GuestInHostWorld => Active != null && Active.InOwnersWorld;

    private static bool BeforeItemPickup(DroppedItem __instance) => Active == null || Active.OwnsWorld || !Active.RequestTake(__instance);

    private static bool BeforeGoldPickup(GoldPileTrigger __instance, Collider other) => Active == null || Active.OwnsWorld || other.GetComponent<Player>() == null || !Active.RequestTake(__instance);

    private static bool BeforeItemDrop(Item __instance, Vector3 worldPosStart, ref DroppedItem? __result)
    {
        var plugin = Active;
        if (plugin == null || (!plugin.buildingActor && (plugin.dropLocally || !GuestInHostWorld))) return true;
        if (!plugin.buildingActor)
        {
            plugin.SendToOwner("drop", JsonUtility.ToJson(new GroundItem { position = worldPosStart, item = __instance.GetItemSave() }));
            plugin.dropsSent++;
        }
        __result = null;
        return false;
    }

    private static bool BeforeGoldSpawn(Vector3 position, int goldAmount)
    {
        if (!GuestInHostWorld || goldAmount <= 0) return true;
        Active!.SendToOwner("drop", JsonUtility.ToJson(new GroundItem { position = position, gold = goldAmount }));
        return false;
    }

    private static bool BeforeChestOpen(ContainerInteractable __instance)
    {
        if (Active == null || Active.applyingFixture || !GuestInHostWorld || __instance.IsOpen) return true;
        var position = __instance.transform.position;
        if (!__instance.IsLocked)
        {
            Active!.RequestFixture("c", position);
            return false;
        }
        var key = Player.PlayerChar.inventory.FirstOrDefault(item => item.itemType == ItemType.ChestKey);
        if (key == null) return true;
        if (Active!.RequestFixture("k", position)) Player.PlayerChar.RemoveItemFromInventory(key, RemoveItemReason.Consumed);
        return false;
    }

    private static bool BeforeBarrelBreak(ContainerDestroyable __instance)
    {
        if (Active == null || Active.applyingFixture) return true;
        if (Active.VisualOnly) return false;
        if (!GuestInHostWorld || __instance.IsDestroyed) return true;
        Active!.RequestFixture("b", __instance.transform.position);
        return false;
    }

    private bool RequestTake(Component replica)
    {
        if (!groundIds.TryGetValue(replica, out var id)) return false;
        if (pendingTakes.Add(id)) SendToOwner("take", id.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    private bool RequestFixture(string kind, Vector3 position)
    {
        var key = FixtureKey(kind, position);
        if (fixtureRetry.TryGetValue(key, out var retryAt) && Time.unscaledTime < retryAt) return false;
        fixtureRetry[key] = Time.unscaledTime + FixtureRetryTime;
        SendToOwner("use-fixture", key);
        return true;
    }

    private static string FixtureKey(string kind, Vector3 p) => string.Join("|", kind, p.x.ToString("0.##", CultureInfo.InvariantCulture), p.y.ToString("0.##", CultureInfo.InvariantCulture), p.z.ToString("0.##", CultureInfo.InvariantCulture));

    private static Item ToItem(ItemSaveData data)
    {
        var itemData = SingletonBehaviour<FateDB>.Instance.AllItems.Find(a => a.displayName == data.baseName) ?? throw new InvalidOperationException("Unknown item: " + data.baseName);
        var item = new Item(itemData);
        item.ApplyInstance(data);
        return item;
    }

    private static IEnumerable<Component> GroundObjects() =>
        UnityEngine.Object.FindObjectsOfType<DroppedItem>().Where(d => d.Item != null).Cast<Component>()
            .Concat(UnityEngine.Object.FindObjectsOfType<GoldPileTrigger>().Where(g => !g.IsEmpty));

    private static GroundItem Describe(int id, Component obj) => obj is DroppedItem dropped
        ? new GroundItem { id = id, position = dropped.transform.position, item = dropped.Item.GetItemSave() }
        : new GroundItem { id = id, position = obj.transform.position, gold = ((GoldPileTrigger)obj).GoldAmount };

    private void TrackGround(int id, Component obj)
    {
        ground[id] = obj;
        groundIds[obj] = id;
    }

    private void ForgetGround(int id)
    {
        if (ground.TryGetValue(id, out var obj)) groundIds.Remove(obj);
        ground.Remove(id);
        groundSeen.Remove(id);
        announcedGround.Remove(id);
        pendingTakes.Remove(id);
    }

    private static void RemoveGroundObject(Component obj)
    {
        if (obj == null) return;
        if (obj is DroppedItem dropped)
        {
            if (LevelGenerator.currentInstance != null) LevelGenerator.currentInstance.RemoveDroppedItem(dropped);
            dropped.Remove();
        }
        else UnityEngine.Object.Destroy(obj.gameObject);
    }

    private void ResetGround()
    {
        if (!OwnsWorld) foreach (var obj in ground.Values) RemoveGroundObject(obj);
        ground.Clear();
        groundIds.Clear();
        groundSeen.Clear();
        announcedGround.Clear();
        pendingTakes.Clear();
        announcedFixtures.Clear();
        fixtureRetry.Clear();
        nextGroundId = 1;
    }

    private void SyncGround()
    {
        if (Time.unscaledTime < nextGroundSync) return;
        nextGroundSync = Time.unscaledTime + GroundInterval;
        if (!SharedWorldReady()) return;
        var inWorld = WorldPartners().Any();
        var seen = new HashSet<int>();
        foreach (var obj in GroundObjects())
        {
            if (!groundIds.TryGetValue(obj, out var id))
            {
                id = nextGroundId++;
                TrackGround(id, obj);
                groundSeen[id] = Time.unscaledTime;
            }
            seen.Add(id);
            if (!inWorld || Time.unscaledTime - groundSeen[id] < GroundSettleTime || !announcedGround.Add(id)) continue;
            SendWorld("ground", JsonUtility.ToJson(Describe(id, obj)));
        }
        foreach (var id in ground.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            if (announcedGround.Contains(id)) SendWorld("ground-gone", id.ToString(CultureInfo.InvariantCulture));
            ForgetGround(id);
        }
        if (!inWorld) return;
        if (Time.unscaledTime >= nextGroundCheck)
        {
            nextGroundCheck = Time.unscaledTime + FullRefreshInterval;
            announcedFixtures.Clear();
            SendWorld("ground-list", string.Join(",", announcedGround));
        }
        foreach (var chest in UnityEngine.Object.FindObjectsOfType<ContainerInteractable>().Where(c => c.IsOpen))
            AnnounceFixture(FixtureKey("c", chest.transform.position));
        foreach (var barrel in UnityEngine.Object.FindObjectsOfType<ContainerDestroyable>().Where(b => b.IsDestroyed))
            AnnounceFixture(FixtureKey("b", barrel.transform.position));
    }

    private void AnnounceFixture(string key)
    {
        if (announcedFixtures.Add(key)) SendWorld("fixture", key);
    }

    private GroundItem ReadGroundItem(string body)
    {
        var entry = JsonUtility.FromJson<GroundItem>(body);
        if (entry == null || !FinitePosition(entry.position) || entry.gold < 0 || (entry.gold == 0 && string.IsNullOrEmpty(entry.item?.baseName)))
            throw new InvalidOperationException("Invalid ground item.");
        return entry;
    }

    private void ReceiveGround(string body)
    {
        if (OwnsWorld) return;
        var entry = ReadGroundItem(body);
        if (!SharedWorldReady() || ground.ContainsKey(entry.id)) return;
        Component replica;
        if (entry.gold > 0)
        {
            var pile = UnityEngine.Object.Instantiate(SingletonBehaviour<FateDB>.Instance.goldPilePrefab, entry.position, Quaternion.identity).GetComponent<GoldPileTrigger>();
            pile.Init(entry.gold);
            replica = pile;
        }
        else
        {
            dropLocally = true;
            try { replica = ToItem(entry.item).Drop(entry.position, skipSoundAndDropTween: true); }
            finally { dropLocally = false; }
            if (LevelGenerator.currentInstance != null) LevelGenerator.currentInstance.RemoveDroppedItem((DroppedItem)replica);
            replica.transform.SetParent(null);
        }
        TrackGround(entry.id, replica);
    }

    private void ReceiveGroundGone(string body)
    {
        var id = ParseInt(body);
        if (ground.TryGetValue(id, out var obj)) RemoveGroundObject(obj);
        ForgetGround(id);
    }

    private void CheckGroundList(string body)
    {
        if (OwnsWorld || !SharedWorldReady()) return;
        var listed = new HashSet<int>(body.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(ParseInt));
        foreach (var stale in ground.Keys.Where(id => !listed.Contains(id)).ToList())
        {
            RemoveGroundObject(ground[stale]);
            ForgetGround(stale);
        }
        var missing = listed.Where(id => !ground.ContainsKey(id)).ToList();
        if (missing.Count > 0) SendToOwner("ground-missing", string.Join(",", missing));
    }

    private void ResendGround(string body)
    {
        if (!OwnsWorld) return;
        foreach (var id in body.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(ParseInt)) announcedGround.Remove(id);
    }

    private void GiveTake(Partner partner, string body)
    {
        if (!OwnsWorld) return;
        var id = ParseInt(body);
        if (!ground.TryGetValue(id, out var obj) || obj == null)
        {
            Send("ground-gone", body, partner.Id, worldScoped: true);
            return;
        }
        var entry = Describe(id, obj);
        RemoveGroundObject(obj);
        ForgetGround(id);
        Send("granted", JsonUtility.ToJson(entry), partner.Id, worldScoped: true);
        lootGiven++;
    }

    private void ReceiveGranted(string body)
    {
        var entry = ReadGroundItem(body);
        if (ground.TryGetValue(entry.id, out var replica)) RemoveGroundObject(replica);
        ForgetGround(entry.id);
        var player = Player.PlayerChar;
        if (player == null)
        {
            SendToOwner("drop", body);
            return;
        }
        if (entry.gold > 0)
        {
            SingletonBehaviour<PlayerStatus>.Instance.AddGold((uint)entry.gold);
            StatsManager.IncrementStat(Stat.GoldLooted, entry.gold);
            ThatHurt.PopText(player.transform.position, "+" + I2Loca.GetFormat("GoldPickup", entry.gold), 1f, ThatHurt.ThatHurtColor.Gold, 0f);
        }
        else
        {
            var item = ToItem(entry.item);
            item.slotIndex = -1;
            var stored = (item.itemType == ItemType.Potion && player.TryGetFirstFreeHotbarSlot(out var slot) && player.AddItemToHotbar(item, slot, AddItemReason.Loot))
                || player.AddItemToInventory(item, AddItemReason.Loot);
            if (stored) item.PlayTakeSound();
            else SendToOwner("drop", JsonUtility.ToJson(new GroundItem { position = player.transform.position, item = entry.item }));
        }
        lootTaken++;
    }

    private void ReceiveDrop(string body)
    {
        if (!OwnsWorld) return;
        var entry = ReadGroundItem(body);
        if (!NavMesh.SamplePosition(entry.position, out var hit, 3f, NavMesh.AllAreas) && !NavMesh.SamplePosition(Player.PlayerChar.transform.position, out hit, 3f, NavMesh.AllAreas))
            throw new InvalidOperationException("Dropped item has no valid place.");
        if (entry.gold > 0) ActiveLevel.SpawnGoldPile(hit.position, entry.gold);
        else ToItem(entry.item).Drop(hit.position);
    }

    private void UseFixture(string body)
    {
        if (!OwnsWorld) return;
        var parts = body.Split('|');
        if (parts.Length != 4 || (parts[0] != "c" && parts[0] != "k" && parts[0] != "b")) throw new InvalidOperationException("Invalid fixture.");
        var position = new Vector3(ParseFloat(parts[1]), ParseFloat(parts[2]), ParseFloat(parts[3]));
        if (parts[0] != "b")
        {
            var chest = Nearest(UnityEngine.Object.FindObjectsOfType<ContainerInteractable>(), position);
            if (chest == null || chest.IsOpen) return;
            if (parts[0] == "k") ChestLockSetter.Invoke(chest, new object[] { false });
            chest.Interact(Player.PlayerChar);
        }
        else
        {
            var barrel = Nearest(UnityEngine.Object.FindObjectsOfType<ContainerDestroyable>(), position);
            if (barrel != null) barrel.Destroyed();
        }
    }

    private void ApplyFixture(string body)
    {
        if (OwnsWorld) return;
        var parts = body.Split('|');
        if (parts.Length != 4) throw new InvalidOperationException("Invalid fixture.");
        var position = new Vector3(ParseFloat(parts[1]), ParseFloat(parts[2]), ParseFloat(parts[3]));
        applyingFixture = true;
        try
        {
            if (parts[0] == "c")
            {
                var chest = Nearest(UnityEngine.Object.FindObjectsOfType<ContainerInteractable>(), position);
                if (chest == null || chest.IsOpen) return;
                chest.Init(false, 0, TrapType.TrapNone, new List<Item>(), false);
                chest.Interact(Player.PlayerChar);
            }
            else if (parts[0] == "b")
            {
                var barrel = Nearest(UnityEngine.Object.FindObjectsOfType<ContainerDestroyable>(), position);
                if (barrel == null || barrel.IsDestroyed) return;
                barrel.Init(false, 0, TrapType.TrapNone, new List<Item>());
                barrel.Destroyed();
            }
        }
        finally { applyingFixture = false; }
    }

    private void RealizeGround()
    {
        var copies = ground.ToList();
        nextGroundId = ground.Keys.DefaultIfEmpty(0).Max() + 1;
        foreach (var entry in copies)
        {
            if (entry.Value == null) continue;
            var item = Describe(entry.Key, entry.Value);
            RemoveGroundObject(entry.Value);
            if (item.gold > 0) ActiveLevel.SpawnGoldPile(item.position, item.gold);
            else ToItem(item.item).Drop(item.position, skipSoundAndDropTween: true);
        }
        ground.Clear();
        groundIds.Clear();
        groundSeen.Clear();
        announcedGround.Clear();
        pendingTakes.Clear();
    }

    private static T? Nearest<T>(IEnumerable<T> candidates, Vector3 position) where T : Component =>
        candidates.Where(c => (c.transform.position - position).sqrMagnitude < 0.25f).OrderBy(c => (c.transform.position - position).sqrMagnitude).FirstOrDefault();
}
