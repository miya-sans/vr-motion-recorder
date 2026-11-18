// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.
namespace VRMR.Fbik
{
    public struct IkBatchPositionFilterJob
    {
        public NativeArray<byte> mode;
        public NativeArray<float3> rawInputs;
        public NativeArray<float4> tuning;
        public NativeArray<IkEuroVec3State> euroStates;
        public NativeArray<float3> fallbackStates;
        public NativeArray<float3> outputs;
        public float dt;
        public float4x4 playspaceToWorld;
        // Indexers, not UnsafeUtility: six arrays are indexed by the same i and nothing here checks
        // they are the same length, so a caller scheduling on one array's count while another is
        // short read/wrote off the end of a heap block with no diagnostic in any build. Burst strips
        // the container bounds check when ENABLE_UNITY_COLLECTIONS_CHECKS is off, so a player build
        // emits the same loads and stores it did before and the editor now catches the mismatch.
        public void Run(int count) { for (int i = 0; i < count; i++) Execute(i); }
        public void Execute(int i)
        {
            byte m = mode[i];
            float3 x = rawInputs[i];

            if (m == (byte)IkFilterMode.Passthrough)
            {
                outputs[i] = math.transform(playspaceToWorld, x);
                return;
            }

            float4 t = tuning[i];

            if (m == (byte)IkFilterMode.Fallback)
            {
                float3 fs = IkFilterMath.IsFinite(x) ? math.lerp(fallbackStates[i], x, t.w) : fallbackStates[i];
                if (!IkFilterMath.IsFinite(fs)) fs = IkFilterMath.IsFinite(x) ? x : float3.zero;
                fallbackStates[i] = fs;
                outputs[i] = math.transform(playspaceToWorld, fs);
                return;
            }

            IkEuroVec3State st = euroStates[i];
            float3 result = IkFilterMath.EuroVec3(ref st, x, math.max(dt, 1e-6f), t.x, t.y, t.z);
            euroStates[i] = st;
            outputs[i] = math.transform(playspaceToWorld, result);
        }
    }
    public struct IkBatchRotationFilterJob
    {
        public NativeArray<byte> mode;
        public NativeArray<quaternion> rawInputs;
        public NativeArray<float4> tuning;
        public NativeArray<IkEuroQuatState> euroStates;
        public NativeArray<quaternion> fallbackStates;
        public NativeArray<quaternion> outputs;
        public float dt;
        public quaternion playspaceRotation;
        // See IkBatchPositionFilterJob.Execute for why these are indexers and not UnsafeUtility.
        public void Run(int count) { for (int i = 0; i < count; i++) Execute(i); }
        public void Execute(int i)
        {
            byte m = mode[i];
            quaternion q = rawInputs[i];

            if (m == (byte)IkFilterMode.Passthrough)
            {
                outputs[i] = math.mul(playspaceRotation, q);
                return;
            }

            float4 t = tuning[i];

            if (m == (byte)IkFilterMode.Fallback)
            {
                quaternion fs = IkFilterMath.IsUnit(q) ? IkFilterMath.SlerpShortest(fallbackStates[i], q, t.w) : fallbackStates[i];
                if (!IkFilterMath.IsUnit(fs)) fs = IkFilterMath.IsUnit(q) ? q : quaternion.identity;
                fallbackStates[i] = fs;
                outputs[i] = math.mul(playspaceRotation, fs);
                return;
            }

            IkEuroQuatState st = euroStates[i];
            quaternion result = IkFilterMath.EuroQuat(ref st, q, math.max(dt, 1e-6f), t.x, t.y, t.z);
            euroStates[i] = st;
            outputs[i] = math.mul(playspaceRotation, result);
        }
    }
    // IkReadBoneWorldPoseJob / IkPoseScatterJob / IkPoseGatherJob (TransformAccess jobs) dropped in the port:
    // the in-memory IkPoseSkeleton has no Transforms to read or write.
    public static class IkFilterMath
    {
        public static float Alpha(float cutoff, float dt)
        {
            float tau = 1.0f / (2.0f * math.PI * cutoff);
            return 1.0f / (1.0f + tau / math.max(dt, 1e-6f));
        }
        public static bool IsFinite(float3 v) => math.all(math.isfinite(v));
        public static bool IsUnit(quaternion q)
        {
            float lengthSq = math.lengthsq(q.value);
            return lengthSq > 0.5f && lengthSq < 2f;
        }
        public static float3 EuroVec3(ref IkEuroVec3State st, float3 x, float dt, float minCutoff, float beta, float dCutoff)
        {
            if (!IsFinite(x)) return st.xHasPrev && IsFinite(st.hatX) ? st.hatX : float3.zero;
            if (st.xHasPrev && (!IsFinite(st.hatX) || !IsFinite(st.hatDx))) st = default;
            float3 prevHatX = st.xHasPrev ? st.hatX : x, dx = (prevHatX - x) / dt;
            float ad = Alpha(dCutoff, dt);
            if (st.dxHasPrev) st.hatDx = math.lerp(st.hatDx, dx, ad);
            else { st.hatDx = dx; st.dxHasPrev = true; }

            float cutoff = minCutoff + beta * math.length(st.hatDx), a = Alpha(cutoff, dt);

            if (st.xHasPrev) st.hatX = math.lerp(st.hatX, x, a);
            else { st.hatX = x; st.xHasPrev = true; }

            return st.hatX;
        }
        public static quaternion EuroQuat(ref IkEuroQuatState st, quaternion q, float dt, float minCutoff, float beta, float dCutoff)
        {
            if (!IsUnit(q)) return st.hasPrev && IsUnit(st.prev) ? st.prev : quaternion.identity;
            if (st.hasPrev && (!IsUnit(st.prev) || !IsFinite(st.logVecState.hatDx))) st = default;
            if (!st.hasPrev)
            {
                st.hasPrev = true;
                st.prev = math.normalize(q);
                st.logVecState = default;
                return st.prev;
            }
            dt = math.max(dt, 1e-6f);
            float4 pv = st.prev.value, qv = q.value;
            if (math.dot(pv, qv) < 0f) qv = -qv;
            q = new quaternion(qv);
            float4 dv = math.mul(q, math.conjugate(st.prev)).value;
            float w = math.clamp(dv.w, -1f, 1f), sinHalf = math.sqrt(math.max(0f, 1f - w * w));
            float3 rate = sinHalf > 1e-6f ? dv.xyz * (2f * math.acos(w) / (sinHalf * dt)) : float3.zero;
            float ad = Alpha(dCutoff, dt);
            if (st.logVecState.dxHasPrev) st.logVecState.hatDx = math.lerp(st.logVecState.hatDx, rate, ad);
            else { st.logVecState.hatDx = rate; st.logVecState.dxHasPrev = true; }
            float cutoff = minCutoff + beta * math.length(st.logVecState.hatDx), a = Alpha(cutoff, dt);
            quaternion outQ = math.normalize(SlerpShortest(st.prev, q, a));
            if (!IsUnit(outQ))
            {
                st = default;
                return q;
            }
            st.prev = outQ;
            return outQ;
        }
        public static quaternion SlerpShortest(quaternion a, quaternion b, float t)
        {
            float4 av = a.value, bv = b.value;
            float cosHalf = math.dot(av, bv);
            if (cosHalf < 0f) { bv = -bv; cosHalf = -cosHalf; }

            if (cosHalf > 0.9995f)
            {
                float4 r = math.normalize(math.lerp(av, bv, t));
                return new quaternion(r);
            }

            float halfAngle = math.acos(math.min(cosHalf, 1f)), sinHalf = math.sin(halfAngle);
            float wa = math.sin((1f - t) * halfAngle) / sinHalf, wb = math.sin(t * halfAngle) / sinHalf;
            return new quaternion(av * wa + bv * wb);
        }
    }
}
