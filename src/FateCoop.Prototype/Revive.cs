using System;
using System.Reflection;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using UnityEngine.AI;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float BleedOutSeconds = 30f, ReviveSeconds = 4f, ReviveRange = 3f, RevivedHealth = 0.25f;
    private static readonly MethodInfo VitalsDeadSetter = AccessTools.PropertySetter(typeof(CharacterVitals), "IsDead");
    private bool downed, replicaDown, dyingForReal;
    private float downedUntil, reviveStart = -1f, reviveProgress;
    private GUIStyle? centeredLabel;

    private GUIStyle CenteredLabel => centeredLabel ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
    private bool PartnerDown => remoteFrame != null && remoteFrame.hp <= 0 && remoteFrame.maxHp > 0;

    private void InstallReviveHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(CharacterVitals), "Die", new[] { typeof(CharacterVitals.Damage) }), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePlayerDie)));
        harmony.Patch(AccessTools.Method(typeof(CharacterVitals), "Die", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePlayerDie)));
        harmony.Patch(AccessTools.Method(typeof(PauseUI), "OnSaveAndQuitButton"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeSaveAndQuit)));
    }

    private static bool BeforeSaveAndQuit(PauseUI __instance)
    {
        var plugin = Active;
        if (plugin == null || !plugin.downed) return true;
        ((UnityEngine.UI.Button)AccessTools.Field(typeof(PauseUI), "resumeButton").GetValue(__instance)).onClick.Invoke();
        plugin.DieForReal();
        return false;
    }

    private static bool BeforePlayerDie(CharacterVitals __instance)
    {
        var plugin = Active;
        if (plugin == null || plugin.dyingForReal || __instance.Char != Player.PlayerChar) return true;
        if (plugin.downed) return false;
        if (!plugin.PeerInWorld() || plugin.PartnerDown) return true;
        plugin.GoDown();
        return false;
    }

    private void GoDown()
    {
        var player = Player.PlayerChar;
        downed = true;
        downedUntil = Time.unscaledTime + BleedOutSeconds;
        player.MyVitals.SetHealthPct(0f);
        VitalsDeadSetter.Invoke(player.MyVitals, new object[] { true });
        player.SetTarget(null);
        player.SetAIState(AIState.PassedOut);
        player.GetComponent<Animator>().SetBool("IsDead", true);
        var agent = player.GetComponent<NavMeshAgent>();
        if (agent.isOnNavMesh) agent.ResetPath();
        InputBlocker.uiBlockers++;
        Notify("You're down. " + partnerName + " can revive you.");
    }

    private void StandUp()
    {
        var player = Player.PlayerChar;
        EndDowned();
        player.MyVitals.Revive();
        player.MyVitals.SetHealthPct(RevivedHealth);
        player.SetAIState(AIState.Inactive);
        player.GetComponent<Animator>().SetBool("IsDead", false);
        Notify(partnerName + " revived you.");
    }

    private void EndDowned()
    {
        if (!downed) return;
        downed = false;
        InputBlocker.uiBlockers--;
    }

    private void DieForReal()
    {
        EndDowned();
        if (Player.PlayerChar == null) return;
        dyingForReal = true;
        Player.PlayerChar.MyVitals.Die();
        dyingForReal = false;
    }

    private void UpdateRevive()
    {
        if (downed && (Time.unscaledTime > downedUntil || !PeerInWorld() || PartnerDown)) DieForReal();
        var player = Player.PlayerChar;
        if (downed || player == null || !player.Alive() || actor == null || !PartnerDown || !PeerInWorld() || (player.transform.position - actor.transform.position).sqrMagnitude > ReviveRange * ReviveRange)
        {
            reviveStart = -1f;
            reviveProgress = 0f;
            return;
        }
        if (reviveStart < 0f || lastCombatTime > reviveStart) reviveStart = Time.unscaledTime;
        reviveProgress = (Time.unscaledTime - reviveStart) / ReviveSeconds;
        if (reviveProgress < 1f) return;
        Send("revive", "");
        reviveStart = -1f;
        reviveProgress = 0f;
    }

    private void ShowReplicaDown(bool down)
    {
        if (actor == null || down == replicaDown) return;
        replicaDown = down;
        actor.SetAIState(down ? AIState.PassedOut : AIState.Inactive);
        actor.GetComponent<Animator>().SetBool("IsDead", down);
    }

    private void DrawRevive()
    {
        var area = new Rect(Screen.width / 2f - 150f, Screen.height * 0.7f, 300f, 24f);
        if (reviveProgress > 0f)
        {
            GUI.Label(area, "Reviving " + partnerName, CenteredLabel);
            DrawFill(new Rect(area.x, area.yMax, area.width, 10f), reviveProgress, new Color(0.4f, 0.9f, 0.4f));
        }
        else if (downed)
        {
            var helping = remoteFrame != null && remoteFrame.revive > 0f;
            GUI.Label(area, helping ? partnerName + " is reviving you" : "Down. " + partnerName + " has " + Mathf.CeilToInt(downedUntil - Time.unscaledTime) + "s to reach you", CenteredLabel);
            DrawFill(new Rect(area.x, area.yMax, area.width, 10f), helping ? remoteFrame!.revive : (downedUntil - Time.unscaledTime) / BleedOutSeconds, helping ? new Color(0.4f, 0.9f, 0.4f) : Color.red);
        }
    }
}
