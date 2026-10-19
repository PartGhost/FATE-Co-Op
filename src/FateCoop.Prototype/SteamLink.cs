using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;

namespace FateCoop.Prototype;

internal sealed class SteamLink : IPeerLink
{
    private const string VersionKey = "fatecoop";
    private const int Channel = 0;
    private const int ReliableFlags = Constants.k_nSteamNetworkingSend_Reliable | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
    private const int UnreliableFlags = Constants.k_nSteamNetworkingSend_UnreliableNoNagle | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
    private readonly bool host;
    private readonly Action<string> report;
    private readonly CallResult<LobbyCreated_t> created;
    private readonly CallResult<LobbyEnter_t> entered;
    private readonly Callback<LobbyChatUpdate_t> membersChanged;
    private readonly Callback<SteamNetworkingMessagesSessionRequest_t> sessionRequested;
    private readonly IntPtr[] incoming = new IntPtr[1];
    private readonly Dictionary<int, CSteamID> users = new Dictionary<int, CSteamID>();
    private readonly Dictionary<CSteamID, int> ids = new Dictionary<CSteamID, int>();
    private CSteamID lobby = CSteamID.Nil;
    private int nextPeer = Plugin.HostId + 1;
    private string failure = "";

    private SteamLink(bool host, Action<string> report)
    {
        this.host = host;
        this.report = report;
        created = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
        entered = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
        membersChanged = Callback<LobbyChatUpdate_t>.Create(OnMembersChanged);
        sessionRequested = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequested);
    }

    internal static SteamLink Host(int players, Action<string> report)
    {
        var link = new SteamLink(true, report);
        link.created.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, players));
        report("Creating Steam lobby...");
        return link;
    }

    internal static SteamLink Join(CSteamID lobby, Action<string> report)
    {
        var link = new SteamLink(false, report);
        link.entered.Set(SteamMatchmaking.JoinLobby(lobby));
        report("Joining Steam lobby...");
        return link;
    }

    public string CloseReason => failure;
    internal bool CanInvite => host && lobby.IsValid();

    internal void Invite() => SteamFriends.ActivateGameOverlayInviteDialog(lobby);

    private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
    {
        if (ioFailure || result.m_eResult != EResult.k_EResultOK) { failure = "Steam lobby creation failed: " + result.m_eResult; return; }
        lobby = new CSteamID(result.m_ulSteamIDLobby);
        SteamMatchmaking.SetLobbyData(lobby, VersionKey, Plugin.Compatibility);
        SteamFriends.SetRichPresence("connect", "+connect_lobby " + lobby.m_SteamID);
        report("Steam lobby ready. Invite friends with Shift+Tab or Invite friend in the Escape menu.");
    }

    private void OnLobbyEntered(LobbyEnter_t result, bool ioFailure)
    {
        if (host) return;
        lobby = new CSteamID(result.m_ulSteamIDLobby);
        if (ioFailure || result.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) { failure = "Could not join the Steam lobby: " + (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse; return; }
        var version = SteamMatchmaking.GetLobbyData(lobby, VersionKey);
        if (version != Plugin.Compatibility) { failure = "The host runs " + version + ", this game runs " + Plugin.Compatibility + "."; return; }
        Add(Plugin.HostId, SteamMatchmaking.GetLobbyOwner(lobby));
        report("Joined " + SteamFriends.GetFriendPersonaName(users[Plugin.HostId]) + "'s lobby.");
    }

    private void OnMembersChanged(LobbyChatUpdate_t change)
    {
        if (change.m_ulSteamIDLobby != lobby.m_SteamID) return;
        var user = new CSteamID(change.m_ulSteamIDUserChanged);
        if ((change.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
        {
            if (host && !ids.ContainsKey(user)) Add(nextPeer++, user);
            return;
        }
        if (!ids.TryGetValue(user, out var peer)) return;
        CloseSession(user);
        users.Remove(peer);
        ids.Remove(user);
        if (!host) failure = "The host closed the lobby.";
    }

    private void Add(int peer, CSteamID user)
    {
        users[peer] = user;
        ids[user] = peer;
    }

    private void OnSessionRequested(SteamNetworkingMessagesSessionRequest_t request)
    {
        var remote = request.m_identityRemote;
        var user = remote.GetSteamID();
        if (ids.ContainsKey(user) || (lobby.IsValid() && InLobby(user))) SteamNetworkingMessages.AcceptSessionWithUser(ref remote);
    }

    private bool InLobby(CSteamID user)
    {
        for (var i = 0; i < SteamMatchmaking.GetNumLobbyMembers(lobby); i++)
            if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i) == user) return true;
        return false;
    }

    public void Send(int peer, byte[] message, bool reliable)
    {
        if (!users.TryGetValue(peer, out var user)) return;
        var identity = Identity(user);
        var data = Marshal.AllocHGlobal(message.Length);
        try
        {
            Marshal.Copy(message, 0, data, message.Length);
            SteamNetworkingMessages.SendMessageToUser(ref identity, data, (uint)message.Length, reliable ? ReliableFlags : UnreliableFlags, Channel);
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    public bool TryReceive(out int peer, out byte[] message)
    {
        peer = 0;
        message = Array.Empty<byte>();
        while (SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, incoming, 1) == 1)
        {
            var received = SteamNetworkingMessage_t.FromIntPtr(incoming[0]);
            try
            {
                if (!ids.TryGetValue(received.m_identityPeer.GetSteamID(), out peer) || received.m_cbSize <= 0 || received.m_cbSize > Plugin.MaxMessageBytes) continue;
                message = new byte[received.m_cbSize];
                Marshal.Copy(received.m_pData, message, 0, received.m_cbSize);
                return true;
            }
            finally { SteamNetworkingMessage_t.Release(incoming[0]); }
        }
        return false;
    }

    private static SteamNetworkingIdentity Identity(CSteamID user)
    {
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID(user);
        return identity;
    }

    private static void CloseSession(CSteamID user)
    {
        var identity = Identity(user);
        SteamNetworkingMessages.CloseSessionWithUser(ref identity);
    }

    public void Dispose()
    {
        foreach (var user in users.Values) CloseSession(user);
        if (lobby.IsValid()) SteamMatchmaking.LeaveLobby(lobby);
        if (host) SteamFriends.ClearRichPresence();
        created.Dispose();
        entered.Dispose();
        membersChanged.Dispose();
        sessionRequested.Dispose();
    }
}
