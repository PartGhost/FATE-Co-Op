using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

// Host A owns floor selection. The local bridge transfers verified snapshots to B.
internal static class PairRelay
{
    private sealed class Link : IDisposable
    {
        private readonly TcpClient client;
        private readonly StreamReader reader;
        private readonly StreamWriter writer;
        private readonly string token;
        private readonly string runtime;
        private long sequence;
        internal JsonElement State;
        internal Link(string endpointPath)
        {
            using var endpoint = JsonDocument.Parse(File.ReadAllText(endpointPath));
            runtime = Path.GetDirectoryName(endpointPath)!;
            token = endpoint.RootElement.GetProperty("token").GetString()!;
            client = new TcpClient { NoDelay = true, ReceiveTimeout = 15000, SendTimeout = 5000 };
            client.Connect(IPAddress.Loopback, endpoint.RootElement.GetProperty("port").GetInt32());
            reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 1024, true);
            writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            Send("status");
            if (!State.TryGetProperty("petJson", out _) || !State.TryGetProperty("combatJson", out _))
                throw new IOException("Both games must run the compatible 0.6 entity prototype.");
        }
        internal bool Ready => State.GetProperty("worldReady").GetBoolean();
        internal string WorldKey => State.GetProperty("worldKey").GetString() ?? "";
        internal void Send(string operation)
        {
            var epoch = State.ValueKind == JsonValueKind.Object ? State.GetProperty("epoch").GetInt32() : 0;
            writer.WriteLine($"v1 {token} {++sequence} {epoch} {operation}");
            using var doc = JsonDocument.Parse(reader.ReadLine() ?? throw new IOException("Game disconnected."));
            State = doc.RootElement.Clone();
            if (!State.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(State.GetProperty("error").GetString());
        }
        internal void Mirror(JsonElement source)
        {
            var bytes = Encoding.UTF8.GetBytes(source.GetRawText());
            if (bytes.Length > 65536) throw new IOException("Peer entity frame exceeds its size limit.");
            var path = Path.Combine(runtime, "peer-incoming.json");
            File.WriteAllBytes(path + ".tmp", bytes); File.Move(path + ".tmp", path, true);
            Send("peer-frame " + Convert.ToHexString(SHA256.HashData(bytes)));
        }
        public void Dispose() { writer.Dispose(); reader.Dispose(); client.Dispose(); }
    }
    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static double Distance(JsonElement a, JsonElement b) => Math.Sqrt(new[] { "x", "z" }.Sum(k => Math.Pow(a.GetProperty(k).GetDouble() - b.GetProperty(k).GetDouble(), 2)));
    private sealed class LocalMonitor
    {
        private int epoch = -1, primaryId;
        private JsonElement origin;
        internal double Travel;
        internal int UnexpectedChanges;
        internal void Observe(Link link)
        {
            if (!link.Ready) return;
            var nextEpoch = link.State.GetProperty("epoch").GetInt32();
            var nextId = link.State.GetProperty("primaryId").GetInt32();
            var position = link.State.GetProperty("primaryPosition");
            if (nextEpoch != epoch) { epoch = nextEpoch; primaryId = nextId; origin = position; }
            else if (nextId != primaryId) { UnexpectedChanges++; primaryId = nextId; }
            Travel = Math.Max(Travel, Distance(origin, position));
        }
    }
    internal static void Run(string workspace, string[] args)
    {
        using var relayLock = new FileStream(Path.Combine(workspace, "artifacts", "pair-relay.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var stopPath = Path.Combine(workspace, "artifacts", "pair-relay-stop");
        if (File.Exists(stopPath)) File.Delete(stopPath);
        var secondsIndex = Array.IndexOf(args, "--seconds");
        var seconds = secondsIndex >= 0 ? int.Parse(args[secondsIndex + 1], CultureInfo.InvariantCulture) : 0;
        using var a = new Link(Path.Combine(workspace, "artifacts", "runtime", "endpoint.json"));
        using var b = new Link(Path.Combine(workspace, "artifacts", "runtime-b", "endpoint.json"));
        a.Send("session-on"); b.Send("session-on");
        var running = true;
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; running = false; };
        Console.CancelKeyPress += cancel;
        var start = DateTime.UtcNow;
        var nextReport = start;
        var monitorA = new LocalMonitor(); var monitorB = new LocalMonitor();
        monitorA.Observe(a); monitorB.Observe(b);
        var samples = 0;
        var rejected = 0;
        var transitionRetries = 0;
        var joinedFloors = 0;
        var requestedWorld = "";
        double errorA = 0, errorB = 0;
        Console.WriteLine("Two-game relay connected. A hosts; B follows A's Grove town and dungeon floor. Movement test only. Ctrl+C stops.");
        try
        {
            while (running && !File.Exists(stopPath) && (seconds <= 0 || (DateTime.UtcNow - start).TotalSeconds < seconds))
            {
                try
                {
                    if (a.Ready && b.Ready && a.WorldKey == b.WorldKey)
                    {
                        var sourceA = a.State;
                        var sourceB = b.State;
                        a.Mirror(sourceB);
                        b.Mirror(sourceA);
                        samples++;
                        errorA = Distance(a.State.GetProperty("guestPosition"), b.State.GetProperty("primaryPosition"));
                        errorB = Distance(b.State.GetProperty("guestPosition"), a.State.GetProperty("primaryPosition"));
                        File.AppendAllText(Path.Combine(workspace, "artifacts", "pair-telemetry.jsonl"), JsonSerializer.Serialize(new { time = DateTime.UtcNow, a = a.State, b = b.State, errorA, errorB }) + Environment.NewLine);
                    }
                    else
                    {
                        // Guests must not appear in unrelated or independently generated worlds.
                        if (a.State.GetProperty("guestId").GetInt32() != 0 && !a.State.GetProperty("transitioning").GetBoolean()) a.Send("despawn");
                        if (b.State.GetProperty("guestId").GetInt32() != 0 && !b.State.GetProperty("transitioning").GetBoolean()) b.Send("despawn");
                        a.Send("status"); b.Send("status");
                        if (a.Ready && !b.State.GetProperty("transitioning").GetBoolean() && b.State.GetProperty("primaryId").GetInt32() != 0)
                        {
                            if (a.WorldKey == "GroveTown" && b.State.GetProperty("scene").GetString() == "Scene_Dungeon")
                            { b.Send("join-town"); requestedWorld = ""; }
                            else if (a.State.GetProperty("scene").GetString() == "Scene_Dungeon" && a.WorldKey != b.WorldKey)
                            {
                                if (requestedWorld == a.WorldKey) throw new IOException("Host floor loaded but its layout did not verify; stopping replication.");
                                a.Send("export-world");
                                var bytes = File.ReadAllBytes(Path.Combine(workspace, "artifacts", "runtime", "world-export.json"));
                                if (bytes.Length > 8 * 1024 * 1024) throw new IOException("Host world snapshot exceeds prototype size limit.");
                                var incoming = Path.Combine(workspace, "artifacts", "runtime-b", "world-incoming.json");
                                File.WriteAllBytes(incoming + ".tmp", bytes);
                                File.Move(incoming + ".tmp", incoming, true);
                                b.Send("join-world " + Convert.ToHexString(SHA256.HashData(bytes)));
                                requestedWorld = a.WorldKey;
                                joinedFloors++;
                                Console.WriteLine("Joining host dungeon floor " + a.State.GetProperty("depth").GetInt32());
                            }
                        }
                    }
                }
                catch (InvalidOperationException e)
                {
                    if (e.Message.StartsWith("Stale world epoch", StringComparison.Ordinal) || e.Message.StartsWith("World is transitioning", StringComparison.Ordinal)) transitionRetries++;
                    else rejected++;
                    a.Send("status"); b.Send("status");
                    if (transitionRetries + rejected < 5) Console.WriteLine("Waiting: " + e.Message);
                }
                monitorA.Observe(a); monitorB.Observe(b);
                if (DateTime.UtcNow >= nextReport)
                {
                    nextReport = DateTime.UtcNow.AddSeconds(1);
                    var report = new { transport = "TCP loopback, two game processes", samples, rejected, transitionRetries, joinedFloors, worldReadyA = a.Ready, worldReadyB = b.Ready, worldKeyA = a.WorldKey, worldKeyB = b.WorldKey, depthA = a.State.GetProperty("depth").GetInt32(), depthB = b.State.GetProperty("depth").GetInt32(), layoutA = a.State.GetProperty("layoutHash").GetString(), layoutB = b.State.GetProperty("layoutHash").GetString(), primaryTravelA = monitorA.Travel, primaryTravelB = monitorB.Travel, guestPositionErrorA = errorA, guestPositionErrorB = errorB, primaryIdentityPreservedA = monitorA.UnexpectedChanges == 0, primaryIdentityPreservedB = monitorB.UnexpectedChanges == 0 };
                    File.WriteAllText(Path.Combine(workspace, "artifacts", "pair-result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                }
                Thread.Sleep(25);
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            foreach (var link in new[] { a, b })
                try { link.Send("status"); if (!link.State.GetProperty("transitioning").GetBoolean()) link.Send("session-off"); } catch (Exception) { }
            Console.WriteLine("Relay stopped; remote guests removed where reachable.");
        }
    }
}
