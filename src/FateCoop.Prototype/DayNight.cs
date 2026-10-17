using System;
using System.Collections.Generic;
using System.Linq;
using Tableflip;
using UnityEngine;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float AlwaysDay = -1f, NightSun = 0.25f, NightLampIntensity = 6f, NightLampRange = 4f, NightWaterShade = 0.85f, NightFoam = 0.6f;
    private const string WaterColor = "_WaterColor", WaterFoam = "_FoamIntensity";
    private static readonly Color Dusk = new Color(1f, 0.78f, 0.6f), DuskSky = new Color(1f, 0.88f, 0.8f), Moon = new Color(0.55f, 0.65f, 1f), NightSky = new Color(0.6f, 0.7f, 1f);
    private static readonly (float Phase, float Sun, Color SunTint, float Ambient, Color AmbientTint)[] DayKeys =
    {
        (0f, 1f, Color.white, 1f, Color.white),
        (0.6f, 1f, Color.white, 1f, Color.white),
        (0.65f, 0.6f, Dusk, 0.7f, DuskSky),
        (0.7f, NightSun, Moon, 0.42f, NightSky),
        (0.9f, NightSun, Moon, 0.42f, NightSky),
        (0.95f, 0.6f, Dusk, 0.7f, DuskSky),
        (1f, 1f, Color.white, 1f, Color.white)
    };
    private readonly List<(Light light, float intensity, float range)> groveLamps = new List<(Light, float, float)>();
    private readonly List<(Material material, Color color, float foam)> groveWater = new List<(Material, Color, float)>();
    private float dayLengthSeconds, dayOffset;
    private Light? groveSun;
    private float daySunIntensity;
    private Color daySunColor, daySky, dayEquator, dayGround;

    private void CaptureGroveDay()
    {
        groveSun = RenderSettings.sun;
        if (groveSun == null) return;
        daySunIntensity = groveSun.intensity;
        daySunColor = groveSun.color;
        daySky = RenderSettings.ambientSkyColor;
        dayEquator = RenderSettings.ambientEquatorColor;
        dayGround = RenderSettings.ambientGroundColor;
        var roots = groveSun.gameObject.scene.GetRootGameObjects();
        groveLamps.Clear();
        groveLamps.AddRange(roots.SelectMany(root => root.GetComponentsInChildren<Light>()).Where(light => light.type == LightType.Point).Select(light => (light, light.intensity, light.range)));
        groveWater.Clear();
        groveWater.AddRange(roots.SelectMany(root => root.GetComponentsInChildren<Renderer>()).Where(renderer => renderer.sharedMaterial != null && renderer.sharedMaterial.HasProperty(WaterColor))
            .Select(renderer => renderer.material).Select(water => (water, water.GetColor(WaterColor), water.GetFloat(WaterFoam))));
    }

    private float LocalDayPhase() => dayLengthSeconds > 0f ? (Time.realtimeSinceStartup / dayLengthSeconds + dayOffset) % 1f : AlwaysDay;

    private float DayPhase()
    {
        if (dayLengthSeconds <= 0f) return AlwaysDay;
        return !isHost && partners.TryGetValue(HostId, out var host) && host.Frame != null && host.Frame.dayPhase >= 0f ? host.Frame.dayPhase : LocalDayPhase();
    }

    private void UpdateDayNight()
    {
        if (groveSun == null || SceneLoad.CurrentScene != Tableflip.Scene.Town_Grove) return;
        var phase = DayPhase();
        var (sun, sunTint, ambient, ambientTint) = phase < 0f ? (1f, Color.white, 1f, Color.white) : DayAt(phase);
        groveSun.intensity = daySunIntensity * sun;
        groveSun.color = daySunColor * sunTint;
        RenderSettings.ambientSkyColor = daySky * ambientTint * ambient;
        RenderSettings.ambientEquatorColor = dayEquator * ambientTint * ambient;
        RenderSettings.ambientGroundColor = dayGround * ambientTint * ambient;
        var darkness = Mathf.InverseLerp(1f, NightSun, sun);
        foreach (var (lamp, intensity, range) in groveLamps)
        {
            if (lamp == null) continue;
            lamp.intensity = intensity * Mathf.Lerp(1f, NightLampIntensity, darkness);
            lamp.range = range * Mathf.Lerp(1f, NightLampRange, darkness);
        }
        foreach (var (water, color, foam) in groveWater)
        {
            if (water == null) continue;
            water.SetColor(WaterColor, Color.Lerp(color, color * NightSky * NightWaterShade, darkness));
            water.SetFloat(WaterFoam, foam * Mathf.Lerp(1f, NightFoam, darkness));
        }
    }

    private static (float sun, Color sunTint, float ambient, Color ambientTint) DayAt(float phase)
    {
        var next = Array.FindIndex(DayKeys, key => key.Phase >= phase);
        var to = DayKeys[Math.Max(1, next)];
        var from = DayKeys[Math.Max(1, next) - 1];
        var t = Mathf.SmoothStep(0f, 1f, (phase - from.Phase) / (to.Phase - from.Phase));
        return (Mathf.Lerp(from.Sun, to.Sun, t), Color.Lerp(from.SunTint, to.SunTint, t), Mathf.Lerp(from.Ambient, to.Ambient, t), Color.Lerp(from.AmbientTint, to.AmbientTint, t));
    }
}
