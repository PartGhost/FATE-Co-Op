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
    void Send(byte[] message, bool reliable);
    bool TryReceive(out byte[] message);
}

internal sealed class LocalLink : IPeerLink
{
    private readonly TcpListener? listener;
    private readonly int port;
    private readonly Thread worker;
    private readonly ConcurrentQueue<byte[]> inbox = new ConcurrentQueue<byte[]>();
    private volatile TcpClient? client;
    private volatile bool disposed;

    private LocalLink(TcpListener? listener, int port)
    {
        this.listener = listener;
        this.port = port;
        worker = new Thread(Run) { IsBackground = true, Name = "FateCoop local link" };
        worker.Start();
    }

    internal static LocalLink Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        return new LocalLink(listener, 0);
    }

    internal static LocalLink Connect(int port) => new LocalLink(null, port);

    internal int Port => listener != null ? ((IPEndPoint)listener.LocalEndpoint).Port : port;
    public string CloseReason => listener == null && !worker.IsAlive ? "The host closed the connection." : "";

    private void Run()
    {
        do
        {
            try
            {
                var accepted = listener != null ? listener.AcceptTcpClient() : new TcpClient(IPAddress.Loopback.ToString(), port);
                accepted.NoDelay = true;
                client = accepted;
                using var stream = accepted.GetStream();
                var header = new byte[4];
                while (!disposed)
                {
                    ReadExactly(stream, header);
                    var length = BitConverter.ToInt32(header, 0);
                    if (length <= 0 || length > Plugin.MaxMessageBytes) throw new IOException("Peer message size is invalid.");
                    var message = new byte[length];
                    ReadExactly(stream, message);
                    inbox.Enqueue(message);
                }
            }
            catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException || e is InvalidOperationException) { }
            finally
            {
                client?.Close();
                client = null;
            }
        } while (listener != null && !disposed);
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

    public void Send(byte[] message, bool reliable)
    {
        var current = client;
        if (current == null) return;
        try
        {
            var stream = current.GetStream();
            stream.Write(BitConverter.GetBytes(message.Length), 0, 4);
            stream.Write(message, 0, message.Length);
        }
        catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException || e is InvalidOperationException) { current.Close(); }
    }

    public bool TryReceive(out byte[] message) => inbox.TryDequeue(out message);

    public void Dispose()
    {
        disposed = true;
        listener?.Stop();
        client?.Close();
        worker.Join(500);
    }
}
