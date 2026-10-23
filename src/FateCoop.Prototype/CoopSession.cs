using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FateCoop.Core;
using HarmonyLib;
using Steamworks;
using Tableflip;
using UnityEngine;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    internal const int MaxMessageBytes = Constants.k_cbMaxSteamNetworkingSocketsMessageSizeSend;
    internal const int HostId = 0;
    private const int Everyone = -1, Unassigned = -2;
    private const float FrameInterval = 0.05f, AwayFrameInterval = 0.5f, PeerTimeout = 10f;
    private static readonly HashSet<string> OwnerMessages = new HashSet<string> { "monsters", "strike", "hurt", "kill", "pop", "ground", "ground-gone", "ground-list", "fixture" };
    private static readonly HashSet<string> HostMessages = new HashSet<string> { "quests", "quest-reward" };
    private IPeerLink? link;
    private bool isHost = true;
    private int myId = HostId;
    private readonly SortedDictionary<int, Partner> partners = new SortedDictionary<int, Partner>();
    private readonly Dictionary<(int from, int to), float> awayRelays = new Dictionary<(int, int), float>();
    private float nextFrame, lastHostMessage;
    private int frameSeq;
    private ulong commandLineLobby;
    private Callback<GameLobbyJoinRequested_t>? lobbyInvites;
    private Callback<GameRichPresenceJoinRequested_t>? presenceJoins;

    [Serializable] private sealed class Envelope
    {
        public string version = "", type = "", body = "", world = "";
        public int from, to = Everyone;
    }
    [Serializable] private sealed class PeerFrame
    {
        public string worldKey = "", scene = "";
        public Vector3 primaryPosition = Vector3.zero;
        public float primaryYaw = 0, swingSpeed = 0, xpPercent = 0, time = 0, revive = 0, dayPhase = GroveClock.AlwaysDay, worldBorn;
        public int seq, owner, travelDepth, portalDepth, reviveTarget = Unassigned, dlcs;
        public int swings, casts, hp, maxHp, mana, maxMana, level, depth, fishing, arrows, levelUps, spells, spellTarget;
        public bool running, mapRevealed;
        public Vector3 arrowEnd, spellAim, portalPosition;
        public string petJson = "", castSpell = "", effects = "", spellName = "", portalWorld = "";
        public List<string> summonJson = new List<string>();
    }

    private bool Connected => partners.Count > 0;

    private float HostDayPhase() => !isHost && partners.TryGetValue(HostId, out var host) && host.Frame != null ? host.Frame.dayPhase : GroveClock.AlwaysDay;
    internal static string Compatibility => SuiteVersion.Value + "/" + Application.version + "/" + SharedContent.Key;
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
        myId = host ? HostId : Unassigned;
        lastHostMessage = Time.unscaledTime;
        nextFrame = 0;
        autoContinued = false;
        worldOwner = myId;
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
        DeliverDeferred();
        if (link == null) return;
        if (link.CloseReason.Length > 0) { EndSession(link.CloseReason); return; }
        while (link != null && link.TryReceive(out var peer, out var message)) Receive(peer, message);
        if (link == null) return;
        if (!isHost && Time.unscaledTime - lastHostMessage > PeerTimeout) { EndSession("The host stopped responding."); return; }
        foreach (var gone in partners.Values.Where(p => Time.unscaledTime - p.LastMessage > PeerTimeout).ToList()) DropPartner(gone, " stopped responding.");
        if (Time.unscaledTime >= nextFrame)
        {
            nextFrame = Time.unscaledTime + (WorldPartners().Any() ? FrameInterval : AwayFrameInterval);
            Send("frame", JsonUtility.ToJson(LocalFrame()), reliable: false);
        }
        UpdateHeldTravel();
        UpdateRevive();
        if (!Connected) return;
        SendProfileIfChanged();
        UpdateOwnership();
        if (OwnsWorld) { SyncMonsters(); SyncGround(); }
        SyncQuests();
        TrackPartnerTravel();
    }

    private PeerFrame LocalFrame()
    {
        var player = Player.PlayerChar;
        TrackLocalPortal();
        var portal = PortalEntry();
        return new PeerFrame
        {
            seq = ++frameSeq, time = Time.unscaledTime, dlcs = ownedDlcs, dayPhase = GroveClock.LocalPhase(),
            worldKey = SharedWorldReady() ? CurrentWorldKey() : "", worldBorn = worldBorn, owner = worldOwner, travelDepth = transitioning ? travelDepth : 0, scene = SceneLoad.CurrentScene.ToString(),
            primaryPosition = player != null ? player.transform.position : Vector3.zero, primaryYaw = player != null ? player.transform.eulerAngles.y : 0f, running = LocalRunning(), mapRevealed = MapRevealed(),
            hp = player != null ? player.HP() : 0, maxHp = player != null ? player.MaxHP() : 0, mana = player != null ? player.Mana() : 0, maxMana = player != null ? player.MaxMana() : 0,
            level = player != null ? player.Level : 0, xpPercent = player != null ? player.GetPctToNextLevel() : 0f, depth = RunFloorHere(),
            petJson = JsonUtility.ToJson(LocalPetSnapshot()), summonJson = LocalSummonSnapshots().Select(JsonUtility.ToJson).ToList(), swings = localSwings, swingSpeed = localSwingSpeed, casts = localCasts, castSpell = localCastSpell,
            effects = player != null ? ActiveEffectNames(player) : "", fishing = LocalFishing(), arrows = localArrows, arrowEnd = localArrowEnd, levelUps = CountLevelUps(),
            spells = localSpells, spellName = localSpellName, spellAim = localSpellAim, spellTarget = localSpellTarget, revive = reviveProgress, reviveTarget = reviveTarget, portalWorld = localPortalWorld, portalDepth = portal != null ? RunFloor(portal.dungeonRealm, portal.levelDepth) : 0, portalPosition = portal?.dungeonPosition ?? Vector3.zero
        };
    }

    private void Send(string type, string body, int to = Everyone, bool reliable = true, bool worldScoped = false)
    {
        var envelope = new Envelope { version = Compatibility, type = type, body = body, from = myId, to = to, world = worldScoped ? CurrentWorldKey() : "" };
        if (isHost) Deliver(envelope, reliable);
        else Transmit(HostId, envelope, reliable);
    }

    private void SendWorld(string type, string body) => Send(type, body, worldScoped: true);

    private void SendToOwner(string type, string body) => Send(type, body, worldOwner, worldScoped: true);

    private void Transmit(int peer, Envelope envelope, bool reliable)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(envelope));
        if (bytes.Length > MaxMessageBytes) { Logger.LogError($"PEER_MESSAGE_TOO_LARGE type={envelope.type} bytes={bytes.Length}"); return; }
        link!.Send(peer, bytes, reliable);
    }

    private void Deliver(Envelope envelope, bool reliable)
    {
        var sender = envelope.from == myId ? LocalFrameWorld() : partners.TryGetValue(envelope.from, out var origin) ? origin.Frame?.worldKey ?? "" : "";
        foreach (var target in partners.Values)
        {
            if (target.Id == envelope.from || (envelope.to != Everyone && envelope.to != target.Id)) continue;
            if (envelope.type == "frame" && !(sender.Length > 0 && target.Frame?.worldKey == sender))
            {
                var key = (envelope.from, target.Id);
                if (awayRelays.TryGetValue(key, out var next) && Time.unscaledTime < next) continue;
                awayRelays[key] = Time.unscaledTime + AwayFrameInterval;
            }
            Transmit(target.Id, envelope, reliable);
        }
    }

    private string LocalFrameWorld() => SharedWorldReady() ? CurrentWorldKey() : "";

    private void Receive(int peer, byte[] bytes)
    {
        Envelope envelope;
        try { envelope = JsonUtility.FromJson<Envelope>(Encoding.UTF8.GetString(bytes)); }
        catch (ArgumentException e)
        {
            networkRejected++;
            Logger.LogWarning("PEER_MESSAGE_REJECTED unreadable from " + peer + ": " + e.Message);
            return;
        }
        if (envelope == null || envelope.version != Compatibility)
        {
            if (!isHost) { EndSession("Your friends run a different FATE Co-Op version, game version or set of content mods (" + envelope?.version + ", this game runs " + Compatibility + ")."); return; }
            Transmit(peer, new Envelope { version = Compatibility, type = "mismatch" }, true);
            return;
        }
        if (isHost)
        {
            envelope.from = peer;
            if (!partners.ContainsKey(peer)) Welcome(peer);
            if (envelope.to != HostId) Deliver(envelope, envelope.type != "frame");
            if (envelope.to != HostId && envelope.to != Everyone) { partners[peer].LastMessage = Time.unscaledTime; return; }
        }
        else lastHostMessage = Time.unscaledTime;
        try
        {
            if (envelope.type == "welcome" || envelope.type == "left")
            {
                if (!isHost && envelope.from == HostId) TakeHostControl(envelope);
                return;
            }
            if (envelope.from == myId) return;
            var partner = PartnerFor(envelope.from);
            partner.LastMessage = Time.unscaledTime;
            if (envelope.world.Length > 0 && envelope.world != CurrentWorldKey())
            {
                if (envelope.type == "drop") Send("granted", envelope.body, partner.Id);
                return;
            }
            Handle(partner, envelope);
        }
        catch (Exception e)
        {
            networkRejected++;
            Logger.LogWarning("PEER_MESSAGE_REJECTED " + envelope.type + " from " + envelope.from + ": " + e.Message);
        }
    }

    private void TakeHostControl(Envelope envelope)
    {
        var id = ParseInt(envelope.body);
        if (envelope.type == "left")
        {
            if (partners.TryGetValue(id, out var left)) DropPartner(left, " left.");
            return;
        }
        if (worldOwner == myId) worldOwner = id;
        myId = id;
        sentProfileKey = "";
        sentQuests = "";
    }

    private void Handle(Partner partner, Envelope envelope)
    {
        if ((OwnerMessages.Contains(envelope.type) && partner.Id != worldOwner) || (HostMessages.Contains(envelope.type) && partner.Id != HostId)) return;
        switch (envelope.type)
        {
            case "frame": ReceiveFrame(partner, JsonUtility.FromJson<PeerFrame>(envelope.body)); break;
            case "profile": ReceiveProfile(partner, envelope.body); break;
            case "world-request": SendWorldTo(partner, envelope.body); break;
            case "world": ReceiveWorld(partner, envelope.body); break;
            case "monsters": ReceiveMonsters(envelope.body); break;
            case "hit": ApplyHit(partner, envelope.body); break;
            case "strike": TakeStrike(envelope.body); break;
            case "hurt": TakeHurt(envelope.body); break;
            case "effect": TakeEffect(partner, envelope.body); break;
            case "kill": TakeKill(envelope.body); break;
            case "pop": ShowDamagePopup(envelope.body); break;
            case "ground": ReceiveGround(envelope.body); break;
            case "ground-gone": ReceiveGroundGone(envelope.body); break;
            case "ground-list": CheckGroundList(envelope.body); break;
            case "ground-missing": ResendGround(envelope.body); break;
            case "take": GiveTake(partner, envelope.body); break;
            case "granted": ReceiveGranted(envelope.body); break;
            case "drop": ReceiveDrop(envelope.body); break;
            case "use-fixture": UseFixture(partner, envelope.body); break;
            case "key-used": SpendChestKey(); break;
            case "fixture": ApplyFixture(envelope.body); break;
            case "quests": ReceiveQuests(envelope.body); break;
            case "quest-progress": MergeProgress(envelope.body); break;
            case "quest-accept": TakeQuestAccept(envelope.body); break;
            case "quest-cancel": TakeQuestCancel(envelope.body); break;
            case "quest-claim": TakeQuestClaim(envelope.body); break;
            case "quest-reward": TakeQuestReward(envelope.body); break;
            case "use-ward": UseWard(envelope.body); break;
            case "revive": if (downed) StandUp(partner); break;
            case "chat": ReceiveChat(partner, envelope.body); break;
            case "ping": Send("pong", envelope.body, partner.Id, reliable: false); break;
            case "pong": TakePong(partner, envelope.body); break;
            case "reset-dungeons": TakeDungeonReset(partner); break;
            default: throw new InvalidOperationException("Unknown peer message: " + envelope.type);
        }
    }

    private void Welcome(int peer)
    {
        Transmit(peer, new Envelope { version = Compatibility, type = "welcome", body = peer.ToString(), from = HostId, to = peer }, true);
        PartnerFor(peer);
        sentProfileKey = "";
        sentQuests = "";
        Fate.SetPause(false);
    }

    private Partner PartnerFor(int id)
    {
        if (partners.TryGetValue(id, out var partner)) return partner;
        partner = new Partner(id) { LastMessage = Time.unscaledTime };
        partners[id] = partner;
        sentProfileKey = "";
        return partner;
    }

    private void ReceiveFrame(Partner partner, PeerFrame frame)
    {
        if (frame == null) throw new InvalidOperationException("Empty peer frame.");
        if (partner.Frame != null && frame.seq <= partner.Frame.seq) return;
        partner.Frame = frame;
        partner.SyncClock(frame.time);
        ShowPartnerPortal(partner);
        if (partner.InWorld(LocalFrameWorld()))
        {
            ApplyPeerFrame(partner, frame);
            networkAccepted++;
        }
        else if (partner.Actor != null && !transitioning) Despawn(partner);
    }

    private void DropPartner(Partner partner, string reason)
    {
        if (!partners.Remove(partner.Id)) return;
        lastPlaces.Remove(partner.Id);
        foreach (var relay in awayRelays.Keys.Where(relay => relay.from == partner.Id || relay.to == partner.Id).ToList()) awayRelays.Remove(relay);
        Despawn(partner);
        if (partner.Portal != null) UnityEngine.Object.Destroy(partner.Portal);
        if (partner.TownPortal != null) UnityEngine.Object.Destroy(partner.TownPortal);
        partner.DestroyHud();
        if (isHost) Send("left", partner.Id.ToString());
        if (partner.Name.Length > 0 && reason.Length > 0) Notify(partner.Name + reason);
        if (Connected || OwnsWorld) return;
        ResetGround();
        TakeOwnership();
    }

    private void EndSession(string reason)
    {
        if (link == null) return;
        if (downed) DieForReal();
        foreach (var partner in partners.Values.ToList()) DropPartner(partner, "");
        link.Dispose();
        link = null;
        isHost = true;
        myId = worldOwner = HostId;
        pendingWorld = joiningWorld = null;
        awayRelays.Clear();
        RestoreOwnQuests();
        ResetTravel();
        if (reason.Length > 0) Notify(reason);
    }
}
