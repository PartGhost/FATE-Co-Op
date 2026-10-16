using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float BleedOutSeconds = 30f, ReviveSeconds = 4f, ReviveRange = 3f, RevivedHealth = 0.25f, HurtPause = 0.75f;
    private static readonly MethodInfo VitalsDeadSetter = AccessTools.PropertySetter(typeof(CharacterVitals), "IsDead");
    private static readonly AccessTools.FieldRef<Character, Collider> ColliderRef = AccessTools.FieldRefAccess<Character, Collider>("myCollider");
    private bool downed, dyingForReal;
    private float downedUntil, reviveProgress, reviveSince, lastHurtTime = float.NegativeInfinity, lastActionTime = float.NegativeInfinity;
    private int reviveTarget = Unassigned;
    private TargetBar? progressBar;

    private static bool PartnerDown(Partner partner) => partner.Frame != null && partner.Frame.hp <= 0 && partner.Frame.maxHp > 0;

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
        ((Button)AccessTools.Field(typeof(PauseUI), "resumeButton").GetValue(__instance)).onClick.Invoke();
        plugin.DieForReal();
        return false;
    }

    private static bool BeforePlayerDie(CharacterVitals __instance)
    {
        var plugin = Active;
        if (plugin == null || plugin.dyingForReal || __instance.Char != Player.PlayerChar) return true;
        if (plugin.downed) return false;
        if (!plugin.WorldPartners().Any(p => !PartnerDown(p))) return true;
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
        Notify("You're down. A friend can revive you.");
    }

    private void StandUp(Partner reviver)
    {
        var player = Player.PlayerChar;
        EndDowned();
        player.MyVitals.Revive();
        player.MyVitals.SetHealthPct(RevivedHealth);
        player.SetAIState(AIState.Inactive);
        player.GetComponent<Animator>().SetBool("IsDead", false);
        Notify(reviver.Name + " revived you.");
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
        var player = Player.PlayerChar;
        if (downed && player != null && WorldPartners().Any(p => !PartnerDown(p) && p.Actor != null && (p.Actor.transform.position - player.transform.position).sqrMagnitude <= ReviveRange * ReviveRange))
            downedUntil += Time.unscaledDeltaTime;
        if (downed && (Time.unscaledTime > downedUntil || !WorldPartners().Any(p => !PartnerDown(p)))) DieForReal();
        var target = downed || player == null || !player.Alive() ? null : WorldPartners().Where(p => PartnerDown(p) && p.Actor != null && (player.transform.position - p.Actor.transform.position).sqrMagnitude <= ReviveRange * ReviveRange)
            .OrderBy(p => (player.transform.position - p.Actor!.transform.position).sqrMagnitude).FirstOrDefault();
        if (target == null || target.Id != reviveTarget || lastActionTime > reviveSince)
        {
            reviveProgress = 0f;
            reviveSince = Time.unscaledTime;
        }
        reviveTarget = target?.Id ?? Unassigned;
        if (target == null || Time.unscaledTime - lastHurtTime < HurtPause) return;
        reviveProgress += Time.unscaledDeltaTime / ReviveSeconds;
        if (reviveProgress < 1f) return;
        Send("revive", "", target.Id, worldScoped: true);
        reviveProgress = 0f;
    }

    private void ShowReplicaDown(Partner partner, bool down)
    {
        var actor = partner.Actor;
        if (actor == null || down == partner.Down) return;
        partner.Down = down;
        AIStateRef(actor) = down ? AIState.PassedOut : AIState.Inactive;
        actor.GetComponent<Animator>().SetBool("IsDead", down);
        if (down) return;
        actor.MyVitals.Revive();
        ColliderRef(actor).enabled = true;
    }

    private void UpdateProgressBar()
    {
        var helper = partners.Values.FirstOrDefault(p => p.Frame != null && p.Frame.reviveTarget == myId && p.Frame.revive > 0f && p.InWorld(LocalFrameWorld()));
        string label;
        float fraction;
        var reviving = true;
        if (reviveProgress > 0f && partners.TryGetValue(reviveTarget, out var revived)) { label = "Reviving " + revived.Name; fraction = reviveProgress; }
        else if (downed && helper != null) { label = helper.Name + " is reviving you"; fraction = helper.Frame!.revive; }
        else if (downed) { label = "Down: " + Mathf.CeilToInt(downedUntil - Time.unscaledTime) + "s for a friend to reach you"; fraction = (downedUntil - Time.unscaledTime) / BleedOutSeconds; reviving = false; }
        else
        {
            if (progressBar != null) progressBar.gameObject.SetActive(false);
            return;
        }
        if (progressBar == null && !BuildProgressBar()) return;
        var bar = progressBar!;
        bar.gameObject.SetActive(showPanel && !MenuOpen());
        bar.nameText.text = label;
        bar.poisonBarFill.gameObject.SetActive(reviving);
        bar.hpBarFill.gameObject.SetActive(!reviving);
        (reviving ? bar.poisonBarFill : bar.hpBarFill).localScale = new Vector3(Mathf.Clamp01(fraction), 1f, 1f);
    }

    private bool BuildProgressBar()
    {
        if (hudOwner == null) return false;
        var source = TargetBarRef(hudOwner);
        progressBar = Object.Instantiate(source.gameObject, source.transform.parent).GetComponent<TargetBar>();
        progressBar.enabled = false;
        progressBar.bossIndicator.SetActive(false);
        progressBar.rankedUpIndicator.SetActive(false);
        progressBar.levelText.transform.parent.gameObject.SetActive(false);
        progressBar.resistanceTemplate.transform.parent.gameObject.SetActive(false);
        progressBar.enemyOnlyParent.SetActive(true);
        progressBar.nameText.color = progressBar.normalNameColor;
        var rect = (RectTransform)progressBar.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.28f);
        rect.anchoredPosition = Vector2.zero;
        return true;
    }
}
