using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Tableflip;
using UnityEngine;

namespace FateCoop.Capes;

public sealed partial class Plugin
{
    private const int CapeLinks = 3;
    private const float CapePinned = 0.15f, CapeDrag = 3f, CapeStiffness = 35f, CapeDamping = 9f, CapeTeleportDistance = 1f;
    private static readonly HashSet<string> RigidBackItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Item_BloodQuiver", "Item_ForestQuiver", "Item_GoblinQuiver", "Item_LightQuiver", "Item_RoyalQuiver", "Item_BroadwingMagicalWings", "Item_TravelerBackpack",
        "Item_BlazenShield", "Item_Twilight", "Item_Crabfork", "Item_Uber", "Item_Skullcutt", "Item_IronSickle", "Item_IronCleaver", "Item_Doomstick"
    };
    private static readonly AccessTools.FieldRef<CharacterCustomizationDefaults, CharacterCustomizationDefaults.CustomizationCategory> BackRef =
        AccessTools.FieldRefAccess<CharacterCustomizationDefaults, CharacterCustomizationDefaults.CustomizationCategory>("back");
    private static readonly Dictionary<Mesh, CapeRig?> CapeRigs = new Dictionary<Mesh, CapeRig?>();

    private sealed class CapeRig
    {
        internal Mesh Mesh = null!;
        internal int Spine;
        internal Vector3[] Joints = null!;
    }

    private void InstallCapeHooks() =>
        harmony!.Patch(AccessTools.Method(typeof(CharacterCustomizationDefaults), nameof(CharacterCustomizationDefaults.SetBack)), postfix: new HarmonyMethod(typeof(Plugin), nameof(AfterSetBack)));

    private static void AfterSetBack(CharacterCustomizationDefaults __instance, string backKey)
    {
        var back = BackRef(__instance);
        if (back == null || back.defaultMesh == null) return;
        var renderer = back.SkinnedMesh;
        if (renderer.TryGetComponent<CapeSwing>(out var old)) old.Remove();
        if (string.IsNullOrEmpty(backKey) || RigidBackItems.Contains(backKey.TrimEnd()) || !renderer.gameObject.activeSelf) return;
        var source = renderer.sharedMesh;
        if (!CapeRigs.TryGetValue(source, out var rig)) CapeRigs[source] = rig = RigCape(source, renderer.bones);
        if (rig != null) renderer.gameObject.AddComponent<CapeSwing>().Attach(renderer, rig);
    }

    private static CapeRig? RigCape(Mesh source, Transform[] bones)
    {
        var mesh = ReadableCopy(source);
        var bindposes = mesh.bindposes;
        var vertices = mesh.vertices;
        if (vertices.Length == 0) return null;
        var spine = Array.FindIndex(bones, bone => bone.name.EndsWith("_Spine1", StringComparison.Ordinal));
        Vector3 joint(string suffix) => bindposes[Array.FindIndex(bones, bone => bone.name.EndsWith(suffix, StringComparison.Ordinal))].inverse.MultiplyPoint3x4(Vector3.zero);
        var up = (joint("_Head") - joint("_Pelvis")).normalized;
        var heights = vertices.Select(vertex => Vector3.Dot(vertex, up)).ToArray();
        var top = heights.Max();
        var length = top - heights.Min();
        if (!(length > 0f)) return null;
        var depth = heights.Select(height => (top - height) / length).ToArray();
        Vector3 centre(float along)
        {
            for (var reach = 0.08f; ; reach += 0.05f)
            {
                var inside = vertices.Where((vertex, i) => Mathf.Abs(depth[i] - along) <= reach).ToList();
                if (inside.Count > 0) return bindposes[spine].MultiplyPoint3x4(inside.Aggregate(Vector3.zero, (sum, vertex) => sum + vertex) / inside.Count);
            }
        }
        var joints = Enumerable.Range(0, CapeLinks + 1).Select(i => centre(CapePinned + (1f - CapePinned) * i / CapeLinks)).ToArray();
        var first = bindposes.Length;
        mesh.bindposes = bindposes.Concat(joints.Take(CapeLinks).Select(point => Matrix4x4.Translate(-point) * bindposes[spine])).ToArray();
        mesh.boneWeights = mesh.boneWeights.Select((weight, i) =>
        {
            if (depth[i] < CapePinned) return weight;
            var along = Mathf.Min((depth[i] - CapePinned) / (1f - CapePinned) * CapeLinks, CapeLinks - 0.001f);
            var link = (int)along;
            var blend = along - link;
            var next = Math.Min(link + 1, CapeLinks - 1);
            return next == link ? new BoneWeight { boneIndex0 = first + link, weight0 = 1f } : new BoneWeight { boneIndex0 = first + link, weight0 = 1f - blend, boneIndex1 = first + next, weight1 = blend };
        }).ToArray();
        return new CapeRig { Mesh = mesh, Spine = spine, Joints = joints };
    }

    private static Mesh ReadableCopy(Mesh locked)
    {
        locked.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
        locked.indexBufferTarget |= GraphicsBuffer.Target.Raw;
        var copy = new Mesh { name = locked.name, indexFormat = locked.indexFormat };
        copy.SetVertexBufferParams(locked.vertexCount, locked.GetVertexAttributes());
        for (var stream = 0; stream < locked.vertexBufferCount; stream++)
        {
            using var buffer = locked.GetVertexBuffer(stream);
            var data = new byte[buffer.count * buffer.stride];
            buffer.GetData(data);
            copy.SetVertexBufferData(data, 0, 0, data.Length, stream);
        }
        using (var indices = locked.GetIndexBuffer())
        {
            var data = new byte[indices.count * indices.stride];
            indices.GetData(data);
            copy.SetIndexBufferParams(indices.count, locked.indexFormat);
            copy.SetIndexBufferData(data, 0, 0, data.Length);
        }
        copy.subMeshCount = locked.subMeshCount;
        for (var i = 0; i < locked.subMeshCount; i++) copy.SetSubMesh(i, locked.GetSubMesh(i));
        copy.bindposes = locked.bindposes;
        copy.boneWeights = locked.boneWeights;
        copy.RecalculateBounds();
        return copy;
    }

    private sealed class CapeSwing : MonoBehaviour
    {
        private SkinnedMeshRenderer target = null!;
        private Transform[] originalBones = null!, chain = null!;
        private Vector3[] restTips = null!, tips = null!, previous = null!, lastRests = null!;
        private Vector3 lastRoot;

        internal float Bend => chain.Take(CapeLinks).Max(link => Quaternion.Angle(Quaternion.identity, link.localRotation));

        internal void Attach(SkinnedMeshRenderer renderer, CapeRig rig)
        {
            target = renderer;
            originalBones = renderer.bones;
            chain = new Transform[CapeLinks + 1];
            for (var i = 0; i <= CapeLinks; i++)
            {
                chain[i] = new GameObject("FateCoop_CapeLink" + i).transform;
                chain[i].SetParent(i == 0 ? originalBones[rig.Spine] : chain[i - 1], false);
                chain[i].localPosition = i == 0 ? rig.Joints[0] : rig.Joints[i] - rig.Joints[i - 1];
            }
            renderer.bones = originalBones.Concat(chain.Take(CapeLinks)).ToArray();
            renderer.sharedMesh = rig.Mesh;
            restTips = chain.Skip(1).Select(link => link.localPosition).ToArray();
            Settle();
        }

        internal void Remove()
        {
            target.bones = originalBones;
            Destroy(chain[0].gameObject);
            DestroyImmediate(this);
        }

        private void Settle()
        {
            tips = chain.Skip(1).Select(link => link.position).ToArray();
            previous = tips.ToArray();
            lastRests = tips.ToArray();
            lastRoot = chain[0].position;
        }

        private void LateUpdate()
        {
            var dt = Mathf.Min(Time.deltaTime, 0.05f);
            if (dt <= 0f) return;
            var root = chain[0].position;
            var jump = root - lastRoot;
            lastRoot = root;
            if (jump.sqrMagnitude > CapeTeleportDistance * CapeTeleportDistance)
            {
                foreach (var link in chain) link.localRotation = Quaternion.identity;
                Settle();
                return;
            }
            for (var i = 0; i < CapeLinks; i++)
            {
                var link = chain[i];
                link.localRotation = Quaternion.identity;
                var head = link.position;
                var rest = link.TransformPoint(restTips[i]);
                var carry = rest - lastRests[i];
                lastRests[i] = rest;
                tips[i] += carry;
                previous[i] += carry;
                var momentum = (tips[i] - previous[i]) * Mathf.Exp(-CapeDamping * dt);
                previous[i] = tips[i];
                tips[i] += momentum - carry * CapeDrag * dt + (rest - tips[i]) * CapeStiffness * dt * dt;
                tips[i] = head + (tips[i] - head).normalized * (rest - head).magnitude;
                link.localRotation = Quaternion.FromToRotation(restTips[i], link.InverseTransformPoint(tips[i]));
            }
        }
    }
}
