using System;
using System.Linq;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float AllyHoverPixels = 70f, AllyAimHeight = 1f;
    private const int SpellBuffSeconds = 150;
    private static readonly EffectType[] UnstretchedEffects = { EffectType.HPRecharge, EffectType.ManaRecharge, EffectType.StaminaRecharge, EffectType.Summon, EffectType.SummonWard, EffectType.Transform };
    private static readonly ItemTarget[] PartyTargets = { ItemTarget.UserAndPetsAndParty, ItemTarget.UserAndParty, ItemTarget.UserParty, ItemTarget.MasterOrUserAndPetsAndParty, ItemTarget.MasterOrUserAndParty, ItemTarget.MasterOrUserParty };
    private static readonly ItemTarget[] SelfTargets = { ItemTarget.User, ItemTarget.UserPets, ItemTarget.UserAndPets, ItemTarget.MasterOrUser, ItemTarget.MasterOrUserPets, ItemTarget.MasterOrUserAndPets };
    private static readonly EffectType[] SelfOnlyEffects = { EffectType.Transform, EffectType.RevertTransform, EffectType.OpenPortal, EffectType.Discover };

    private static bool Friendly(SpellData spell) => spell.targetAlignment != Alignment.Evil;

    private static bool Shareable(SpellData spell) => spell.effects == null || !spell.effects.Any(effect => effect != null && SelfOnlyEffects.Contains(effect.buffType));
    private Character? redirectTarget;
    private SpellData? redirectSpell;
    private int friendImpacts, replicaImpacts;

    private void InstallAllySpellHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(Character), "TryCastCurrentSpell"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeTryCast)));
        harmony.Patch(AccessTools.Method(typeof(Character), "CompleteSpell"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePartySpell)), finalizer: new HarmonyMethod(typeof(Plugin), nameof(AfterAllySpell)));
        harmony.Patch(AccessTools.Method(typeof(Character), "WasHitBySpell"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeSpellHit)));
        harmony.Patch(AccessTools.Method(typeof(Character), "SpawnImpactVfxForSpellOnSelf"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeSelfImpactVfx)));
        harmony.Patch(AccessTools.Method(typeof(Character), "AddEffect", new[] { typeof(EffectData), typeof(int), typeof(Character), typeof(int), typeof(bool) }), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterSpellBuff)));
    }

    private static void AfterSpellBuff(Character __instance, AppliedEffect __result, bool isSpellEffect)
    {
        if (!isSpellEffect || __result == null || !__result.Positive || __result.Permanent || __result.duration <= 0 || __instance.GetAlignment() == Alignment.Evil || UnstretchedEffects.Contains(__result.BuffType)) return;
        __result.duration = Math.Max(__result.duration, SpellBuffSeconds);
    }


    private bool testShift;

    private static bool ShiftHeld => (Active != null && Active.testShift) || (Keyboard.current != null && (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed));

    private Character? AllyUnderMouse(bool anyDistance)
    {
        var mouse = Mouse.current;
        var camera = Camera.main;
        if (mouse == null || camera == null) return null;
        var pointer = mouse.position.ReadValue();
        var best = WorldPartners().Where(p => p.Actor != null && !p.Down)
            .Select(p => (actor: p.Actor!, screen: camera.WorldToScreenPoint(p.Actor!.transform.position + Vector3.up * AllyAimHeight)))
            .Where(c => c.screen.z > 0f).OrderBy(c => ((Vector2)c.screen - pointer).sqrMagnitude).FirstOrDefault();
        return best.actor != null && (anyDistance || ((Vector2)best.screen - pointer).sqrMagnitude <= AllyHoverPixels * AllyHoverPixels) ? best.actor : null;
    }

    private static void BeforeTryCast(Character __instance, ref Character targetChar)
    {
        var plugin = Active;
        var spell = __instance.activeSpell;
        if (plugin == null || __instance != Player.PlayerChar) return;
        plugin.redirectTarget = null;
        plugin.redirectSpell = null;
        if (spell == null || !Friendly(spell)) return;
        if (spell.RequiresCharacterTarget())
        {
            if (plugin.PartnerOf(targetChar) == null && plugin.AllyUnderMouse(ShiftHeld) is Character ally) targetChar = ally;
            return;
        }
        if (!ShiftHeld || !SelfTargets.Contains(spell.targetType) || !Shareable(spell)) return;
        plugin.redirectTarget = plugin.AllyUnderMouse(true);
        if (plugin.redirectTarget != null) plugin.redirectSpell = spell;
    }

    private static void BeforePartySpell(Character __instance, SpellData overrideSpell)
    {
        var plugin = Active;
        var spell = overrideSpell != null ? overrideSpell : __instance.activeSpell;
        if (plugin == null || plugin.VisualOnly || __instance != Player.PlayerChar || spell == null || !PartyTargets.Contains(spell.targetType)) return;
        foreach (var partner in plugin.WorldPartners().Where(p => p.Actor != null && !p.Down)) partner.Actor!.WasHitBySpell(spell, __instance);
    }

    private static Exception? AfterAllySpell(Exception __exception, Character __instance)
    {
        if (Active != null && __instance == Player.PlayerChar)
        {
            Active.redirectTarget = null;
            Active.redirectSpell = null;
        }
        return __exception;
    }

    private static bool BeforeSpellHit(Character __instance, SpellData spell, Character caster)
    {
        var plugin = Active;
        if (plugin?.redirectTarget == null || spell != plugin.redirectSpell || caster != Player.PlayerChar || __instance == plugin.redirectTarget) return true;
        if (__instance == Player.PlayerChar) plugin.redirectTarget.WasHitBySpell(spell, caster);
        return __instance != Player.PlayerChar && !(__instance.HasMaster() && __instance.Master() == Player.PlayerChar);
    }

    private static bool BeforeSelfImpactVfx(Character __instance, SpellData chosenSpell, float duration)
    {
        var plugin = Active;
        if (plugin == null) return true;
        if (__instance == Player.PlayerChar && plugin.VisualOnly) plugin.friendImpacts++;
        if (plugin.PartnerOf(__instance) != null) plugin.replicaImpacts++;
        if (__instance == Player.PlayerChar && !plugin.VisualOnly && PartyTargets.Contains(chosenSpell.targetType))
            foreach (var partner in plugin.WorldPartners().Where(p => p.Actor != null && !p.Down)) partner.Actor!.SpawnImpactVfxForSpellOnSelf(chosenSpell, duration);
        if (plugin.redirectTarget == null || chosenSpell != plugin.redirectSpell || __instance == plugin.redirectTarget) return true;
        plugin.redirectTarget.SpawnImpactVfxForSpellOnSelf(chosenSpell, duration);
        return false;
    }
}
