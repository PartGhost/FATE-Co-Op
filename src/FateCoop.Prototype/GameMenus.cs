using System;
using System.Collections.Generic;
using System.Linq;
using Cleverous.ThatHurtModule;
using HarmonyLib;
using Steamworks;
using Tableflip;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float MessageSeconds = 5f, PauseButtonHeight = 66f;
    private readonly Queue<string> pendingPrompts = new Queue<string>();
    private readonly List<Button> mainMenuButtons = new List<Button>(), coopMenuButtons = new List<Button>();
    private MainMenuUI? mainMenu;
    private Button? hostPauseButton, invitePauseButton, leavePauseButton;
    private bool coopMenuOpen, autoContinued;

    private void InstallMenuHooks()
    {
        harmony!.Patch(AccessTools.Method(typeof(MainMenuUI), "Start"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterMainMenuStart)));
        harmony.Patch(AccessTools.Method(typeof(PauseUI), "OnMenuButton"), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterPauseOpened)));
    }

    private static Button CloneButton(Button source, string label, Action onClick)
    {
        var clone = Object.Instantiate(source.gameObject, source.transform.parent).GetComponent<Button>();
        Object.DestroyImmediate(clone.GetComponentInChildren<I2.Loc.Localize>(true));
        clone.GetComponentInChildren<TMP_Text>(true).text = label;
        clone.interactable = true;
        clone.onClick.AddListener(() =>
        {
            SingletonBehaviour<AudioManager>.Instance.PlayAudioClip(AudioData.AudioClipName.ButtonBasic, randomizePitch: false);
            onClick();
        });
        return clone;
    }

    private static void AfterMainMenuStart(MainMenuUI __instance)
    {
        if (Active == null || Fate.CurrentFateVer != Fate.FateVer.Original) return;
        Active.BuildMainMenu(__instance);
    }

    private void BuildMainMenu(MainMenuUI menu)
    {
        mainMenu = menu;
        coopMenuOpen = false;
        mainMenuButtons.Clear();
        coopMenuButtons.Clear();
        mainMenuButtons.AddRange(new[] { menu.continueButton, menu.newgameButton, menu.optionsButton, menu.creditsButton, menu.quitButton });
        var coop = CloneButton(menu.optionsButton, "Co-op", () => ShowCoopMenu(true));
        coop.transform.SetSiblingIndex(menu.continueButton.transform.GetSiblingIndex() + 1);
        mainMenuButtons.Add(coop);
        coopMenuButtons.Add(CloneButton(menu.optionsButton, "Host co-op", HostFromMenu));
        coopMenuButtons.Add(CloneButton(menu.optionsButton, "Join a friend", JoinFromMenu));
        coopMenuButtons.Add(CloneButton(menu.optionsButton, "Back", () => ShowCoopMenu(false)));
        foreach (var button in coopMenuButtons) button.gameObject.SetActive(false);
    }

    private void ShowCoopMenu(bool open)
    {
        if (mainMenu == null) return;
        coopMenuOpen = open;
        foreach (var button in mainMenuButtons) button.gameObject.SetActive(!open);
        foreach (var button in coopMenuButtons) button.gameObject.SetActive(open);
        var selected = open ? coopMenuButtons[0] : mainMenu.continueButton.interactable ? mainMenu.continueButton : mainMenu.newgameButton;
        EventSystem.current.SetSelectedGameObject(selected.gameObject);
    }

    private static bool CanContinue()
    {
        var slot = SaveData.CurrentSaveSlot;
        return SceneLoad.IsInMainMenuScene && slot != null && slot.targetGame == Fate.CurrentFateVer && slot.saveState == SaveSlotState.Normal && slot.saveData?.playerSaveData != null;
    }

    private void HostFromMenu()
    {
        if (!SteamReady) { Notify("Steam isn't running. Start Steam to host co-op."); return; }
        if (!CanContinue()) { Notify("Make or pick a character first, then host co-op."); return; }
        HostSteam();
        MainMenuUI.ContinueRequested();
    }

    private void JoinFromMenu()
    {
        if (!SteamReady) { Notify("Steam isn't running. Start Steam to join a friend."); return; }
        SteamFriends.ActivateGameOverlay("friends");
        Notify("Right-click your friend and choose Join Game, or accept their invite.");
    }

    private void ContinueForPartner()
    {
        if (isHost || autoContinued || !peerConnected || !SceneLoad.IsInMainMenuScene || Fate.CurrentFateVer != Fate.FateVer.Original) return;
        autoContinued = true;
        if (!CanContinue()) { Notify("Make or pick a character to join " + partnerName + "."); return; }
        MainMenuUI.ContinueRequested();
    }

    private static void AfterPauseOpened(PauseUI __instance)
    {
        Active?.UpdatePauseMenu(__instance);
    }

    private void UpdatePauseMenu(PauseUI pause)
    {
        var saveQuit = (Button)AccessTools.Field(typeof(PauseUI), "saveQuitButton").GetValue(pause);
        var options = (Button)AccessTools.Field(typeof(PauseUI), "optionsButton").GetValue(pause);
        var resume = (Button)AccessTools.Field(typeof(PauseUI), "resumeButton").GetValue(pause);
        if (hostPauseButton == null)
        {
            hostPauseButton = CloneButton(options, "Host co-op", () => { resume.onClick.Invoke(); TryHostSteam(); });
            invitePauseButton = CloneButton(options, "Invite friend", () => { resume.onClick.Invoke(); (link as SteamLink)?.Invite(); });
            leavePauseButton = CloneButton(options, "Leave co-op", () => { resume.onClick.Invoke(); EndSession("You left co-op."); });
            var index = options.transform.GetSiblingIndex() + 1;
            foreach (var button in new[] { hostPauseButton, invitePauseButton, leavePauseButton }) button.transform.SetSiblingIndex(index++);
            var content = (RectTransform)options.transform.parent.parent;
            content.sizeDelta += new Vector2(0f, 2 * PauseButtonHeight);
        }
        hostPauseButton.gameObject.SetActive(link == null && SteamReady);
        invitePauseButton!.gameObject.SetActive(link is SteamLink { CanInvite: true });
        leavePauseButton!.gameObject.SetActive(link != null);
        var order = new[] { saveQuit, options, hostPauseButton, invitePauseButton, leavePauseButton, resume }.Where(button => button.gameObject.activeSelf).ToArray();
        for (var i = 0; i < order.Length; i++)
        {
            var navigation = order[i].navigation;
            navigation.mode = Navigation.Mode.Explicit;
            navigation.selectOnUp = order[(i + order.Length - 1) % order.Length];
            navigation.selectOnDown = order[(i + 1) % order.Length];
            order[i].navigation = navigation;
        }
    }

    private void TryHostSteam()
    {
        try { HostSteam(); }
        catch (Exception e) { Notify(e.Message); }
    }

    private void Notify(string text)
    {
        SetStatus(text);
        var player = Player.PlayerChar;
        if (player != null && ThatHurt.Instance != null && ThatHurt.Instance.FloatiesEnabled)
            ThatHurt.PopText(player.transform.position + Vector3.up * 2f, text, 1.1f, MessageSeconds, ThatHurt.ThatHurtColor.White, 0f);
        else if (SceneLoad.IsInMainMenuScene) pendingPrompts.Enqueue(text);
    }

    private void UpdateMenus()
    {
        var keyboard = Keyboard.current;
        var gamepad = Gamepad.current;
        if (coopMenuOpen && ((keyboard != null && keyboard.escapeKey.wasPressedThisFrame) || (gamepad != null && gamepad.buttonEast.wasPressedThisFrame))) ShowCoopMenu(false);
        if (pendingPrompts.Count > 0 && SingletonBehaviour<PromptUI>.InstanceExists() && !PromptUI.IsPromptActive)
            SingletonBehaviour<PromptUI>.Instance.ShowPromptRaw(pendingPrompts.Dequeue(), null, twoButtons: false, "OK");
        ContinueForPartner();
    }
}
