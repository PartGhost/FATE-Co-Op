using System;
using System.Linq;
using BepInEx;
using BepInEx.Unity.Mono;
using FateCoop.Core;
using HarmonyLib;
using Tableflip;
using Object = UnityEngine.Object;

namespace FateCoop.QuestLog;

[BepInPlugin(Guid, "FATE Co-Op Quest Log", SuiteVersion.Value)]
[BepInDependency(CorePlugin.Guid)]
public sealed partial class Plugin : BaseUnityPlugin
{
    public const string Guid = "local.fatecoop.questlog";
    private Harmony? harmony;

    private void Awake()
    {
        harmony = new Harmony(Guid);
        try { InstallQuestLogHooks(); }
        catch (Exception e)
        {
            Logger.LogError(e);
            harmony.UnpatchSelf();
            return;
        }
        SharedContent.Register(Info.Metadata);
        if (!TestChannel.Enabled) return;
        TestChannel.Commands["test-quest-scroll"] = parts =>
            UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(SideQuestRowsRef(Object.FindObjectOfType<QuestUI>()).Last(row => row.gameObject.activeSelf).questButton.gameObject);
    }

    private void OnDestroy() => harmony?.UnpatchSelf();
}
