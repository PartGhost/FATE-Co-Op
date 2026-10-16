using System.Linq;
using HarmonyLib;
using Tableflip;
using UnityEngine;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float AllyHoverRadius = 1.5f;
    private static readonly ItemTarget[] PartyTargets = { ItemTarget.UserAndPetsAndParty, ItemTarget.UserAndParty, ItemTarget.UserParty, ItemTarget.MasterOrUserAndPetsAndParty, ItemTarget.MasterOrUserAndParty, ItemTarget.MasterOrUserParty };

    private void InstallAllySpellHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(Character), "TryCastCurrentSpell"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeTryCast)));
        harmony.Patch(AccessTools.Method(typeof(Character), "CompleteSpell"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforePartySpell)));
    }

    private static void BeforeTryCast(Character __instance, Vector3? targetPosition, ref Character targetChar)
    {
        var plugin = Active;
        var spell = __instance.activeSpell;
        if (plugin == null || __instance != Player.PlayerChar || spell == null || spell.targetAlignment != Alignment.Good || !spell.RequiresCharacterTarget()
            || plugin.PartnerOf(targetChar) != null || !targetPosition.HasValue) return;
        var aim = targetPosition.Value;
        var ally = plugin.WorldPartners().Where(p => p.Actor != null && !p.Down && (p.Actor.transform.position - aim).sqrMagnitude <= AllyHoverRadius * AllyHoverRadius)
            .OrderBy(p => (p.Actor!.transform.position - aim).sqrMagnitude).FirstOrDefault();
        if (ally != null) targetChar = ally.Actor!;
    }

    private static void BeforePartySpell(Character __instance, SpellData overrideSpell)
    {
        var plugin = Active;
        var spell = overrideSpell != null ? overrideSpell : __instance.activeSpell;
        if (plugin == null || plugin.VisualOnly || __instance != Player.PlayerChar || spell == null || !PartyTargets.Contains(spell.targetType)) return;
        foreach (var partner in plugin.WorldPartners().Where(p => p.Actor != null && !p.Down)) partner.Actor!.WasHitBySpell(spell, __instance);
    }
}
