using System.Collections.Generic;
using System.Linq;
using BepInEx;

namespace FateCoop.Core;

public static class SharedContent
{
    private static readonly SortedSet<string> Packs = new SortedSet<string>();

    public static void Register(BepInPlugin pack) => Packs.Add(pack.GUID.Substring(pack.GUID.LastIndexOf('.') + 1));

    public static string Key => string.Join(",", Packs.DefaultIfEmpty("base"));
}
