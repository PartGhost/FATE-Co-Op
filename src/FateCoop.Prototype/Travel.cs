using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using Tableflip;
using UnityEngine;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float CombatCooldown = 8f;
    private const string TownPlace = "town";
    private readonly Dictionary<int, string> lastPlaces = new Dictionary<int, string>();
    private float lastCombatTime = float.NegativeInfinity;
    private string lastLocalPlace = "";
    private int pendingFollow = Unassigned;
    private bool metHost;
    private Action<bool>? followAnswer;

    private bool InCombat => Time.unscaledTime - lastCombatTime < CombatCooldown;

    private string LocalPlace()
    {
        if (transitioning || Player.PlayerChar == null) return "";
        if (SceneLoad.CurrentScene == Tableflip.Scene.Town_Grove) return TownPlace;
        return SceneLoad.CurrentScene == Tableflip.Scene.Dungeon && LevelGenerator.currentInstance != null ? RunFloorHere().ToString(CultureInfo.InvariantCulture) : "";
    }

    private static string PartnerPlace(PeerFrame? frame)
    {
        if (frame == null || frame.worldKey.Length == 0) return "";
        if (frame.worldKey == TownKey) return TownPlace;
        return frame.scene == Tableflip.Scene.Dungeon.ToString() && frame.depth > 0 ? frame.depth.ToString(CultureInfo.InvariantCulture) : "";
    }

    private static string DescribePlace(string place) => place == TownPlace ? "Grove town" : RunFloorName(int.Parse(place, CultureInfo.InvariantCulture));

    private IEnumerable<Partner> PartnersElsewhere()
    {
        var mine = LocalPlace();
        return mine.Length == 0 ? Enumerable.Empty<Partner>() : partners.Values.Where(p => p.Name.Length > 0 && PartnerPlace(p.Frame).Length > 0 && PartnerPlace(p.Frame) != mine);
    }

    private void TrackPartnerTravel()
    {
        if (followAnswer != null && !PromptUI.IsPromptActive) followAnswer = null;
        var mine = LocalPlace();
        if (mine.Length == 0) return;
        if (!isHost && !metHost && partners.TryGetValue(HostId, out var host) && PartnerPlace(host.Frame).Length > 0)
        {
            metHost = true;
            if (PartnerPlace(host.Frame) != mine) GoToPartner(host);
        }
        foreach (var partner in partners.Values)
        {
            var theirs = PartnerPlace(partner.Frame);
            if (theirs.Length == 0) continue;
            if (lastPlaces.TryGetValue(partner.Id, out var before) && before == mine && lastLocalPlace == mine && theirs != mine) pendingFollow = partner.Id;
            lastPlaces[partner.Id] = theirs;
        }
        lastLocalPlace = mine;
        if (partners.Values.Any(p => PartnerPlace(p.Frame) == mine))
        {
            pendingFollow = Unassigned;
            if (followAnswer != null) AnswerPrompt(SingletonBehaviour<PromptUI>.Instance, false);
            return;
        }
        if (!partners.TryGetValue(pendingFollow, out var leader) || InCombat || chatOpen || MenuOpen() || !SingletonBehaviour<PromptUI>.InstanceExists()) return;
        pendingFollow = Unassigned;
        var place = PartnerPlace(leader.Frame);
        if (place.Length == 0 || place == mine) return;
        followAnswer = follow =>
        {
            followAnswer = null;
            if (follow) GoToPartner(leader);
        };
        SingletonBehaviour<PromptUI>.Instance.ShowPromptRaw(leader.Name + " went to " + DescribePlace(place) + ". Follow them?", followAnswer, twoButtons: true, "Yes", "No");
    }

    private static void AnswerPrompt(PromptUI prompt, bool yes) => AccessTools.Method(typeof(PromptUI), yes ? "OnButtonPositive" : "OnButtonNegative").Invoke(prompt, null);

    private void GoToPartner(Partner partner)
    {
        if (InCombat) { Notify("You can't travel during a fight."); return; }
        var theirs = PartnerPlace(partner.Frame);
        if (theirs.Length == 0 || theirs == LocalPlace() || LocalPlace().Length == 0 || heldTravel != null) return;
        arriveNear = partner.Id;
        if (theirs == TownPlace)
        {
            TravelToTown();
            return;
        }
        RequestWorld(partner.Frame!);
        heldTravel = () => Notify("Couldn't reach " + partner.Name + "'s floor. Try again.");
        heldDepth = 0;
        heldUntil = Time.unscaledTime + JoinWait;
    }

    private void TravelToTown()
    {
        if (LevelGenerator.currentInstance == null || transitioning) throw new InvalidOperationException("Dungeon is not ready.");
        LevelGenerator.currentInstance.UpdateSaveData(false);
        Town.UpdateSpawnIndex(RealmData.RealmId.Grove, Town.EntryMethod.Stairs);
        SceneLoad.LoadAsync(Tableflip.Scene.Town_Grove, true);
    }

    private void TravelToFloor(int runFloor)
    {
        if (transitioning) throw new InvalidOperationException("Wait for the current trip to finish.");
        Player.latestStairsUsedWasDown = true;
        var (realm, depth) = RunPlace(runFloor);
        if (SceneLoad.CurrentScene == Tableflip.Scene.Dungeon && LevelGenerator.currentInstance != null)
        {
            LevelGenerator.currentInstance.ReplaceDungeon(0, 0, depth, (int)realm, true, false);
            return;
        }
        if (SceneLoad.CurrentScene != Tableflip.Scene.Town_Grove) throw new InvalidOperationException("Travel starts in Grove or the dungeon.");
        Player.isEnteringDungeonFromTownDoor = true;
        EnterFloorFromTown(realm, depth);
        SceneLoad.LoadAsync(Tableflip.Scene.Dungeon, true);
    }

    private void ResetTravel()
    {
        lastPlaces.Clear();
        lastLocalPlace = "";
        pendingFollow = arriveNear = Unassigned;
        metHost = false;
        followAnswer = null;
        heldTravel = null;
        requestedWorld = "";
    }
}
