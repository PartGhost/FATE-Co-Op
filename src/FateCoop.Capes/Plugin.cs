using System;
using BepInEx;
using BepInEx.Unity.Mono;
using FateCoop.Core;
using HarmonyLib;
using Tableflip;

namespace FateCoop.Capes;

[BepInPlugin(Guid, "FATE Co-Op Capes", SuiteVersion.Value)]
[BepInDependency(CorePlugin.Guid)]
public sealed partial class Plugin : BaseUnityPlugin
{
    public const string Guid = "local.fatecoop.capes";
    private Harmony? harmony;

    [Serializable] private sealed class Status
    {
        public float capeBend;
    }

    private void Awake()
    {
        harmony = new Harmony(Guid);
        try { InstallCapeHooks(); }
        catch (Exception e)
        {
            Logger.LogError(e);
            harmony.UnpatchSelf();
            return;
        }
        if (!TestChannel.Enabled) return;
        TestChannel.Commands["test-back"] = parts => Player.PlayerChar.GetComponentInChildren<CharacterCustomizationDefaults>(true).SetBack(parts[1]);
        TestChannel.Status.Add(() => new Status { capeBend = Player.PlayerChar != null && Player.PlayerChar.GetComponentInChildren<CapeSwing>() is CapeSwing swing ? swing.Bend : -1f });
    }

    private void OnDestroy() => harmony?.UnpatchSelf();
}
