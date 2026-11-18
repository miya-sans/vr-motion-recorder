using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VRMR
{
    /// <summary>
    ///     Finds the mesh of an avatar that carries its face shapes, so that nobody has to type its name: the mesh whose
    ///     shape keys cover the most face-tracking shapes, or, on a model without face tracking, the mesh with by far the
    ///     most shape keys.
    /// </summary>
    public static class FaceMeshFinder
    {
        /// <summary>The name most avatars give their face mesh; used only when a model's meshes are not known at all.</summary>
        public const string Fallback = "Body";

        /// <summary>A mesh without face-tracking shapes still counts as the face when it has at least this many shape keys.</summary>
        const int ManyShapes = 10;

        /// <summary>
        ///     The face mesh of an FBX as an animation path from the avatar's root (the mesh's path in
        ///     <paramref name="skeleton" /> when it is a node there, else its name), or null when the file cannot be read or
        ///     no mesh has shape keys.
        /// </summary>
        public static string InFbx(string fbxPath, AvatarProfile skeleton = null)
        {
            try
            {
                if (string.IsNullOrEmpty(fbxPath) || !File.Exists(fbxPath)) return null;
                string mesh = Best(ShapesByMesh(FbxReader.Read(fbxPath)));
                if (mesh == null) return null;
                var node = skeleton?.Nodes.FirstOrDefault(n => n.Name == mesh);
                return node != null && !string.IsNullOrEmpty(node.Path) ? node.Path : mesh;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Face mesh of " + Path.GetFileName(fbxPath) + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        ///     The face mesh among the meshes a skeleton lists with their shape keys (skeletons exported by the Unity
        ///     bridge), or null.
        /// </summary>
        public static string InProfile(AvatarProfile profile)
        {
            if (profile == null || profile.Meshes.Count == 0) return null;
            return Best(profile.Meshes.Where(m => !string.IsNullOrEmpty(m.Path))
                .Select(m => new KeyValuePair<string, List<string>>(m.Path, m.BlendShapes)));
        }

        /// <summary>The name with the most face shapes, else the one with clearly the most shape keys; null when there is none.</summary>
        internal static string Best(IEnumerable<KeyValuePair<string, List<string>>> shapesByMesh)
        {
            string best = null;
            int bestFace = 0, bestAll = 0;
            foreach (var kv in shapesByMesh)
            {
                int face = FaceMapping.MatchToMesh(FaceMapping.UnifiedNames, kv.Value).Count;
                int all = kv.Value.Count;
                bool better = face > bestFace || (face == bestFace && all > bestAll);
                if (!better) continue;
                best = kv.Key;
                bestFace = face;
                bestAll = all;
            }

            return bestFace > 0 || bestAll >= ManyShapes ? best : null;
        }

        /// <summary>Every mesh of an FBX with the names of its shape keys.</summary>
        static IEnumerable<KeyValuePair<string, List<string>>> ShapesByMesh(List<FbxNode> top)
        {
            var objects = top.FirstOrDefault(n => n.Name == "Objects");
            var connections = top.FirstOrDefault(n => n.Name == "Connections");
            if (objects == null || connections == null) yield break;
            var byId = new Dictionary<long, FbxNode>();
            foreach (var o in objects.Children) byId[o.Id()] = o;
            var kids = new Dictionary<long, List<FbxNode>>();
            foreach (var c in connections.Children)
            {
                if (c.Props.Count < 3 || !(c.Props[1] is long child) || !(c.Props[2] is long parent) ||
                    !byId.TryGetValue(child, out var node)) continue;
                if (!kids.TryGetValue(parent, out var list)) kids[parent] = list = new List<FbxNode>();
                list.Add(node);
            }

            List<FbxNode> Kids(FbxNode n)
            {
                return kids.TryGetValue(n.Id(), out var l) ? l : new List<FbxNode>();
            }

            foreach (var model in objects.ChildrenNamed("Model").Where(o => o.ObjClass() == "Mesh"))
            {
                var geometry = Kids(model).FirstOrDefault(k => k.Name == "Geometry" && k.ObjClass() == "Mesh");
                if (geometry == null) continue;
                var names = new List<string>();
                foreach (var deformer in Kids(geometry)
                             .Where(k => k.Name == "Deformer" && k.ObjClass() == "BlendShape"))
                foreach (var channel in Kids(deformer).Where(k => k.ObjClass() == "BlendShapeChannel"))
                    names.Add(channel.ObjName());
                if (names.Count > 0) yield return new KeyValuePair<string, List<string>>(model.ObjName(), names);
            }
        }
    }
}
