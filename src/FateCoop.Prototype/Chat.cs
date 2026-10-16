using System;
using System.Collections.Generic;
using Cleverous.Prodigy.Input;
using Cleverous.ThatHurtModule;
using Tableflip;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const int ChatHistory = 8, ChatMaxLength = 200;
    private const float ChatFadeSeconds = 10f, ChatLineHeight = 22f, ChatWidth = 420f, BubbleHeight = 2.6f, BubbleSize = 0.9f;
    private readonly List<(string line, float time)> chatLines = new List<(string, float)>();
    private string chatDraft = "";
    private bool chatOpen;
    private int chatReceived;
    private GUIStyle? chatStyle;

    private void UpdateChat()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        var enter = keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame;
        if (chatOpen)
        {
            if (enter) SendChat(chatDraft);
            if (enter || keyboard.escapeKey.wasPressedThisFrame) CloseChat();
            return;
        }
        if (!enter || !Connected || transitioning || Player.PlayerChar == null || MenuOpen()) return;
        chatOpen = true;
        chatDraft = "";
        SingletonBehaviour<InputBroker>.Instance.DisableAllInput();
    }

    private void CloseChat()
    {
        if (!chatOpen) return;
        chatOpen = false;
        SingletonBehaviour<InputBroker>.Instance.EnableAllInput();
        SingletonBehaviour<InputBroker>.Instance.SetInputMode(InputBroker.InputMode.Game);
    }

    private void SendChat(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > ChatMaxLength) text = text.Substring(0, ChatMaxLength);
        Send("chat", text);
        AddChatLine(LocalName(), text, Player.PlayerChar);
    }

    private void ReceiveChat(Partner partner, string text)
    {
        if (text.Length == 0 || text.Length > ChatMaxLength) throw new InvalidOperationException("Invalid chat message.");
        AddChatLine(partner.Name, text, partner.Actor);
        chatReceived++;
    }

    private void AddChatLine(string name, string text, Character? speaker)
    {
        chatLines.Add((name + ": " + text, Time.unscaledTime));
        if (chatLines.Count > ChatHistory) chatLines.RemoveAt(0);
        if (speaker != null && ThatHurt.Instance != null) ThatHurt.PopText(speaker.transform.position + Vector3.up * BubbleHeight, text, BubbleSize, ChatFadeSeconds / 2f, ThatHurt.ThatHurtColor.White, 0f);
    }

    private void DrawChat(float bottom)
    {
        chatStyle ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, wordWrap = false };
        if (chatOpen)
        {
            bottom -= ChatLineHeight + 4f;
            GUI.SetNextControlName("FateCoopChat");
            chatDraft = GUI.TextField(new Rect(12f, bottom, ChatWidth, ChatLineHeight + 2f), chatDraft, ChatMaxLength);
            GUI.FocusControl("FateCoopChat");
        }
        for (var i = chatLines.Count - 1; i >= 0; i--)
        {
            var age = Time.unscaledTime - chatLines[i].time;
            if (!chatOpen && age > ChatFadeSeconds) break;
            bottom -= ChatLineHeight;
            var alpha = chatOpen ? 1f : Mathf.Clamp01(ChatFadeSeconds - age);
            var area = new Rect(12f, bottom, ChatWidth, ChatLineHeight);
            GUI.color = new Color(0f, 0f, 0f, alpha);
            GUI.Label(new Rect(area.x + 1f, area.y + 1f, area.width, area.height), chatLines[i].line, chatStyle);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(area, chatLines[i].line, chatStyle);
        }
        GUI.color = Color.white;
    }
}
