// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Port for VR Motion Recorder: the Transform/TransformAccessArray-backed skeleton is replaced by an
// in-memory node list (parent index + authored local TRS). The IkPoseStream it owns is unchanged.
// Node 0 is the avatar root; the stream's Anchor is the root's parent (identity here unless set).
// Gather/Scatter (Transform IO) become "reset stream to base pose" / no-op; everything else
// (rest data, body-fit scaling, rest-position queries) keeps the upstream semantics.
using System;

namespace VRMR.Fbik
{
    public sealed class IkPoseSkeleton : IDisposable
    {
        public IkPoseStream Stream;
        public int[] WriteIndices = Array.Empty<int>();
        public float[] FitScale = Array.Empty<float>();
        public bool FitActive;
        float3[] authoredLocalPosition = Array.Empty<float3>();
        // Base pose the stream is reset to before every solve (replaces the Animator pose the original solver gathers).
        float3[] basePosition = Array.Empty<float3>();
        quaternion[] baseRotation = Array.Empty<quaternion>();
        float3[] baseScale = Array.Empty<float3>();
        public Vector3 AnchorPosition = Vector3.zero;
        public Quaternion AnchorRotation = Quaternion.identity;
        public bool IsCreated => Stream.LocalPosition.IsCreated;
        public int Count => Stream.Count;

        /// <summary>
        /// Builds the stream from nodes already ordered parents-first. parent[i] must be &lt; i (or -1 for node 0).
        /// Local rotations with a near-zero length become identity and non-finite positions become zero, as upstream.
        /// </summary>
        public void Build(int[] parent, Vector3[] localPosition, Quaternion[] localRotation, Vector3[] localScale)
        {
            Dispose();
            int count = parent.Length;
            if (count == 0)
            {
                return;
            }
            Stream = new IkPoseStream
            {
                LocalPosition = new NativeArray<float3>(count, Allocator.Persistent),
                LocalRotation = new NativeArray<quaternion>(count, Allocator.Persistent),
                LocalScale = new NativeArray<float3>(count, Allocator.Persistent),
                Parent = new NativeArray<int>(count, Allocator.Persistent),
                BindLength = new NativeArray<float>(count, Allocator.Persistent),
                TranslationFree = new NativeArray<byte>(count, Allocator.Persistent),
                RestLocalRotation = new NativeArray<quaternion>(count, Allocator.Persistent),
                RestLocalPosition = new NativeArray<float3>(count, Allocator.Persistent),
                RestLocalScale = new NativeArray<float3>(count, Allocator.Persistent),
                WorldPositionCache = new NativeArray<float3>(count, Allocator.Persistent),
                WorldRotationCache = new NativeArray<quaternion>(count, Allocator.Persistent),
                WorldScaleCache = new NativeArray<float3>(count, Allocator.Persistent),
                WorldCacheStamp = new NativeArray<int>(count + 1, Allocator.Persistent),
                Count = count,
            };
            Stream.WorldCacheStamp[count] = 1;
            FitScale = new float[count];
            authoredLocalPosition = new float3[count];
            basePosition = new float3[count];
            baseRotation = new quaternion[count];
            baseScale = new float3[count];
            for (int i = 0; i < count; i++)
            {
                int p = parent[i];
                if (p >= i)
                {
                    throw new ArgumentException("IkPoseSkeleton.Build: nodes must be ordered parents-first.");
                }
                float3 authored = localPosition[i];
                if (!math.all(math.isfinite(authored)))
                {
                    authored = float3.zero;
                }
                quaternion localRot = localRotation[i];
                float rotationLengthSq = math.lengthsq(localRot.value);
                float3 scale = localScale != null ? (float3)localScale[i] : new float3(1f);
                authoredLocalPosition[i] = authored;
                Stream.RestLocalPosition[i] = authored;
                FitScale[i] = 1f;
                Stream.Parent[i] = p;
                Stream.BindLength[i] = math.length(authored);
                Stream.TranslationFree[i] = (byte)(p < 0 ? 1 : 0);
                Stream.LocalPosition[i] = authored;
                Stream.LocalRotation[i] = math.isfinite(rotationLengthSq) && rotationLengthSq > 1e-8f ? localRot : quaternion.identity;
                Stream.RestLocalRotation[i] = Stream.LocalRotation[i];
                Stream.LocalScale[i] = math.all(math.isfinite(scale)) ? scale : new float3(1f);
                Stream.RestLocalScale[i] = Stream.LocalScale[i];
                basePosition[i] = authored;
                baseRotation[i] = Stream.LocalRotation[i];
                baseScale[i] = Stream.LocalScale[i];
            }
            WriteIndices = new int[count];
            for (int i = 0; i < count; i++) WriteIndices[i] = i;
            SyncAnchor();
            Stream.InvalidateWorldCache();
        }
        public IkBoneHandle Bind(int node) => node >= 0 && node < Count ? IkBoneHandle.FromIndex(node) : IkBoneHandle.Unbound;
        /// <summary>Fitted rest position of a node in root-local space (the root's own transform excluded), as upstream.</summary>
        public bool TryGetRestPositionRelativeTo(int boneIndex, int rootIndex, out Vector3 position)
        {
            position = Vector3.zero;
            if (!IsCreated || boneIndex < 0 || rootIndex < 0 || boneIndex >= Count || rootIndex >= Count)
            {
                return false;
            }
            if (boneIndex == rootIndex)
            {
                return true;
            }
            int[] chain = new int[IkPoseStream.MaxDepth];
            int depth = 0;
            int walk = boneIndex;
            while (walk >= 0 && walk != rootIndex && depth < chain.Length)
            {
                chain[depth++] = walk;
                walk = Stream.Parent[walk];
            }
            if (walk != rootIndex)
            {
                return false;
            }
            float3 restPosition = float3.zero;
            quaternion restRotation = quaternion.identity;
            float3 restScale = new float3(1f);
            for (int i = depth - 1; i >= 0; i--)
            {
                int index = chain[i];
                restPosition += math.mul(restRotation, Stream.RestLocalPosition[index] * restScale);
                restRotation = math.mul(restRotation, Stream.RestLocalRotation[index]);
                restScale *= Stream.RestLocalScale[index];
            }
            if (!math.all(math.isfinite(restPosition)))
            {
                return false;
            }
            position = restPosition;
            return true;
        }
        public void SetTranslationFree(int index)
        {
            if (index >= 0 && index < Count)
            {
                Stream.TranslationFree[index] = 1;
            }
        }
        public void SetFitScale(int index, float scale)
        {
            if (index < 0 || index >= Count || Stream.TranslationFree[index] != 0)
            {
                return;
            }
            float safe = scale > 0f && math.isfinite(scale) ? scale : 1f;
            float3 fitted = authoredLocalPosition[index] * safe;
            FitScale[index] = safe;
            Stream.RestLocalPosition[index] = math.all(math.isfinite(fitted)) ? fitted : float3.zero;
            Stream.BindLength[index] = math.length(authoredLocalPosition[index]) * safe;
            if (!Mathf.Approximately(safe, 1f))
            {
                FitActive = true;
            }
        }
        public void ResetFit()
        {
            for (int i = 0; i < FitScale.Length; i++)
            {
                FitScale[i] = 1f;
                Stream.RestLocalPosition[i] = authoredLocalPosition[i];
                Stream.BindLength[i] = math.length(authoredLocalPosition[i]);
            }
            FitActive = false;
        }
        public void ApplyFit()
        {
            if (!FitActive)
            {
                return;
            }
            for (int i = 0; i < FitScale.Length; i++)
            {
                if (!Mathf.Approximately(FitScale[i], 1f))
                {
                    Stream.LocalPosition[i] = Stream.RestLocalPosition[i];
                }
            }
            Stream.InvalidateWorldCache();
        }
        /// <summary>Upstream wrote the fitted rest positions to the Transforms; here they become the base pose.</summary>
        public void WriteFittedLocalPositions()
        {
            if (!IsCreated)
            {
                return;
            }
            Stream.RestLocalPosition.CopyTo(Stream.LocalPosition);
            for (int i = 0; i < Count; i++)
            {
                basePosition[i] = Stream.RestLocalPosition[i];
            }
            Stream.InvalidateWorldCache();
        }
        /// <summary>Sets the root node's local pose (upstream: RefreshRootFromTransform reads the Animator transform).</summary>
        public void SetRootLocal(Vector3 position, Quaternion rotation)
        {
            if (!IsCreated)
            {
                return;
            }
            SyncAnchor();
            Stream.LocalPosition[0] = position;
            Stream.LocalRotation[0] = rotation;
            basePosition[0] = position;
            baseRotation[0] = rotation;
            Stream.InvalidateWorldCache();
        }
        /// <summary>Replaces GatherNow: the "animated" pose fed to the solver is the stored base pose.</summary>
        public void GatherNow()
        {
            if (!IsCreated)
            {
                return;
            }
            SyncAnchor();
            for (int i = 0; i < Count; i++)
            {
                Stream.LocalPosition[i] = basePosition[i];
                Stream.LocalRotation[i] = baseRotation[i];
                Stream.LocalScale[i] = baseScale[i];
            }
            Stream.InvalidateWorldCache();
        }
        /// <summary>Overrides one node's base local rotation (e.g. to give untracked limbs a non-T-pose rest).</summary>
        public void SetBaseLocalRotation(int index, Quaternion rotation)
        {
            if (index >= 0 && index < Count) baseRotation[index] = rotation;
        }
        public Quaternion GetBaseLocalRotation(int index) => index >= 0 && index < Count ? (Quaternion)baseRotation[index] : Quaternion.identity;
        public void ScatterNow() { }
        void SyncAnchor()
        {
            Stream.AnchorPosition = AnchorPosition;
            Stream.AnchorRotation = AnchorRotation;
            Stream.AnchorScale = new float3(1f);
        }
        public void Dispose()
        {
            if (!IsCreated)
            {
                return;
            }
            Stream.LocalPosition.Dispose();
            Stream.LocalRotation.Dispose();
            Stream.LocalScale.Dispose();
            Stream.Parent.Dispose();
            Stream.BindLength.Dispose();
            Stream.TranslationFree.Dispose();
            Stream.RestLocalRotation.Dispose();
            Stream.WorldPositionCache.Dispose();
            Stream.WorldRotationCache.Dispose();
            Stream.WorldScaleCache.Dispose();
            Stream.WorldCacheStamp.Dispose();
            Stream.RestLocalPosition.Dispose();
            Stream.RestLocalScale.Dispose();
            Stream = default;
            WriteIndices = Array.Empty<int>();
            FitScale = Array.Empty<float>();
            authoredLocalPosition = Array.Empty<float3>();
            FitActive = false;
        }
    }

    /// <summary>A rigid transform (position + rotation), e.g. a bone's calibrated T-pose coordinates.</summary>
    [Serializable]
    public struct IkCalibratedCoords
    {
        public Quaternion rotation;
        public Vector3 position;
        public static IkCalibratedCoords Identity => new IkCalibratedCoords(Vector3.zero, Quaternion.identity);
        public IkCalibratedCoords(Vector3 pos, Quaternion rot)
        {
            position = pos;
            rotation = rot;
        }
        public static Vector3 operator *(IkCalibratedCoords basisTransform, Vector3 vec) => basisTransform.position + (basisTransform.rotation * vec);
    }
}
