// VR Motion Recorder addition (not upstream): VRChat-style FBT spine lock for a tracked hip.
// The base solver always lets a tracked hip move so the head reaches the headset (TrackedHipsPositionFollow = 1, i.e.
// VRChat's "Lock Head"). The app can override that follow amount per solve:
//   1 = Lock Head (head on the headset, the hips may leave their tracker)
//   0 = Lock Hips (hips stay on their tracker; the spine bends toward the head, the head may fall short)
//   in between = both held, the remaining gap is shared.
// Read in SolveRalivSpineIK (FbikSolver.Spine.cs). Default (override off) = upstream behaviour.
// Thread-static (set by the caller right before each solve on the same thread): instance fields in a second
// partial declaration would make the struct layout order undefined (CS0282).
using System;

namespace VRMR.Fbik
{
    public partial struct FbikSolver
    {
        [ThreadStatic] public static bool VrmrHipsFollowOverride;
        [ThreadStatic] public static float VrmrHipsFollow;

        static float TrackedHipsFollow => VrmrHipsFollowOverride ? Mathf.Clamp01(VrmrHipsFollow) : TrackedHipsPositionFollow;
    }
}
