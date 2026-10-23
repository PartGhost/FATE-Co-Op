using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Tableflip;
using UnityEngine;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const int RevealReach = 2;
    private const float IconLift = 1f;
    private static readonly AccessTools.FieldRef<MinimapFogGridUI, List<bool>> FogCellsRef = AccessTools.FieldRefAccess<MinimapFogGridUI, List<bool>>("visibility");
    private static readonly AccessTools.FieldRef<MinimapFogGridUI, int> FogWidthRef = AccessTools.FieldRefAccess<MinimapFogGridUI, int>("sizeX");
    private static readonly AccessTools.FieldRef<MinimapFogGridUI, bool> FogClearedRef = AccessTools.FieldRefAccess<MinimapFogGridUI, bool>("removeFog");
    private static readonly MethodInfo RevealFogMethod = AccessTools.Method(typeof(MinimapFogGridUI), "UpdateFogMesh");

    private static bool MapRevealed() => SceneLoad.CurrentScene == Tableflip.Scene.Dungeon && SingletonBehaviour<MinimapFogGridUI>.InstanceExists() && FogClearedRef(SingletonBehaviour<MinimapFogGridUI>.Instance);

    private void UpdateSharedMap()
    {
        if (SceneLoad.CurrentScene != Tableflip.Scene.Dungeon || LevelGenerator.currentInstance == null || !SingletonBehaviour<MinimapFogGridUI>.InstanceExists()) return;
        var fog = SingletonBehaviour<MinimapFogGridUI>.Instance;
        if (!FogClearedRef(fog) && WorldPartners().Any(p => p.Frame!.mapRevealed)) LevelGenerator.currentInstance.DiscoverAll();
        var cells = FogCellsRef(fog);
        var width = FogWidthRef(fog);
        var maze = LevelGenerator.currentInstance.GetMaze();
        var hidden = new HashSet<int>();
        foreach (var partner in WorldPartners().Where(p => p.Actor != null))
        {
            var lifted = partner.MapIcon!.transform.position;
            lifted.y = fog.transform.position.y + IconLift;
            partner.MapIcon.transform.position = lifted;
            if (cells == null || FogClearedRef(fog) || width <= 0) continue;
            var position = partner.Actor!.transform.position;
            var center = maze.GetCellIndexFromPosition((int)position.x, (int)position.z);
            for (var x = center.x - RevealReach; x <= center.x + RevealReach; x++)
            for (var y = center.y - RevealReach; y <= center.y + RevealReach; y++)
            {
                var index = x + y * width;
                if (x >= 0 && x < width && index >= 0 && index < cells.Count && !cells[index]) hidden.Add(index);
            }
        }
        if (hidden.Count > 0) RevealFogMethod.Invoke(fog, new object[] { hidden.ToList() });
    }
}
