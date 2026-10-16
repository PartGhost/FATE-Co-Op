using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FateCoop.Prototype;

internal interface IPeerLink : IDisposable
{
    string CloseReason { get; }
    void Send(int peer, byte[] message, bool reliable);
    bool TryReceive(out int peer, out byte[] message);
}

internal sealed class LocalLink : IPeerLink
{
    private readonly TcpListener? listener;
    private readonly int port;
    private readonly ConcurrentQueue<(int peer, byte[] message)> inbox = new ConcurrentQueue<(int, byte[])>();
    private readonly ConcurrentDictionary<int, TcpClient> clients = new ConcurrentDictionary<int, TcpClient>();
    private volatile bool disposed, hostLost;
    private int nextPeer = Plugin.HostId + 1;

    private LocalLink(TcpListener? listener, int port)
    {
        this.listener = listener;
        this.port = port;
        new Thread(listener != null ? Accept : Connect) { IsBackground = true, Name = "FateCoop local link" }.Start();
    }

    internal static LocalLink Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LocalLink(listener, 0);
    }

    internal static LocalLink Connect(int port) => new LocalLink(null, port);

    internal int Port => listener != null ? ((IPEndPoint)listener.LocalEndpoint).Port : port;
    public string CloseReason => hostLost ? "The host closed the connection." : "";

    private void Accept()
    {
        try
        {
            while (!disposed)
            {
                var client = listener!.AcceptTcpClient();
                var peer = nextPeer++;
                new Thread(() => Read(peer, client)) { IsBackground = true, Name = "FateCoop local peer " + peer }.Start();
            }
        }
        catch (Exception e) when (e is SocketException || e is ObjectDisposedException || e is InvalidOperationException) { }
    }

    private void Connect()
    {
        try { Read(Plugin.HostId, new TcpClient(IPAddress.Loopback.ToString(), port)); }
        catch (SocketException) { }
        hostLost = !disposed;
    }

    private void Read(int peer, TcpClient client)
    {
        client.NoDelay = true;
        clients[peer] = client;
        try
        {
            using var stream = client.GetStream();
            var header = new byte[4];
            while (!disposed)
            {
                ReadExactly(stream, header);
                var length = BitConverter.ToInt32(header, 0);
                if (length <= 0 || length > Plugin.MaxMessageBytes) throw new IOException("Peer message size is invalid.");
                var message = new byte[length];
                ReadExactly(stream, message);
                inbox.Enqueue((peer, message));
            }
        }
        catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException || e is InvalidOperationException) { }
        finally
        {
            clients.TryRemove(peer, out _);
            client.Close();
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        for (var read = 0; read < buffer.Length;)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0) throw new IOException("Peer closed the connection.");
            read += count;
        }
    }

    public void Send(int peer, byte[] message, bool reliable)
    {
        if (!clients.TryGetValue(peer, out var client)) return;
        try
        {
            var stream = client.GetStream();
            stream.Write(BitConverter.GetBytes(message.Length), 0, 4);
            stream.Write(message, 0, message.Length);
        }
        catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException || e is InvalidOperationException) { client.Close(); }
    }

    public bool TryReceive(out int peer, out byte[] message)
    {
        var received = inbox.TryDequeue(out var entry);
        (peer, message) = entry;
        return received;
    }

    public void Dispose()
    {
        disposed = true;
        listener?.Stop();
        foreach (var client in clients.Values) client.Close();
    }
}
