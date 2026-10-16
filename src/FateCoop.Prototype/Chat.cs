using System;
using System.Collections.Generic;
using Cleverous.Prodigy.Input;
using Tableflip;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const int ChatHistory = 8, ChatMaxLength = 200;
    private const float ChatFadeSeconds = 10f, ChatLineHeight = 22f, ChatWidth = 420f;
    private readonly List<(string line, float time)> chatLines = new List<(string, float)>();
    private string chatDraft = "";
    private bool chatOpen;
    private int chatOpenedFrame, chatReceived;
    private GUIStyle? chatStyle;

    private void UpdateChat()
    {
        var keyboard = Keyboard.current;
        if (chatOpen || !peerConnected || keyboard == null || !keyboard.enterKey.wasPressedThisFrame || transitioning || Player.PlayerChar == null
            || PauseUI.IsShown || PromptUI.IsPromptActive || SingletonBehaviour<InputBroker>.Instance.ActiveInputMode != InputBroker.InputMode.Game) return;
        chatOpen = true;
        chatOpenedFrame = Time.frameCount;
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
        AddChatLine(LocalName(), text);
    }

    private void ReceiveChat(string text)
    {
        if (text.Length == 0 || text.Length > ChatMaxLength) throw new InvalidOperationException("Invalid chat message.");
        AddChatLine(partnerName, text);
        chatReceived++;
    }

    private void AddChatLine(string name, string text)
    {
        chatLines.Add((name + ": " + text, Time.unscaledTime));
        if (chatLines.Count > ChatHistory) chatLines.RemoveAt(0);
    }

    private void DrawChat(float bottom)
    {
        chatStyle ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, wordWrap = false };
        if (chatOpen)
        {
            var current = Event.current;
            if (current.type == EventType.KeyDown && Time.frameCount != chatOpenedFrame)
            {
                if (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter)
                {
                    SendChat(chatDraft);
                    CloseChat();
                    current.Use();
                    return;
                }
                if (current.keyCode == KeyCode.Escape)
                {
                    CloseChat();
                    current.Use();
                    return;
                }
            }
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
