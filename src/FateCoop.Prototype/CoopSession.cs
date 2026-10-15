using System;
using System.Text;
using HarmonyLib;
using Steamworks;
using Tableflip;
using UnityEngine;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    internal const string Version = "0.14.0";
    internal const int MaxMessageBytes = Constants.k_cbMaxSteamNetworkingSocketsMessageSizeSend;
    private const float FrameInterval = 0.05f, PeerTimeout = 10f;
    private IPeerLink? link;
    private bool isHost = true, peerConnected;
    private float lastPeerMessage = float.NegativeInfinity, nextFrame;
    private PeerFrame? remoteFrame;
    private string requestedWorld = "";
    private ulong commandLineLobby;
    private Callback<GameLobbyJoinRequested_t>? lobbyInvites;
    private Callback<GameRichPresenceJoinRequested_t>? presenceJoins;

    [Serializable] private sealed class Envelope
    {
        public string version = "", type = "", body = "";
    }
    [Serializable] private sealed class PeerFrame
    {
        public string worldKey = "", scene = "";
        public Vector3 primaryPosition = Vector3.zero;
        public float primaryYaw = 0, swingSpeed = 0, xpPercent = 0;
        public int swings, casts, hp, maxHp, mana, maxMana, level, depth, fishing, arrows, levelUps, spells, spellTarget;
        public Vector3 arrowEnd, spellAim;
        public string petJson = "", castSpell = "", effects = "", spellName = "";
    }

    internal static string Compatibility => Version + "/" + Application.version;
    private static bool SteamReady => AccessTools.StaticFieldRefAccess<SteamManager, bool>("s_EverInitialized");

    private static ulong LobbyFromArguments(string[] args)
    {
        var index = Array.IndexOf(args, "+connect_lobby");
        return index >= 0 && index + 1 < args.Length && ulong.TryParse(args[index + 1], out var lobby) ? lobby : 0;
    }

    private void StartSession(IPeerLink next, bool host)
    {
        EndSession("");
        link = next;
        isHost = host;
        nextFrame = 0;
        autoContinued = false;
    }

    private void JoinSteam(CSteamID lobby)
    {
        if (!SteamReady) throw new InvalidOperationException("Steam is not running for this game.");
        StartSession(SteamLink.Join(lobby, Notify), false);
    }

    private void UpdateSession()
    {
        if (lobbyInvites == null && SteamReady)
        {
            lobbyInvites = Callback<GameLobbyJoinRequested_t>.Create(request => JoinSteam(request.m_steamIDLobby));
            presenceJoins = Callback<GameRichPresenceJoinRequested_t>.Create(request =>
            {
                var lobby = LobbyFromArguments(request.m_rgchConnect.Split(' '));
                if (lobby != 0) JoinSteam(new CSteamID(lobby));
            });
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
            if (connected)
            {
                sharedSession = true;
                sentProfileKey = "";
                sentQuests = "";
                Fate.SetPause(false);
                if (isHost) Send("resync", "");
                SetStatus("Co-op partner connected.");
            }
            else { DropPeer(); Notify("Your co-op partner stopped responding."); }
        }
        if (Time.unscaledTime >= nextFrame)
        {
            nextFrame = Time.unscaledTime + FrameInterval;
            Send("frame", JsonUtility.ToJson(LocalFrame()));
        }
        if (!peerConnected) return;
        SendProfileIfChanged();
        if (isHost) { SyncMonsters(); SyncGround(); SyncQuests(); }
        else if (remoteFrame != null) FollowHost(remoteFrame);
    }

    private PeerFrame LocalFrame()
    {
        var host = Player.PlayerChar;
        return new PeerFrame
        {
            worldKey = SharedWorldReady() ? CurrentWorldKey() : "", scene = SceneLoad.CurrentScene.ToString(),
            primaryPosition = host != null ? host.transform.position : Vector3.zero, primaryYaw = host != null ? host.transform.eulerAngles.y : 0f,
            hp = host != null ? host.HP() : 0, maxHp = host != null ? host.MaxHP() : 0, mana = host != null ? host.Mana() : 0, maxMana = host != null ? host.MaxMana() : 0,
            level = host != null ? host.Level : 0, xpPercent = host != null ? host.GetPctToNextLevel() : 0f, depth = LevelGenerator.currentInstance != null ? LevelGenerator.currentInstance.GetLevelDepth() : 0,
            petJson = JsonUtility.ToJson(LocalPetSnapshot()), swings = localSwings, swingSpeed = localSwingSpeed, casts = localCasts, castSpell = localCastSpell,
            effects = host != null ? ActiveEffectNames(host) : "", fishing = LocalFishing(), arrows = localArrows, arrowEnd = localArrowEnd, levelUps = CountLevelUps(),
            spells = localSpells, spellName = localSpellName, spellAim = localSpellAim, spellTarget = localSpellTarget
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
                    if (!isHost || SceneLoad.CurrentScene != Tableflip.Scene.Dungeon || !SharedWorldReady()) break;
                    monsterStates.Clear();
                    announcedGround.Clear();
                    announcedFixtures.Clear();
                    Send("world", JsonUtility.ToJson(ExportWorld()));
                    break;
                case "resync":
                    if (isHost) throw new InvalidOperationException("Only the host asks for a resync.");
                    ForgetHostWorld();
                    break;
                case "world":
                    if (isHost) throw new InvalidOperationException("Only the host sends dungeon floors.");
                    if (transitioning) { requestedWorld = ""; break; }
                    JoinWorld(JsonUtility.FromJson<WorldSnapshot>(envelope.body));
                    break;
                case "profile": ReceiveProfile(envelope.body); break;
                case "monsters": ReceiveMonsters(envelope.body); break;
                case "hit": ApplyHit(envelope.body); break;
                case "strike": TakeStrike(envelope.body); break;
                case "hurt": TakeHurt(envelope.body); break;
                case "effect": TakeEffect(envelope.body); break;
                case "kill": TakeKill(envelope.body); break;
                case "pop": ShowDamagePopup(envelope.body); break;
                case "ground": ReceiveGround(envelope.body); break;
                case "ground-gone": ReceiveGroundGone(envelope.body); break;
                case "take": GiveTake(envelope.body); break;
                case "granted": ReceiveGranted(envelope.body); break;
                case "drop": ReceiveDrop(envelope.body); break;
                case "use-fixture": UseFixture(envelope.body); break;
                case "fixture": ApplyFixture(envelope.body); break;
                case "quests": ReceiveQuests(envelope.body); break;
                case "quest-reward": TakeQuestReward(envelope.body); break;
                case "use-ward": UseWard(envelope.body); break;
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
        remoteFrame = frame;
        if (frame.worldKey.Length > 0 && frame.worldKey == CurrentWorldKey() && SharedWorldReady())
        {
            ApplyPeerFrame(frame);
            networkAccepted++;
        }
        else if (actor != null && !transitioning) Despawn();
    }

    private void FollowHost(PeerFrame host)
    {
        if (transitioning || Player.PlayerChar == null || SceneLoad.CurrentScene != Tableflip.Scene.Dungeon) return;
        if (host.worldKey == "GroveTown")
        {
            if (loadedWorldKey.Length == 0) return;
            ForgetHostWorld();
            Notify("The host went to town. This floor is yours until they come back down.");
        }
        else if (host.scene == Tableflip.Scene.Dungeon.ToString() && host.worldKey.Length > 0 && host.worldKey != CurrentWorldKey() && host.worldKey != requestedWorld)
        {
            requestedWorld = host.worldKey;
            Send("world-request", "");
            Logger.LogInfo("REQUESTED_HOST_FLOOR " + host.worldKey);
        }
    }

    private void ForgetHostWorld()
    {
        loadedWorldKey = "";
        requestedWorld = "";
        ResetMonsters();
        ResetGround();
    }

    private void DropPeer()
    {
        Despawn();
        LeaveSharedWorld();
        if (!isHost) loadedWorldKey = "";
        remoteFrame = null;
        remoteProfileJson = "";
        partnerName = "";
        partyQuests = "";
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
        if (reason.Length > 0) Notify(reason);
    }
}
