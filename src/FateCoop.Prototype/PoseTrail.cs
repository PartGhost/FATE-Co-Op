using System.Collections.Generic;
using UnityEngine;

namespace FateCoop.Prototype;

internal sealed class PoseTrail
{
    private const int Capacity = 32;
    private const float SnapDistance = 6f, StaleAfter = 0.5f;
    private readonly List<(float time, Vector3 position, float yaw)> poses = new List<(float, Vector3, float)>();

    internal void Add(float time, Vector3 position, float yaw)
    {
        if (poses.Count > 0)
        {
            var last = poses[poses.Count - 1];
            if (time <= last.time) return;
            if ((position - last.position).sqrMagnitude > SnapDistance * SnapDistance) poses.Clear();
        }
        poses.Add((time, position, yaw));
        if (poses.Count > Capacity) poses.RemoveAt(0);
    }

    internal void Clear() => poses.Clear();

    internal bool Sample(float renderTime, out Vector3 position, out float yaw, out bool live)
    {
        position = Vector3.zero;
        yaw = 0f;
        live = false;
        if (poses.Count == 0) return false;
        var last = poses[poses.Count - 1];
        if (renderTime >= last.time)
        {
            position = last.position;
            yaw = last.yaw;
            live = renderTime - last.time < StaleAfter;
            return true;
        }
        live = true;
        if (renderTime <= poses[0].time)
        {
            position = poses[0].position;
            yaw = poses[0].yaw;
            return true;
        }
        for (var i = poses.Count - 2; i >= 0; i--)
        {
            if (poses[i].time > renderTime) continue;
            var from = poses[i];
            var to = poses[i + 1];
            var t = (renderTime - from.time) / (to.time - from.time);
            position = Vector3.Lerp(from.position, to.position, t);
            yaw = Mathf.LerpAngle(from.yaw, to.yaw, t);
            return true;
        }
        return true;
    }
}
