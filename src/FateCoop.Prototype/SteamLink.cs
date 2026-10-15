using System;
using System.Runtime.InteropServices;
using Steamworks;

namespace FateCoop.Prototype;

internal sealed class SteamLink : IPeerLink
{
    private const string VersionKey = "fatecoop";
    private const int Channel = 0;
    private const int SendFlags = Constants.k_nSteamNetworkingSend_Reliable | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession;
    private readonly bool host;
    private readonly Action<string> report;
    private readonly CallResult<LobbyCreated_t> created;
    private readonly CallResult<LobbyEnter_t> entered;
    private readonly Callback<LobbyChatUpdate_t> membersChanged;
    private readonly Callback<SteamNetworkingMessagesSessionRequest_t> sessionRequested;
    private readonly IntPtr[] incoming = new IntPtr[1];
    private CSteamID lobby = CSteamID.Nil, peer = CSteamID.Nil;
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

    internal static SteamLink Host(Action<string> report)
    {
        var link = new SteamLink(true, report);
        link.created.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2));
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
        report("Steam lobby ready. Invite a friend with Shift+Tab or the Invite button.");
    }

    private void OnLobbyEntered(LobbyEnter_t result, bool ioFailure)
    {
        if (host) return;
        lobby = new CSteamID(result.m_ulSteamIDLobby);
        if (ioFailure || result.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) { failure = "Could not join the Steam lobby: " + (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse; return; }
        var version = SteamMatchmaking.GetLobbyData(lobby, VersionKey);
        if (version != Plugin.Compatibility) { failure = "The host runs " + version + ", this game runs " + Plugin.Compatibility + "."; return; }
        peer = SteamMatchmaking.GetLobbyOwner(lobby);
        report("Joined " + SteamFriends.GetFriendPersonaName(peer) + "'s lobby.");
    }

    private void OnMembersChanged(LobbyChatUpdate_t change)
    {
        if (change.m_ulSteamIDLobby != lobby.m_SteamID) return;
        var user = new CSteamID(change.m_ulSteamIDUserChanged);
        if ((change.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
        {
            if (host && !peer.IsValid()) { peer = user; report(SteamFriends.GetFriendPersonaName(user) + " joined the lobby."); }
            return;
        }
        if (user != peer) return;
        CloseSession();
        peer = CSteamID.Nil;
        if (host) report("Your friend left the lobby. Invite them again to continue.");
        else failure = "The host closed the lobby.";
    }

    private void OnSessionRequested(SteamNetworkingMessagesSessionRequest_t request)
    {
        var remote = request.m_identityRemote;
        if (peer.IsValid() && remote.GetSteamID() == peer) SteamNetworkingMessages.AcceptSessionWithUser(ref remote);
    }

    public void Send(byte[] message)
    {
        if (!peer.IsValid()) return;
        var identity = Identity(peer);
        var data = Marshal.AllocHGlobal(message.Length);
        try
        {
            Marshal.Copy(message, 0, data, message.Length);
            SteamNetworkingMessages.SendMessageToUser(ref identity, data, (uint)message.Length, SendFlags, Channel);
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    public bool TryReceive(out byte[] message)
    {
        message = Array.Empty<byte>();
        while (SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, incoming, 1) == 1)
        {
            var received = SteamNetworkingMessage_t.FromIntPtr(incoming[0]);
            try
            {
                if (received.m_identityPeer.GetSteamID() != peer || received.m_cbSize <= 0 || received.m_cbSize > Plugin.MaxMessageBytes) continue;
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

    private void CloseSession()
    {
        if (!peer.IsValid()) return;
        var identity = Identity(peer);
        SteamNetworkingMessages.CloseSessionWithUser(ref identity);
    }

    public void Dispose()
    {
        CloseSession();
        if (lobby.IsValid()) SteamMatchmaking.LeaveLobby(lobby);
        if (host) SteamFriends.ClearRichPresence();
        created.Dispose();
        entered.Dispose();
        membersChanged.Dispose();
        sessionRequested.Dispose();
    }
}
