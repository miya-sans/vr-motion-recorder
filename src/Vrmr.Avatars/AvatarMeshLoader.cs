using System;
using System.Collections.Concurrent;
using System.IO;

namespace VRMR
{
    /// <summary>Loads and caches avatar meshes using the owning host's texture decoder.</summary>
    public sealed class AvatarMeshLoader
    {
        readonly ConcurrentDictionary<string, Lazy<SkinnedAvatar>> _cache = new(StringComparer.OrdinalIgnoreCase);
        readonly TextureDecoder _decoder;

        /// <summary>Creates a cache with a fixed decoder; null leaves textures undecoded.</summary>
        public AvatarMeshLoader(TextureDecoder decoder = null) { _decoder = decoder; }

        /// <summary>Forgets the loaded meshes of a model file, so the next use reads it again (its face shapes changed).</summary>
        public void Forget(string fbx)
        {
            foreach (string key in _cache.Keys)
                if (key.StartsWith(fbx + "|", StringComparison.OrdinalIgnoreCase))
                    _cache.TryRemove(key, out _);
        }

        /// <summary>Loads (once per FBX + skeleton) on the calling thread; null when there is no FBX or loading failed.</summary>
        public SkinnedAvatar ForProfile(AvatarProfile p)
        {
            string fbx = SkinnedAvatar.FbxFor(p);
            if (fbx == null) return null;
            string key = fbx + "|" + p.Nodes.Count;
            var lazy = _cache.GetOrAdd(key, _ => new Lazy<SkinnedAvatar>(() =>
            {
                try { return SkinnedAvatar.Load(fbx, p, _decoder); }
                catch (Exception ex)
                {
                    AppLog.Warn("Avatar mesh preview: " + Path.GetFileName(fbx) + ": " + ex.Message);
                    return null;
                }
            }));
            return lazy.Value;
        }
    }
}
