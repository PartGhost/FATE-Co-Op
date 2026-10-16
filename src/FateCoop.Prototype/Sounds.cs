using System;
using HarmonyLib;
using Tableflip;
using UnityEngine;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float NearSound = 10f, FarSound = 30f;
    private static readonly Vector3 Nowhere = Vector3.positiveInfinity;
    private Vector3? soundEmitter;

    private void InstallSoundHooks()
    {
        var scoped = new HarmonyMethod(typeof(Plugin), nameof(BeforeEmitterScope));
        var unscoped = new HarmonyMethod(typeof(Plugin), nameof(AfterEmitterScope));
        foreach (var method in new[]
        {
            AccessTools.Method(typeof(Character), "PerformAttack"), AccessTools.Method(typeof(Character), "RollAttack"), AccessTools.Method(typeof(Character), "CompleteSpell"),
            AccessTools.Method(typeof(ContainerDestroyable), "Destroyed"), AccessTools.Method(typeof(ContainerInteractable), "Interact"), AccessTools.Method(typeof(PartyMember), "PassOut"),
            AccessTools.Method(typeof(Character), "Attack"), AccessTools.Method(typeof(PartyMember), "Attack"), AccessTools.Method(typeof(Character), "UpdateTimers")
        }) harmony!.Patch(method, prefix: scoped, finalizer: unscoped);
        harmony!.Patch(AccessTools.Method(typeof(Item), "Drop"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeDropSound)), finalizer: unscoped);
        harmony.Patch(AccessTools.Method(typeof(Narrator), "PlayYourLine"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeNarratorLine)));
        var flat = new HarmonyMethod(typeof(Plugin), nameof(BeforeFlatSound));
        harmony!.Patch(AccessTools.Method(typeof(AudioManager), "PlayAudioClip", new[] { typeof(AudioData.AudioClipName), typeof(bool) }), prefix: flat);
        harmony.Patch(AccessTools.Method(typeof(AudioManager), "PlayAudioClip", new[] { typeof(AudioData.AudioClipName), typeof(float) }), prefix: flat);
        harmony.Patch(AccessTools.Method(typeof(AudioManager), "PlayAudioClip", new[] { typeof(AudioData.AudioClipName), typeof(float), typeof(float), typeof(bool) }), prefix: flat);
        var placed = new HarmonyMethod(typeof(Plugin), nameof(BeforePlacedSound));
        harmony.Patch(AccessTools.Method(typeof(AudioManager), "PlayAudioClip", new[] { typeof(AudioData.AudioClipName), typeof(Vector3), typeof(float), typeof(float) }), prefix: placed);
        harmony.Patch(AccessTools.Method(typeof(AudioManager), "PlayAudioClipOnLocation"), prefix: placed);
        harmony.Patch(AccessTools.Method(typeof(MonsterSounds), "PlayClip"), prefix: new HarmonyMethod(typeof(Plugin), nameof(BeforeMonsterSound)));
    }

    private static bool Audible(Vector3 position) => Player.PlayerChar == null || (Player.PlayerChar.transform.position - position).sqrMagnitude < FarSound * FarSound;

    private static void BeforeEmitterScope(Component __instance, out Vector3? __state)
    {
        var plugin = Active;
        __state = plugin?.soundEmitter;
        if (plugin != null && __instance != Player.PlayerChar) plugin.soundEmitter = __instance.transform.position;
    }

    private static void BeforeDropSound(Vector3 worldPosStart, out Vector3? __state)
    {
        var plugin = Active;
        __state = plugin?.soundEmitter;
        if (plugin != null) plugin.soundEmitter = worldPosStart;
    }

    private static Exception? AfterEmitterScope(Exception __exception, Vector3? __state)
    {
        if (Active != null) Active.soundEmitter = __state;
        return __exception;
    }

    private static bool NearEmitter(out Vector3 emitter)
    {
        var source = Active?.soundEmitter;
        emitter = source ?? Vector3.zero;
        return source == null || Player.PlayerChar == null || (Player.PlayerChar.transform.position - emitter).sqrMagnitude <= NearSound * NearSound;
    }

    private static bool BeforeFlatSound(AudioManager __instance, AudioData.AudioClipName clip)
    {
        if (NearEmitter(out var emitter)) return true;
        if (Audible(emitter)) __instance.PlayAudioClipOnLocation(clip, emitter);
        return false;
    }

    private static bool BeforeNarratorLine() => NearEmitter(out _);

    private static bool BeforePlacedSound(Vector3 position) => Audible(position);

    private static bool BeforeMonsterSound(MonsterSounds __instance) => Audible(__instance.transform.position);
}
