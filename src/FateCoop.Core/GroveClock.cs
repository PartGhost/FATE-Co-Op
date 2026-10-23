using System;

namespace FateCoop.Core;

public static class GroveClock
{
    public const float AlwaysDay = -1f;
    public static Func<float> LocalPhase = () => AlwaysDay;
    public static Func<float> HostPhase = () => AlwaysDay;
}
