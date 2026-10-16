using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cleverous.Prodigy;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private static readonly FieldInfo EffectsField = AccessTools.Field(typeof(Character), "effects");
    private static readonly MethodInfo AddParticlesMethod = AccessTools.Method(typeof(Character), "AddPSEffect");
    private static readonly MethodInfo RemoveParticlesMethod = AccessTools.Method(typeof(Character), "RemovePSEffect");
    private static readonly MethodInfo CastingVfxMethod = AccessTools.Method(typeof(Character), "CastingVFX");
    private static readonly MethodInfo ArrowFadeMethod = AccessTools.Method(typeof(Character), "ArrowTrailFadeOut");
    private static readonly MethodInfo HurtPopupMethod = AccessTools.Method(typeof(ThatHurtTrigger), "OnHurt");
    private static readonly FieldInfo FishingPoleField = AccessTools.Field(typeof(Player), "fishingPole");
    private static readonly AccessTools.FieldRef<Character, Character> MasterRef = AccessTools.FieldRefAccess<Character, Character>("masterCharacter");
    private static readonly AccessTools.FieldRef<SpellProjectile, SpellProjectileData> ProjectileDataRef = AccessTools.FieldRefAccess<SpellProjectile, SpellProjectileData>("data");
    private readonly Dictionary<Character, HashSet<string>> shownEffects = new Dictionary<Character, HashSet<string>>();
    private int damagePopups, spellsReplayed, arrowsDrawn, levelUpsPlayed, localArrows, localLevelUps, lastLevel, localSpells, localSpellTarget, visualDepth;
    private Vector3 localArrowEnd, localSpellAim;
    private string localSpellName = "";

    private void InstallVisualHooks()
    {
        harmony!.Patch(ArrowFadeMethod, prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeArrowTrail)));
        harmony.Patch(AccessTools.Method(typeof(Character), "CompleteSpell"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeCompleteSpell)));
        harmony.Patch(AccessTools.Method(typeof(SpellProjectile), "PerformImpact"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeProjectileImpact)), finalizer: new HarmonyMethod(typeof(Plugin), nameof(AfterProjectileImpact)));
        foreach (var type in new[] { typeof(Character), typeof(Monster), typeof(PartyMember) })
            harmony.Patch(AccessTools.Method(type, "RespondToAttack", new[] { typeof(Character) }), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeVisualOnlyGameplay)));
        harmony.Patch(AccessTools.Method(typeof(ProdigyCamera), "Shake"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeVisualOnlyGameplay)));
        harmony.Patch(AccessTools.Method(typeof(CharacterVitals), "ApplyDamage"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterDamage)));
    }

    private bool VisualOnly => visualDepth > 0;

    private bool IsVisualCaster(Character? caster) => caster != null && (IsStandIn(caster) || (!OwnsWorld && caster is Monster mirror && monsterIds.ContainsKey(mirror)));

    private static bool BeforeVisualOnlyGameplay() => Active == null || !Active.VisualOnly;

    private static void BeforeArrowTrail(Character __instance, LineRenderer lineRenderer)
    {
        var plugin = Active;
        if (plugin == null || __instance != Player.PlayerChar) return;
        plugin.localArrows++;
        plugin.localArrowEnd = lineRenderer.GetPosition(1);
    }

    private static void BeforeCompleteSpell(Character __instance, Vector3? targetPosition, Character targetChar, SpellData overrideSpell)
    {
        var plugin = Active;
        var spell = overrideSpell != null ? overrideSpell : __instance.activeSpell;
        if (plugin == null || __instance != Player.PlayerChar || spell == null) return;
        plugin.localSpells++;
        plugin.localSpellName = spell.spellName;
        plugin.localSpellAim = targetPosition ?? (targetChar != null ? targetChar.transform.position : __instance.transform.position + __instance.transform.forward * 150f);
        var aimed = spell == plugin.redirectSpell ? plugin.redirectTarget : targetChar;
        plugin.localSpellTarget = aimed is Monster monster && plugin.monsterIds.TryGetValue(monster, out var id) ? id : plugin.PartnerOf(aimed) is Partner target ? PlayerTarget(target.Id) : 0;
    }

    private static void BeforeProjectileImpact(SpellProjectile __instance, out bool __state)
    {
        var plugin = Active;
        __state = plugin != null && plugin.IsVisualCaster(ProjectileDataRef(__instance).caster);
        if (__state) plugin!.visualDepth++;
    }

    private static Exception? AfterProjectileImpact(Exception __exception, bool __state)
    {
        if (__state && Active != null) Active.visualDepth--;
        return __exception;
    }

    private static void AfterDamage(CharacterVitals __instance, CharacterVitals.Damage data)
    {
        var plugin = Active;
        if (plugin == null || !plugin.OwnsWorld || data.value <= 0 || !(__instance.Char is Monster monster) || !plugin.monsterIds.TryGetValue(monster, out var id) || !plugin.WorldPartners().Any()) return;
        plugin.SendWorld("pop", string.Join("|", id, data.value, data.damageResisted, data.isCritical ? 1 : 0, data.isFullResist ? 1 : 0, data.isPartialResist ? 1 : 0));
    }

    private void ShowDamagePopup(string body)
    {
        if (OwnsWorld) return;
        var parts = body.Split('|');
        if (parts.Length != 6) throw new InvalidOperationException("Invalid damage number.");
        if (!monsters.TryGetValue(ParseInt(parts[0]), out var monster) || monster == null) return;
        var trigger = monster.MyVitals.GetComponent<ThatHurtTrigger>();
        if (trigger == null) return;
        damagePopups++;
        HurtPopupMethod.Invoke(trigger, new object[] { new CharacterVitals.Damage
        {
            value = ParseInt(parts[1]), damageResisted = ParseInt(parts[2]), isCritical = parts[3] == "1", isFullResist = parts[4] == "1", isPartialResist = parts[5] == "1"
        } });
    }

    private static string ActiveEffectNames(Character character)
    {
        var effects = (Dictionary<ActivationType, List<AppliedEffect>>?)EffectsField.GetValue(character);
        if (effects == null) return "";
        var database = SingletonBehaviour<FateDB>.Instance;
        return string.Join(",", effects.Values.Where(list => list != null).SelectMany(list => list).Select(effect => effect.Name)
            .Where(name => name.Length > 0 && name.IndexOfAny(new[] { ',', '|', ';' }) < 0 && database.GetParticlePrefab(name) != null).Distinct().OrderBy(name => name, StringComparer.Ordinal));
    }

    private void ShowEffects(Character target, string names)
    {
        var wanted = new HashSet<string>(names.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        if (!shownEffects.TryGetValue(target, out var shown)) shownEffects[target] = shown = new HashSet<string>();
        foreach (var gone in shown.Where(name => !wanted.Contains(name)).ToList())
        {
            RemoveParticlesMethod.Invoke(target, new object[] { ParticleOnly(gone) });
            shown.Remove(gone);
        }
        foreach (var added in wanted.Where(name => !shown.Contains(name)).ToList())
        {
            AddParticlesMethod.Invoke(target, new object[] { ParticleOnly(added) });
            shown.Add(added);
        }
    }

    private static AppliedEffect ParticleOnly(string name) => new AppliedEffect(new EffectData { name = name }, 0, null, -1);

    private int CountLevelUps()
    {
        var player = Player.PlayerChar;
        if (player == null) return localLevelUps;
        if (lastLevel > 0 && player.Level > lastLevel) localLevelUps++;
        lastLevel = player.Level;
        return localLevelUps;
    }

    private static int LocalFishing()
    {
        var player = Player.PlayerChar;
        if (player == null) return 0;
        var animator = player.GetComponent<Animator>();
        var pole = (FishingPole)FishingPoleField.GetValue(player);
        return (animator.GetBool("IsFishing") ? 1 : 0) | (animator.GetBool("IsFishBiting") ? 2 : 0) | (animator.GetBool("IsPullingFish") ? 4 : 0) | (pole != null && pole.fishGO != null && pole.fishGO.activeSelf ? 8 : 0);
    }

    private void PlayReplicaVisuals(Partner partner, PeerFrame frame)
    {
        var actor = partner.Actor!;
        ShowEffects(actor, frame.effects);
        if (partner.Arrows >= 0 && frame.arrows != partner.Arrows && FinitePosition(frame.arrowEnd)) DrawReplicaArrow(partner, frame.arrowEnd);
        if (partner.LevelUps >= 0 && frame.levelUps != partner.LevelUps) PlayReplicaLevelUp(actor);
        if (partner.Spells >= 0 && frame.spells != partner.Spells && FinitePosition(frame.spellAim)) ReplaySpell(actor, frame.spellName, frame.spellAim, frame.spellTarget);
        partner.Arrows = frame.arrows;
        partner.LevelUps = frame.levelUps;
        partner.Spells = frame.spells;
        ShowReplicaFishing(partner, frame.fishing);
    }

    private void DrawReplicaArrow(Partner partner, Vector3 end)
    {
        var actor = partner.Actor!;
        var handle = actor.leftWeaponHandle != null && actor.leftWeaponHandle.CurrentEquipment != null ? actor.leftWeaponHandle : actor.rightWeaponHandle;
        if (handle == null || handle.CurrentEquipment == null) return;
        var line = handle.CurrentEquipment.GetComponent<LineRenderer>();
        if (line == null)
        {
            if (partner.ArrowLine == null) partner.ArrowLine = Object.Instantiate(SingletonBehaviour<FateDB>.Instance.rangedAttackLineRendererFallbackPrefab, actor.transform);
            line = partner.ArrowLine;
        }
        line.enabled = true;
        line.positionCount = 2;
        line.SetPosition(0, handle.transform.position);
        line.SetPosition(1, end);
        actor.StartCoroutine((IEnumerator)ArrowFadeMethod.Invoke(actor, new object[] { line }));
        arrowsDrawn++;
    }

    private void PlayReplicaLevelUp(Character actor)
    {
        var source = Player.PlayerChar != null ? Player.PlayerChar.levelupParticle : null;
        if (source == null) return;
        var effect = Object.Instantiate(source.gameObject, actor.transform);
        effect.transform.localPosition = source.transform.localPosition;
        var particles = effect.GetComponent<ParticleSystem>();
        particles.Play();
        Object.Destroy(effect, particles.main.duration + particles.main.startLifetime.constantMax);
        levelUpsPlayed++;
    }

    private static void PlayReplicaCastGlow(Character actor, SpellData spell)
    {
        var glow = spell.sphereType == SpellSphere.Attack ? "P_ATTACKCASTING" : spell.sphereType == SpellSphere.Defense ? "P_DEFENSECASTING" : spell.sphereType == SpellSphere.Charm ? "P_CHARMCASTING" : "";
        if (glow.Length > 0) CastingVfxMethod.Invoke(actor, new object[] { glow });
    }

    private void ReplaySpell(Character caster, string spellName, Vector3 aim, int target)
    {
        var spell = SingletonBehaviour<FateDB>.Instance.allSpells.FirstOrDefault(s => s != null && s.spellName == spellName);
        if (spell == null) throw new InvalidOperationException("Unknown spell: " + spellName);
        Character? targetChar = target > 0 && monsters.TryGetValue(target, out var monster) ? monster
            : target < 0 && -target - 1 == myId ? Player.PlayerChar
            : target < 0 && partners.TryGetValue(-target - 1, out var aimed) ? aimed.Actor : null;
        var master = MasterRef(caster);
        caster.mana = Mathf.Max(caster.MaxMana(), spell.manaCost);
        caster.stamina = Mathf.Max(caster.MaxStamina(), spell.staminaCost);
        caster.SetTarget(targetChar);
        MasterRef(caster) = null!;
        var onMe = targetChar == Player.PlayerChar && SelfTargets.Contains(spell.targetType);
        if (onMe)
        {
            redirectTarget = Player.PlayerChar;
            redirectSpell = spell;
        }
        visualDepth++;
        try
        {
            caster.CompleteSpell(aim, null, targetChar, null, spell);
            if (PartyTargets.Contains(spell.targetType)) Player.PlayerChar.SpawnImpactVfxForSpellOnSelf(spell, ImpactDuration(spell));
            spellsReplayed++;
        }
        finally
        {
            visualDepth--;
            MasterRef(caster) = master;
            if (onMe)
            {
                redirectTarget = null;
                redirectSpell = null;
            }
        }
    }

    private static float ImpactDuration(SpellData spell)
    {
        var buff = spell.effects?.Count > 0 ? spell.effects[0].duration : 0;
        return buff > 0 ? buff : spell.sfx?.Count > 0 ? spell.sfx[0].duration : 0f;
    }

    private void ShowReplicaFishing(Partner partner, int flags)
    {
        if (flags == partner.Fishing) return;
        var actor = partner.Actor!;
        var fishing = (flags & 1) != 0;
        var biting = (flags & 2) != 0;
        var pulling = (flags & 4) != 0;
        var started = fishing && (partner.Fishing & 1) == 0;
        var animator = actor.GetComponent<Animator>();
        animator.SetBool("IsFishing", fishing);
        animator.SetBool("IsFishBiting", biting);
        animator.SetBool("IsPullingFish", pulling);
        if (actor.rightWeaponHandle != null) actor.rightWeaponHandle.SetEquipmentActive(!fishing);
        if (actor.leftWeaponHandle != null) actor.leftWeaponHandle.SetEquipmentActive(!fishing);
        var pole = ReplicaPole(partner);
        if (pole != null)
        {
            pole.SetFishingPoleActive(fishing);
            if (fishing) pole.SetFishingState(started ? FishingPole.FishingState.Cast : pulling ? FishingPole.FishingState.Catch : biting ? FishingPole.FishingState.Bite : FishingPole.FishingState.Idle);
            pole.ToggleFishVisibility((flags & 8) != 0);
        }
        var audio = SingletonBehaviour<AudioManager>.Instance;
        var position = actor.transform.position;
        if (started) audio.PlayAudioClipOnLocation(AudioData.AudioClipName.FishPoleThrow, position);
        if (biting && (partner.Fishing & 2) == 0) audio.PlayAudioClipOnLocation(AudioData.AudioClipName.FishBite, position);
        if (pulling && (partner.Fishing & 4) == 0) audio.PlayAudioClipOnLocation(AudioData.AudioClipName.FishPolePull, position);
        if ((flags & 8) != 0 && (partner.Fishing & 8) == 0) audio.PlayAudioClipOnLocation(AudioData.AudioClipName.CaughtFish, position);
        partner.Fishing = flags;
    }

    private FishingPole? ReplicaPole(Partner partner)
    {
        if (partner.Pole != null) return partner.Pole;
        var source = Player.PlayerChar != null ? (FishingPole)FishingPoleField.GetValue(Player.PlayerChar) : null;
        if (source == null) return null;
        var bone = FindChild(partner.Actor!.transform, source.transform.parent.name);
        if (bone == null) return null;
        var copy = Object.Instantiate(source.gameObject, bone);
        copy.transform.localPosition = source.transform.localPosition;
        copy.transform.localRotation = source.transform.localRotation;
        copy.transform.localScale = source.transform.localScale;
        partner.Pole = copy.GetComponent<FishingPole>();
        return partner.Pole;
    }

    private static int WeaponParticles(Character? character) => character == null ? 0
        : new[] { character.rightWeaponHandle, character.leftWeaponHandle }.Where(handle => handle != null && handle.CurrentEquipment != null).Sum(handle => handle.CurrentEquipment.GetComponentsInChildren<ParticleSystem>(true).Length);

    private static Transform? FindChild(Transform root, string name)
    {
        if (root.name == name) return root;
        foreach (Transform child in root)
        {
            var found = FindChild(child, name);
            if (found != null) return found;
        }
        return null;
    }
}
