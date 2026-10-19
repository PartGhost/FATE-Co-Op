using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tableflip;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float PingInterval = 2f, HitchSeconds = 0.1f, LoggedFreezeSeconds = 0.5f, HitchWindow = 60f, StatsWidth = 300f, StatsLineHeight = 20f;
    private readonly Queue<(float time, float length)> hitches = new Queue<(float time, float length)>();
    private float nextPing;
    private bool showNetStats;

    private void UpdateNetStats(Keyboard? keyboard)
    {
        if (keyboard != null && keyboard.f10Key.wasPressedThisFrame) showNetStats = !showNetStats;
        if (Time.unscaledDeltaTime >= HitchSeconds) hitches.Enqueue((Time.unscaledTime, Time.unscaledDeltaTime));
        if (Time.unscaledDeltaTime >= LoggedFreezeSeconds) Logger.LogInfo($"Freeze of {Time.unscaledDeltaTime:0.00} s in {SceneLoad.CurrentScene}");
        while (hitches.Count > 0 && hitches.Peek().time < Time.unscaledTime - HitchWindow) hitches.Dequeue();
        if (!Connected || Time.unscaledTime < nextPing) return;
        nextPing = Time.unscaledTime + PingInterval;
        Send("ping", Time.unscaledTime.ToString("R", CultureInfo.InvariantCulture), reliable: false);
    }

    private void TakePong(Partner partner, string body) => partner.Ping = Time.unscaledTime - float.Parse(body, CultureInfo.InvariantCulture);

    private List<string> NetStatLines()
    {
        var lines = new List<string> { hitches.Count == 0 ? "No freezes in the last minute" : $"Freezes in the last minute: {hitches.Count}, worst {hitches.Max(h => h.length):0.00} s" };
        lines.AddRange(partners.Values.Select(p => $"{(p.Name.Length > 0 ? p.Name : "Player " + p.Id)}: ping {(p.Ping < 0f ? "-" : Mathf.RoundToInt(p.Ping * 1000f) + " ms")}, heard {Time.unscaledTime - p.LastMessage:0.0} s ago"));
        return lines;
    }

    private void DrawNetStats()
    {
        if (!showNetStats) return;
        chatStyle ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, wordWrap = false };
        var lines = NetStatLines();
        var area = new Rect((Screen.width - StatsWidth) / 2f, 8f, StatsWidth, lines.Count * StatsLineHeight + 8f);
        GUI.Box(area, "");
        for (var i = 0; i < lines.Count; i++) GUI.Label(new Rect(area.x + 8f, area.y + 4f + i * StatsLineHeight, StatsWidth - 16f, StatsLineHeight), lines[i], chatStyle);
    }
}
