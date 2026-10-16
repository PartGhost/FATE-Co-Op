using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Cleverous.Prodigy.Input;
using HarmonyLib;
using Tableflip;
using UnityEngine;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float RangedReach = 0.25f, CertainHit = 101f;
    private bool grazePending;
    private int rangedRolls, rangedMisses, grazes;

    private void InstallRangedHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(Player), "PerformHitSweep"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeHitSweep)));
        harmony.Patch(RollAttackMethod, transpiler: new HarmonyMethod(typeof(Plugin), nameof(SureRangedHits)), finalizer: new HarmonyMethod(typeof(Plugin), nameof(AfterRollAttack)));
    }

    private static bool UsingRanged(Character character) => character.activeWeapon != null && (character.activeWeapon.itemType == ItemType.Bow || character.activeWeapon.itemType == ItemType.Crossbow);

    private static void BeforeHitSweep(Player __instance)
    {
        if (__instance != Player.PlayerChar || !UsingRanged(__instance)) return;
        var target = __instance.Target() != null ? __instance.Target() : SingletonBehaviour<InputBroker>.Instance.HoveredCharacter;
        if (target == null || !target.Alive()) return;
        var toTarget = target.transform.position - __instance.transform.position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.01f || toTarget.magnitude > __instance.activeWeapon.strikeRadius * RangedReach) return;
        __instance.transform.rotation = Quaternion.LookRotation(toTarget.normalized);
    }

    private static IEnumerable<CodeInstruction> SureRangedHits(IEnumerable<CodeInstruction> instructions)
    {
        var debugBuild = AccessTools.PropertyGetter(typeof(Debug), nameof(Debug.isDebugBuild));
        var list = new List<CodeInstruction>(instructions);
        for (var i = 0; i + 2 < list.Count; i++)
        {
            if ((list[i].opcode != OpCodes.Ldloc_S && list[i].opcode != OpCodes.Ldloc) || (list[i + 1].opcode != OpCodes.Ble_S && list[i + 1].opcode != OpCodes.Ble) || !list[i + 2].Calls(debugBuild)) continue;
            list.InsertRange(i, new[] { new CodeInstruction(OpCodes.Dup).MoveLabelsFrom(list[i]) });
            list.InsertRange(i + 2, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Plugin), nameof(HitChance))) });
            return list;
        }
        throw new InvalidOperationException("RollAttack hit roll not found.");
    }

    private static float HitChance(float roll, float chance, Character attacker, Character target)
    {
        var plugin = Active;
        if (plugin == null || attacker != Player.PlayerChar || !UsingRanged(attacker)) return chance;
        plugin.grazePending = roll > chance;
        plugin.rangedRolls++;
        return CertainHit;
    }

    private static Exception? AfterRollAttack(Exception __exception, Character __instance, bool __result)
    {
        var plugin = Active;
        if (plugin == null) return __exception;
        plugin.grazePending = false;
        if (__instance == Player.PlayerChar && UsingRanged(__instance) && !__result) plugin.rangedMisses++;
        return __exception;
    }

    private static void Graze(CharacterVitals.Damage data)
    {
        var plugin = Active;
        if (plugin == null || !plugin.grazePending || data.source == null || data.source.Char != Player.PlayerChar) return;
        data.value = Math.Max(1, data.value / (data.isCritical ? 4 : 2));
        plugin.grazes++;
        data.isCritical = false;
    }
}
