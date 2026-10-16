using System;
using HarmonyLib;
using Tableflip;
using UnityEngine;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float NearSound = 10f, FarSound = 30f;
    private Vector3? soundEmitter;

    private void InstallSoundHooks()
    {
        var scoped = new HarmonyMethod(typeof(Plugin), nameof(BeforeEmitterScope));
        var unscoped = new HarmonyMethod(typeof(Plugin), nameof(AfterEmitterScope));
        foreach (var method in new[]
        {
            AccessTools.Method(typeof(Character), "PerformAttack"), AccessTools.Method(typeof(Character), "RollAttack"), AccessTools.Method(typeof(Character), "CompleteSpell"),
            AccessTools.Method(typeof(ContainerDestroyable), "Destroyed"), AccessTools.Method(typeof(ContainerInteractable), "Interact"), AccessTools.Method(typeof(PartyMember), "PassOut")
        }) harmony!.Patch(method, prefix: scoped, finalizer: unscoped);
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

    private static Exception? AfterEmitterScope(Exception __exception, Vector3? __state)
    {
        if (Active != null) Active.soundEmitter = __state;
        return __exception;
    }

    private static bool BeforeFlatSound(AudioManager __instance, AudioData.AudioClipName clip)
    {
        var emitter = Active?.soundEmitter;
        var player = Player.PlayerChar;
        if (emitter == null || player == null || (player.transform.position - emitter.Value).sqrMagnitude <= NearSound * NearSound) return true;
        if (Audible(emitter.Value)) __instance.PlayAudioClipOnLocation(clip, emitter.Value);
        return false;
    }

    private static bool BeforePlacedSound(Vector3 position) => Audible(position);

    private static bool BeforeMonsterSound(MonsterSounds __instance) => Audible(__instance.transform.position);
}
