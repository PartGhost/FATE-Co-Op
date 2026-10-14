using System;
using System.Globalization;

namespace FateCoop.Transport;

public sealed class WireCommand
{
    public long Sequence;
    public int Epoch;
    public string Operation = "";
    public string Argument = "";
    public float X, Z, Yaw;

    // v1 <session-token> <sequence> <world-epoch> <operation> [x z]
    public static bool TryParse(string line, string token, out WireCommand command)
    {
        command = new WireCommand();
        if (line.Length > 256) return false;
        var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 5 || p[0] != "v1" || p[1] != token
            || !long.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out command.Sequence) || command.Sequence <= 0
            || !int.TryParse(p[3], NumberStyles.None, CultureInfo.InvariantCulture, out command.Epoch)) return false;
        command.Operation = p[4];
        if (command.Operation == "join-world" || command.Operation == "peer-frame")
        {
            if (p.Length != 6 || p[5].Length != 64) return false;
            foreach (var c in p[5]) if (!Uri.IsHexDigit(c)) return false;
            command.Argument = p[5];
            return true;
        }
        if (command.Operation == "move" || command.Operation == "mirror")
            return p.Length == (command.Operation == "mirror" ? 8 : 7) && float.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out command.X)
                && float.TryParse(p[6], NumberStyles.Float, CultureInfo.InvariantCulture, out command.Z)
                && !float.IsNaN(command.X) && !float.IsInfinity(command.X) && !float.IsNaN(command.Z) && !float.IsInfinity(command.Z)
                && (command.Operation != "mirror" || (float.TryParse(p[7], NumberStyles.Float, CultureInfo.InvariantCulture, out command.Yaw) && !float.IsNaN(command.Yaw) && !float.IsInfinity(command.Yaw)));
        return p.Length == 5 && (command.Operation == "status" || command.Operation == "spawn" || command.Operation == "stop" || command.Operation == "despawn" || command.Operation == "export-world" || command.Operation == "join-town" || command.Operation == "session-on" || command.Operation == "session-off");
    }
}
