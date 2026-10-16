using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Cleverous.VaultInventory;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const ulong RespecGoldPerPoint = 100;
    private static readonly (NpcGreetings Npc, Tableflip.Scene Home)[] BorrowedNpcs =
    {
        (NpcGreetings.Gort, Tableflip.Scene.Town_Ekbatan), (NpcGreetings.Xander, Tableflip.Scene.Town_Ekbatan), (NpcGreetings.Omar, Tableflip.Scene.Town_Ekbatan),
        (NpcGreetings.LizmotheTraveler, Tableflip.Scene.Town_Ekbatan), (NpcGreetings.XerxestheLibrarian, Tableflip.Scene.Town_Ekbatan),
        (NpcGreetings.SullivantheReaper, Tableflip.Scene.Town_Temple_Fate2)
    };
    private static readonly (NpcGreetings Npc, Vector3 Spot, float Yaw, Action<Plugin> Offer)[] TownServices =
    {
        (NpcGreetings.XerxestheLibrarian, new Vector3(-7f, 0f, 7f), 142f, plugin => plugin.OfferRespec()),
        (NpcGreetings.SullivantheReaper, new Vector3(35f, 0f, -21f), 120f, plugin => plugin.OfferDungeonReset())
    };
    private static readonly Dictionary<Tableflip.Attribute, int> StartingStats = new Dictionary<Tableflip.Attribute, int>
    {
        [Tableflip.Attribute.Strength] = 25, [Tableflip.Attribute.Dexterity] = 20, [Tableflip.Attribute.Vitality] = 25, [Tableflip.Attribute.Magic] = 10
    };
    private static readonly AccessTools.FieldRef<NPC, bool> CanMoveRef = AccessTools.FieldRefAccess<NPC, bool>("canMove");
    private static readonly AccessTools.FieldRef<NPC, GameObject[]> PatrolRef = AccessTools.FieldRefAccess<NPC, GameObject[]>("patrolPoints");
    private static readonly AccessTools.FieldRef<PlayerUI, Button> LevelUpButtonRef = AccessTools.FieldRefAccess<PlayerUI, Button>("levelUpIndicator");
    private readonly Dictionary<NpcGreetings, GameObject> npcTemplates = new Dictionary<NpcGreetings, GameObject>();
    private Transform? npcCache;
    private Action? travelAfterBorrowing;
    private bool furnishing, borrowingNpcs;

    private void InstallTownNpcHooks()
    {
        var quiet = new HarmonyMethod(typeof(Plugin), nameof(BeforeBorrowedStartup));
        foreach (var startup in new[]
        {
            AccessTools.Method(typeof(RealmQuestGiver), "OnEnable"), AccessTools.Method(typeof(DontDestroy), "Awake"), AccessTools.Method(typeof(Cleverous.Prodigy.ProdigyCamera), "Awake"),
            AccessTools.Method(typeof(Cleverous.ThatHurtModule.ThatHurt), "Awake"), AccessTools.Method(typeof(Cleverous.Prodigy.Game), "Awake")
        }) harmony!.Patch(startup, prefix: quiet);
    }

    private static bool BeforeBorrowedStartup() => Active == null || !Active.borrowingNpcs;

    private void FurnishTown()
    {
        var homes = BorrowedNpcs.Where(b => !npcTemplates.ContainsKey(b.Npc)).Select(b => b.Home).Distinct().ToList();
        if (homes.Count == 0) PlaceTownNpcs();
        else if (!furnishing) StartCoroutine(BorrowNpcs(homes));
    }

    private IEnumerator BorrowNpcs(List<Tableflip.Scene> homes)
    {
        furnishing = true;
        foreach (var home in homes)
        {
            borrowingNpcs = true;
            yield return SceneManager.LoadSceneAsync((int)home, LoadSceneMode.Additive);
            yield return SceneManager.UnloadSceneAsync((int)home);
        }
        furnishing = false;
        var travel = travelAfterBorrowing;
        travelAfterBorrowing = null;
        if (travel != null) travel();
        else if (SceneLoad.CurrentScene == Tableflip.Scene.Town_Grove) PlaceTownNpcs();
    }

    private void KeepBorrowedNpcs(UnityEngine.SceneManagement.Scene scene)
    {
        borrowingNpcs = false;
        if (npcCache == null)
        {
            npcCache = new GameObject("FateCoop_BorrowedNpcs").transform;
            npcCache.gameObject.SetActive(false);
            Object.DontDestroyOnLoad(npcCache.gameObject);
        }
        var roots = scene.GetRootGameObjects();
        foreach (var voice in roots.SelectMany(root => root.GetComponentsInChildren<NpcVoiceLines>(true)))
        {
            if (npcTemplates.ContainsKey(voice.thisNpc) || !BorrowedNpcs.Any(b => b.Npc == voice.thisNpc && (int)b.Home == scene.buildIndex)) continue;
            voice.gameObject.SetActive(false);
            voice.transform.SetParent(npcCache, true);
            if (voice.TryGetComponent<RealmQuestGiver>(out var storyteller)) MakeTownService(storyteller);
            npcTemplates[voice.thisNpc] = voice.gameObject;
        }
        foreach (var root in roots) root.SetActive(false);
    }

    private static void MakeTownService(RealmQuestGiver storyteller)
    {
        storyteller.realmQuests = Array.Empty<RealmQuest>();
        storyteller.hasInitialGreeting = false;
        Object.DestroyImmediate(storyteller.GetComponent<QuestInteractable>());
        storyteller.gameObject.AddComponent<TownService>();
    }

    private void PlaceTownNpcs()
    {
        var grove = SceneManager.GetActiveScene().GetRootGameObjects();
        var merchants = grove.SelectMany(root => root.GetComponentsInChildren<Merchant>(true)).ToList();
        foreach (var template in npcTemplates)
        {
            if (template.Value.TryGetComponent<Merchant>(out var borrowed))
            {
                var replaced = merchants.First(m => m.merchantType == borrowed.merchantType);
                var npc = Object.Instantiate(template.Value, replaced.transform.parent);
                npc.transform.SetPositionAndRotation(replaced.transform.position, replaced.transform.rotation);
                CanMoveRef(npc.GetComponent<Merchant>()) = CanMoveRef(replaced);
                PatrolRef(npc.GetComponent<Merchant>()) = PatrolRef(replaced);
                replaced.gameObject.SetActive(false);
                npc.SetActive(true);
                if (Player.PlayerChar != null) npc.GetComponent<Merchant>().GenerateItemInventory();
                continue;
            }
            var spot = TownServices.First(s => s.Npc == template.Key);
            var service = Object.Instantiate(template.Value, spot.Spot, Quaternion.Euler(0f, spot.Yaw, 0f), merchants[0].transform.parent);
            var icon = merchants[0].GetComponentInChildren<MiniMapIconUI>(true).transform;
            Object.Instantiate(icon.gameObject, service.transform).transform.localPosition = icon.localPosition;
            service.GetComponent<TownService>().Setup(template.Key, () => spot.Offer(this));
            service.SetActive(true);
        }
    }

    private static void Ask(string text, Action accepted) =>
        SingletonBehaviour<PromptUI>.Instance.ShowPromptRaw(text, yes => { if (yes) accepted(); }, twoButtons: true);

    private static void Tell(string text) => SingletonBehaviour<PromptUI>.Instance.ShowPromptRaw(text, null, twoButtons: false, "OK");

    private void OfferRespec()
    {
        var player = Player.PlayerChar;
        var bases = player.attributes.GetSaveData().ToDictionary(a => a.attrib, a => a.baseValue);
        var spent = StartingStats.Sum(stat => Math.Max(0, bases[stat.Key] - stat.Value));
        var cost = (ulong)spent * RespecGoldPerPoint;
        if (spent == 0) { Tell("You have no stat points to reset."); return; }
        if (SingletonBehaviour<PlayerStatus>.Instance.gold < cost) { Tell($"Resetting your {spent} stat points costs {cost} gold."); return; }
        Ask($"Reset your stats for {cost} gold? You get {spent} points back to spend again.", () => Respec(spent, cost));
    }

    private static void Respec(int spent, ulong cost)
    {
        var player = Player.PlayerChar;
        SingletonBehaviour<PlayerStatus>.Instance.RemoveGold(cost);
        foreach (var stat in StartingStats) player.attributes.SetBasePoints(stat.Key, stat.Value);
        player.AddUnusedStatPoints(spent);
        var unworn = player.inventory.Where(item => item.equipped && !player.RequirementsMet(item)).ToList();
        var dropped = new List<Item>();
        foreach (var item in unworn)
        {
            player.RemoveItemFromInventory(item, RemoveItemReason.Transfer);
            if (player.AddItemToInventory(item, AddItemReason.Transfer)) continue;
            item.Drop(player.transform.position);
            dropped.Add(item);
        }
        var ui = SimpleSingletonBehaviour<PlayerUI>.Instance;
        ui.MainStatsUI.UpdateStats();
        ui.InventoryStatsUI.UpdateStats();
        LevelUpButtonRef(ui).gameObject.SetActive(true);
        SaveData.RequestSave();
        if (unworn.Count == 0) return;
        Tell(dropped.Count == 0
            ? "Took off gear you no longer meet the requirements for and put it in your bag: " + ItemNames(unworn) + "."
            : "Took off gear you no longer meet the requirements for: " + ItemNames(unworn) + ". Your bag was full, so " + ItemNames(dropped) + " dropped at your feet.");
    }

    private static string ItemNames(IEnumerable<Item> items) => string.Join(", ", items.Select(item => item.ItemDisplayName));

    private void OfferDungeonReset() =>
        Ask("Reset the dungeons? Every floor generates again for everyone in your game, except a floor with an open town portal.", () =>
        {
            ResetDungeons();
            Send("reset-dungeons", "");
            Notify("The dungeons were reset.");
        });

    private static void ResetDungeons()
    {
        var keep = PortalEntry()?.levelDepth ?? 0;
        var storage = SaveData.CurrentSaveData.GetRealmSave(RealmData.RealmId.Grove).dungeonSave;
        storage?.dungeons?.RemoveAll(floor => floor.depth != keep);
        storage?.latestDepths?.RemoveAll(depth => depth != keep);
        SaveData.RequestSave();
    }

    private void TakeDungeonReset(Partner partner)
    {
        ResetDungeons();
        Notify(partner.Name + " reset the dungeons.");
    }
}

public sealed class TownService : InteractableSceneObject
{
    private Action? offer;

    internal void Setup(NpcGreetings npc, Action service)
    {
        InteractableName = "NPC_" + npc;
        offer = service;
    }

    public override void Interact(Character? interactor = null)
    {
        GetComponent<NpcVoiceLines>().PlayYourLine();
        offer?.Invoke();
    }
}
