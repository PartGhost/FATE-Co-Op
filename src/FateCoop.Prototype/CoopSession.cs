using System;
using System.Text;
using HarmonyLib;
using Steamworks;
using Tableflip;
using UnityEngine;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    internal const string Version = "0.7.0";
    internal const int MaxMessageBytes = Constants.k_cbMaxSteamNetworkingSocketsMessageSizeSend;
    private const float FrameInterval = 0.05f, PeerTimeout = 10f;
    private IPeerLink? link;
    private bool isHost = true, peerConnected;
    private float lastPeerMessage = float.NegativeInfinity, nextFrame;
    private PeerFrame? hostFrame;
    private string requestedWorld = "";
    private ulong commandLineLobby;
    private Callback<GameLobbyJoinRequested_t>? lobbyInvites;

    [Serializable] private sealed class Envelope
    {
        public string version = "", type = "", body = "";
    }
    [Serializable] private sealed class PeerFrame
    {
        public string worldKey = "", scene = "";
        public Vector3 primaryPosition = Vector3.zero;
        public float primaryYaw = 0;
        public string petJson = "", combatJson = "", attackJson = "";
    }

    internal static string Compatibility => Version + "/" + Application.version;
    private static bool SteamReady => AccessTools.StaticFieldRefAccess<SteamManager, bool>("s_EverInitialized");

    private static ulong LobbyFromCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "+connect_lobby");
        return index >= 0 && index + 1 < args.Length && ulong.TryParse(args[index + 1], out var lobby) ? lobby : 0;
    }

    private void StartSession(IPeerLink next, bool host)
    {
        EndSession("Starting a new co-op session.");
        link = next;
        isHost = host;
        nextFrame = 0;
    }

    private void JoinSteam(CSteamID lobby)
    {
        if (!SteamReady) throw new InvalidOperationException("Steam is not running for this game.");
        StartSession(SteamLink.Join(lobby, SetStatus), false);
    }

    private void UpdateSession()
    {
        if (lobbyInvites == null && SteamReady)
        {
            lobbyInvites = Callback<GameLobbyJoinRequested_t>.Create(request => JoinSteam(request.m_steamIDLobby));
            if (commandLineLobby != 0) JoinSteam(new CSteamID(commandLineLobby));
        }
        if (link == null) return;
        if (link.CloseReason.Length > 0) { EndSession(link.CloseReason); return; }
        while (link != null && link.TryReceive(out var message)) Receive(message);
        if (link == null) return;
        var connected = Time.unscaledTime - lastPeerMessage < PeerTimeout;
        if (connected != peerConnected)
        {
            peerConnected = connected;
            if (connected) { movementSession = true; SetStatus("Co-op partner connected."); }
            else { DropPeer(); SetStatus("Co-op partner stopped responding."); }
        }
        if (Time.unscaledTime >= nextFrame)
        {
            nextFrame = Time.unscaledTime + FrameInterval;
            Send("frame", JsonUtility.ToJson(LocalFrame()));
        }
        if (peerConnected && !isHost && hostFrame != null) FollowHost(hostFrame);
    }

    private PeerFrame LocalFrame()
    {
        var host = Player.PlayerChar;
        return new PeerFrame
        {
            worldKey = SharedWorldReady() ? CurrentWorldKey() : "", scene = SceneLoad.CurrentScene.ToString(),
            primaryPosition = host != null ? host.transform.position : Vector3.zero, primaryYaw = host != null ? host.transform.eulerAngles.y : 0f,
            petJson = JsonUtility.ToJson(LocalPetSnapshot()), combatJson = isHost ? JsonUtility.ToJson(CombatState()) : "",
            attackJson = pendingAttack != null ? JsonUtility.ToJson(pendingAttack) : ""
        };
    }

    private void Send(string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new Envelope { version = Compatibility, type = type, body = body }));
        if (bytes.Length > MaxMessageBytes) { Logger.LogError($"PEER_MESSAGE_TOO_LARGE type={type} bytes={bytes.Length}"); return; }
        link!.Send(bytes);
    }

    private void Receive(byte[] bytes)
    {
        var envelope = JsonUtility.FromJson<Envelope>(Encoding.UTF8.GetString(bytes));
        if (envelope == null || envelope.version != Compatibility)
        {
            EndSession("Your partner runs a different mod or game version (" + envelope?.version + ", this game runs " + Compatibility + ").");
            return;
        }
        lastPeerMessage = Time.unscaledTime;
        try
        {
            switch (envelope.type)
            {
                case "frame": ReceiveFrame(JsonUtility.FromJson<PeerFrame>(envelope.body)); break;
                case "world-request":
                    if (isHost && SceneLoad.CurrentScene == Tableflip.Scene.Dungeon && SharedWorldReady()) Send("world", JsonUtility.ToJson(ExportWorld()));
                    break;
                case "world":
                    if (isHost) throw new InvalidOperationException("Only the host sends dungeon floors.");
                    if (transitioning) { requestedWorld = ""; break; }
                    JoinWorld(JsonUtility.FromJson<WorldSnapshot>(envelope.body));
                    break;
                default: throw new InvalidOperationException("Unknown peer message: " + envelope.type);
            }
        }
        catch (Exception e)
        {
            networkRejected++;
            Logger.LogWarning("PEER_MESSAGE_REJECTED " + envelope.type + ": " + e.Message);
        }
    }

    private void ReceiveFrame(PeerFrame frame)
    {
        if (frame == null) throw new InvalidOperationException("Empty peer frame.");
        if (!isHost) hostFrame = frame;
        if (frame.worldKey.Length > 0 && frame.worldKey == CurrentWorldKey() && SharedWorldReady())
        {
            ApplyPeerFrame(frame);
            networkAccepted++;
        }
        else if (actor != null && !transitioning) Despawn();
    }

    private void FollowHost(PeerFrame host)
    {
        if (transitioning || Player.PlayerChar == null) return;
        if (host.worldKey == "GroveTown" && SceneLoad.CurrentScene == Tableflip.Scene.Dungeon)
        {
            requestedWorld = "";
            Execute("town");
        }
        else if (host.scene == Tableflip.Scene.Dungeon.ToString() && host.worldKey.Length > 0 && host.worldKey != CurrentWorldKey() && host.worldKey != requestedWorld)
        {
            requestedWorld = host.worldKey;
            Send("world-request", "");
            Logger.LogInfo("REQUESTED_HOST_FLOOR " + host.worldKey);
        }
    }

    private void DropPeer()
    {
        Despawn();
        RestoreSessionSafety();
        hostFrame = null;
        requestedWorld = "";
    }

    private void EndSession(string reason)
    {
        if (link == null) return;
        link.Dispose();
        link = null;
        if (peerConnected) DropPeer();
        peerConnected = false;
        isHost = true;
        lastPeerMessage = float.NegativeInfinity;
        SetStatus(reason);
    }
}
