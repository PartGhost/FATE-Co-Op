using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FateCoop.Core;

public static class TestChannel
{
    public static readonly Dictionary<string, Action<string[]>> Commands = new Dictionary<string, Action<string[]>>();
    public static readonly List<Func<object>> Status = new List<Func<object>>();

    public static string Root { get; internal set; } = "";

    public static bool Enabled => Root.Length > 0;

    public static string WithStatus(string json) =>
        Status.Select(part => JsonUtility.ToJson(part())).Where(part => part.Length > 2).Aggregate(json, (all, part) => all.Substring(0, all.Length - 1) + "," + part.Substring(1));
}
