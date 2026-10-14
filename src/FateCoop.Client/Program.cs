using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FateCoop.Transport;

if (args.Contains("--protocol-test"))
{
    var valid = new[] { "v1 t 1 0 status", "v1 t 2 3 spawn", "v1 t 3 3 move -12.5 2.4", "v1 t 4 3 stop", "v1 t 5 3 despawn", "v1 t 6 3 mirror -12.5 2.4 270", "v1 t 7 3 join-world " + new string('A',64), "v1 t 8 3 export-world", "v1 t 9 3 join-town", "v1 t 10 3 session-on", "v1 t 11 3 session-off" };
    var invalid = new[] { "v2 t 1 0 status", "v1 wrong 1 0 spawn", "v1 t 0 0 status", "v1 t 1 -1 status", "v1 t 1 0 move NaN 2", "v1 t 1 0 move Infinity 2", "v1 t 1 0 move 1", "v1 t 1 0 quit", "v1 t 1 0 status extra", "v1 t 1 0 mirror 1 2 NaN", "v1 t 1 0 mirror 1 2", new string('x', 257) };
    valid = valid.Append("v1 t 12 3 peer-frame " + new string('B',64)).ToArray();
    invalid = invalid.Concat(new[] { "v1 t 1 0 peer-frame abc", "v1 t 1 0 peer-frame " + new string('G',64), "v1 t 1 0 peer-frame " + new string('A',64) + " extra" }).ToArray();
    foreach (var line in valid) if (!WireCommand.TryParse(line, "t", out _)) throw new Exception("Valid message rejected.");
    foreach (var line in invalid) if (WireCommand.TryParse(line, "t", out _)) throw new Exception("Invalid message accepted.");
    foreach (var line in new[] { "v1 t 12 3 join-world abc", "v1 t 13 3 join-world " + new string('G',64), "v1 t 14 3 export-world extra" })
        if (WireCommand.TryParse(line, "t", out _)) throw new Exception("Invalid world command accepted.");
    Console.WriteLine($"Protocol checks passed: {valid.Length} valid and {invalid.Length + 3} invalid frames.");
    return;
}
var workspaceArg = Array.IndexOf(args, "--workspace");
var workspace = workspaceArg >= 0 ? Path.GetFullPath(args[workspaceArg + 1]) : Directory.GetCurrentDirectory();
if (args.Contains("--pair")) { PairRelay.Run(workspace, args); return; }
var endpointPath = Path.Combine(workspace, "artifacts", "runtime", "endpoint.json");
using var endpoint = JsonDocument.Parse(File.ReadAllText(endpointPath));
var port = endpoint.RootElement.GetProperty("port").GetInt32();
var token = endpoint.RootElement.GetProperty("token").GetString()!;
using var client = new TcpClient { NoDelay = true, ReceiveTimeout = 15000, SendTimeout = 5000 };
client.Connect(IPAddress.Loopback, port);
using var stream = client.GetStream();
using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
long sequence = 0;
var epoch = 0;

JsonElement Raw(string line)
{
    writer.WriteLine(line);
    var json = reader.ReadLine() ?? throw new IOException("Game disconnected.");
    using var doc = JsonDocument.Parse(json);
    var result = doc.RootElement.Clone();
    if (result.TryGetProperty("epoch", out var value)) epoch = value.GetInt32();
    return result;
}
JsonElement Send(string operation, int? explicitEpoch = null)
{
    var reply = Raw($"v1 {token} {++sequence} {explicitEpoch ?? epoch} {operation}");
    if (!reply.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(reply.GetProperty("error").GetString());
    return reply;
}
static double Distance(JsonElement a, JsonElement b) => Math.Sqrt(new[] { "x", "y", "z" }.Sum(k => Math.Pow(a.GetProperty(k).GetDouble() - b.GetProperty(k).GetDouble(), 2)));
static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

var initial = Send("status");
Console.WriteLine("Connected to Grove prototype on localhost. " + initial.GetRawText());
if (args.Contains("--probe"))
{
    void Reject(string frame)
    {
        if (Raw(frame).GetProperty("ok").GetBoolean()) throw new Exception("Unsafe/invalid frame accepted.");
    }
    Reject($"v2 {token} {++sequence} {epoch} spawn");
    Reject($"v1 invalid {++sequence} {epoch} spawn");
    Reject($"v1 {token} {++sequence} {epoch} move NaN 0");
    Reject($"v1 {token} {sequence - 3} {epoch} status");
    Reject($"v1 {token} {++sequence} {epoch + 1} spawn");
    Send("status");
    Send("spawn");
    var before = Send("status");
    Reject($"v1 {token} {++sequence} {epoch} move 100000 100000");
    var after = Send("status");
    if (before.GetProperty("primaryId").GetInt32() != after.GetProperty("primaryId").GetInt32()) throw new Exception("Primary identity changed during probe.");
    Console.WriteLine("Live protocol rejection checks passed; game remained responsive.");
    return;
}
if (args.Contains("--demo"))
{
    Send("spawn");
    var before = Send("status");
    var guestStart = before.GetProperty("guestPosition");
    var hostStart = before.GetProperty("primaryPosition");
    var hostId = before.GetProperty("primaryId").GetInt32();
    var x = guestStart.GetProperty("x").GetDouble();
    var z = guestStart.GetProperty("z").GetDouble();
    var peakGuest = 0.0;
    var peakHost = 0.0;
    // Try nearby reachable destinations; reject blocked geometry without bypassing navigation.
    var moved = false;
    foreach (var offset in new[] { (5.0, 0.0), (0.0, 5.0), (-5.0, 0.0), (0.0, -5.0) })
    {
        try { Send($"move {Format(x + offset.Item1)} {Format(z + offset.Item2)}"); moved = true; break; }
        catch (InvalidOperationException) { }
    }
    if (!moved) throw new Exception("No nearby reachable destination.");
    for (var i = 0; i < 32; i++)
    {
        Thread.Sleep(250);
        var current = Send("status");
        if (current.GetProperty("primaryId").GetInt32() != hostId) throw new Exception("Primary identity changed.");
        peakGuest = Math.Max(peakGuest, Distance(guestStart, current.GetProperty("guestPosition")));
        peakHost = Math.Max(peakHost, Distance(hostStart, current.GetProperty("primaryPosition")));
    }
    Send("stop");
    var result = new { guestTravel = peakGuest, primaryTravel = peakHost, primaryIdentityPreserved = true, epoch, transport = "TCP loopback", movementVerified = peakGuest > 2.0, stationaryPrimaryVerified = peakHost < 0.05 };
    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(workspace, "artifacts", "runtime", "network-result.json"), json);
    Console.WriteLine(json);
    if (peakGuest <= 2.0) throw new Exception("Remote movement verification failed.");
    return;
}
if (args.Contains("--disconnect-test"))
{
    Send("spawn");
    var status = Send("status");
    var pos = status.GetProperty("guestPosition");
    var x = pos.GetProperty("x").GetDouble();
    var z = pos.GetProperty("z").GetDouble();
    var accepted = false;
    foreach (var offset in new[] { (5.0, 0.0), (0.0, 5.0), (-5.0, 0.0), (0.0, -5.0) })
    {
        try { Send($"move {Format(x + offset.Item1)} {Format(z + offset.Item2)}"); accepted = true; break; }
        catch (InvalidOperationException) { }
    }
    if (!accepted) throw new Exception("No reachable disconnect-test destination.");
    Thread.Sleep(250);
    Console.WriteLine("Closing client during movement without sending stop.");
    return;
}

Console.WriteLine("Commands: spawn, move X Z, step DX DZ, stop, status, despawn, exit");
while (true)
{
    Console.Write("guest> ");
    var input = Console.ReadLine()?.Trim();
    if (input == null || input == "exit") { Send("stop"); break; }
    try
    {
        var p = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0) continue;
        // Refresh epoch before a new user action; never retry a stale movement across worlds.
        var current = Send("status");
        if (p[0] == "step" && p.Length == 3)
        {
            var pos = current.GetProperty("guestPosition");
            var x = pos.GetProperty("x").GetDouble() + double.Parse(p[1], CultureInfo.InvariantCulture);
            var z = pos.GetProperty("z").GetDouble() + double.Parse(p[2], CultureInfo.InvariantCulture);
            input = $"move {Format(x)} {Format(z)}";
        }
        Console.WriteLine(Send(input).GetRawText());
    }
    catch (Exception e) { Console.WriteLine(e.Message); }
}
