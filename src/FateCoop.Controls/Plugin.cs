using System;
using System.Collections;
using System.Globalization;
using BepInEx;
using BepInEx.Unity.Mono;
using Cleverous.Prodigy.Input;
using FateCoop.Core;
using HarmonyLib;
using Tableflip;
using UnityEngine;

namespace FateCoop.Controls;

[BepInPlugin(Guid, "FATE Co-Op Controls", SuiteVersion.Value)]
[BepInDependency(CorePlugin.Guid)]
public sealed partial class Plugin : BaseUnityPlugin
{
    public const string Guid = "local.fatecoop.controls";
    internal static Plugin? Active;
    private Harmony? harmony;

    [Serializable] private sealed class Status
    {
        public string inputState = "";
    }

    private void Awake()
    {
        harmony = new Harmony(Guid);
        try { InstallControlHooks(); }
        catch (Exception e)
        {
            Logger.LogError(e);
            harmony.UnpatchSelf();
            return;
        }
        Active = this;
        if (!TestChannel.Enabled) return;
        TestChannel.Commands["test-move"] = parts => StartCoroutine(HoldMove(new Vector2(float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture)), float.Parse(parts[3], CultureInfo.InvariantCulture)));
        TestChannel.Commands["test-turn"] = parts =>
        {
            testTurn = float.Parse(parts[1], CultureInfo.InvariantCulture);
            StartCoroutine(StopTurnNextFrame());
        };
        TestChannel.Status.Add(() => new Status
        {
            inputState = SingletonBehaviour<InputBroker>.InstanceExists()
                ? $"{SingletonBehaviour<InputBroker>.Instance.ActiveInputMode} {SingletonBehaviour<InputBroker>.Instance.LocalInputBehavior} blockers {InputBlocker.uiBlockers} menus {InputBlocker.BlockingMenuCount} locked {BrokerMovementLocked(SingletonBehaviour<InputBroker>.Instance)}"
                : ""
        });
    }

    private IEnumerator HoldMove(Vector2 input, float seconds)
    {
        testMove = input;
        yield return new WaitForSecondsRealtime(seconds);
        testMove = Vector2.zero;
    }

    private IEnumerator StopTurnNextFrame()
    {
        yield return null;
        testTurn = 0f;
    }

    private void OnDestroy()
    {
        harmony?.UnpatchSelf();
        if (Active == this) Active = null;
    }
}
