using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using FateCoop.Transport;

namespace FateCoop.Prototype;

// This thread only handles bounded wire messages. Unity operations run in Plugin.Update.
internal sealed class LoopbackServer : IDisposable
{
    internal sealed class Request
    {
        internal WireCommand Command = null!;
        internal string Reply = "";
        internal readonly ManualResetEventSlim Done = new ManualResetEventSlim();
        internal volatile bool Cancelled;
    }
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly Thread worker;
    internal readonly ConcurrentQueue<Request> Requests = new ConcurrentQueue<Request>();
    internal readonly string Token = Guid.NewGuid().ToString("N");
    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    private volatile bool running = true;
    private volatile TcpClient? current;
    internal volatile bool Connected;
    private int disconnects;
    internal bool ConsumeDisconnect() => Interlocked.Exchange(ref disconnects, 0) != 0;

    internal LoopbackServer()
    {
        listener.Start(1);
        worker = new Thread(Run) { IsBackground = true, Name = "FateCoop loopback" };
        worker.Start();
    }

    private void Run()
    {
        while (running)
        {
            try
            {
                using var client = listener.AcceptTcpClient();
                current = client;
                client.NoDelay = true;
                client.ReceiveTimeout = 0; // Idle local clients may remain connected; Dispose closes the stream.
                client.SendTimeout = 5000;
                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                long lastSequence = 0;
                Connected = true;
                while (running)
                {
                    var line = ReadBoundedLine(stream);
                    if (line == null) break;
                    if (!WireCommand.TryParse(line, Token, out var command) || command.Sequence <= lastSequence)
                    {
                        writer.WriteLine("{\"ok\":false,\"error\":\"Invalid protocol, token, sequence or coordinates\"}");
                        continue;
                    }
                    lastSequence = command.Sequence;
                    var request = new Request { Command = command };
                    Requests.Enqueue(request); // One outstanding request per connection.
                    if (!request.Done.Wait(10000))
                    {
                        request.Cancelled = true;
                        writer.WriteLine("{\"ok\":false,\"error\":\"Game thread timeout\"}");
                        break;
                    }
                    writer.WriteLine(request.Reply);
                    request.Done.Dispose();
                }
            }
            catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException) { }
            finally
            {
                Connected = false;
                current = null;
                Interlocked.Increment(ref disconnects);
            }
        }
    }

    private static string? ReadBoundedLine(Stream stream)
    {
        var bytes = new byte[256];
        var count = 0;
        while (true)
        {
            var value = stream.ReadByte();
            if (value == -1) return null;
            if (value == 10) return Encoding.UTF8.GetString(bytes, 0, count).TrimEnd('\r');
            if (count == bytes.Length) throw new IOException("Frame too long.");
            bytes[count++] = (byte)value;
        }
    }

    public void Dispose()
    {
        running = false;
        listener.Stop();
        current?.Close();
        while (Requests.TryDequeue(out var request)) { request.Cancelled = true; request.Done.Set(); }
        worker.Join(500);
    }
}
