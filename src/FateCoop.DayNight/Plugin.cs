using System;
using System.Globalization;
using BepInEx;
using BepInEx.Unity.Mono;
using FateCoop.Core;
using Tableflip;
using UnityEngine;

namespace FateCoop.DayNight;

[BepInPlugin(Guid, "FATE Co-Op Day and Night", SuiteVersion.Value)]
[BepInDependency(CorePlugin.Guid)]
public sealed partial class Plugin : BaseUnityPlugin
{
    public const string Guid = "local.fatecoop.daynight";

    [Serializable] private sealed class Status
    {
        public float dayPhase, sunIntensity;
    }

    private void Awake()
    {
        dayLengthSeconds = Math.Max(0f, Config.Bind("Grove", "DayLengthMinutes", 20f, "Minutes for one day and night in Grove. In co-op everyone follows the host's clock. 0 keeps it day.").Value) * 60f;
        GroveClock.LocalPhase = LocalDayPhase;
        SceneLoad.OnSceneLoaded += OnGameSceneLoaded;
        if (!TestChannel.Enabled) return;
        TestChannel.Commands["test-day"] = parts => dayOffset = ((float.Parse(parts[1], CultureInfo.InvariantCulture) - Time.realtimeSinceStartup / dayLengthSeconds) % 1f + 1f) % 1f;
        TestChannel.Status.Add(() => new Status { dayPhase = DayPhase(), sunIntensity = groveSun != null ? groveSun.intensity : 0f });
    }

    private void OnGameSceneLoaded(Tableflip.Scene scene)
    {
        if (scene == Tableflip.Scene.Town_Grove) CaptureGroveDay();
    }

    private void Update()
    {
        try { UpdateDayNight(); }
        catch (Exception e) { Logger.LogError(e); }
    }

    private void OnDestroy() => SceneLoad.OnSceneLoaded -= OnGameSceneLoaded;
}
