// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Port for VR Motion Recorder: gizmo recording and profiler markers are compiled out. The original
// Record*Gizmos methods only read solver state (every one starts with `if (!gizmos.Wants(stage)) return;`
// and never writes the pose), so these no-op stubs do not change solver behaviour.

using System;

namespace VRMR.Fbik
{
    public enum IkGizmoKind : byte
    {
        Line = 0,
        Sphere = 1
    }

    [Flags]
    public enum IkGizmoStage
    {
        None = 0,
        Targets = 1 << 0,
        Spine = 1 << 1,
        Shoulders = 1 << 2,
        Legs = 1 << 3,
        Arms = 1 << 4,
        Toes = 1 << 5,
        Overrides = 1 << 6,
        Skeleton = 1 << 7,
        Scratch = 1 << 8,
        Frames = 1 << 9,
        Limits = 1 << 10,
        Reach = 1 << 11,
        Numbers = 1 << 12
    }

    /// <summary>Stand-in for Unity.Collections.FixedString64Bytes (only used by the stripped gizmo labels).</summary>
    public struct FixedString64Bytes
    {
        public string Value;
        public FixedString64Bytes(string value) { Value = value; }

        public static implicit operator FixedString64Bytes(string s)
        {
            return new FixedString64Bytes(s);
        }

        public override string ToString()
        {
            return Value ?? string.Empty;
        }
    }

    /// <summary>No-op gizmo recorder (the solver never records in the port).</summary>
    public struct IkGizmoRecorder
    {
        public bool IsCreated => false;

        public bool Wants(IkGizmoStage stage)
        {
            return false;
        }

        public void Clear() { }
        public void Dispose() { }
    }

    public partial struct FbikSolver
    {
        void RecordTargetGizmos() { }
        void RecordSpineGizmos() { }
        void RecordShoulderGizmos() { }
        void RecordLegGizmos() { }
        void RecordArmGizmos() { }
        void RecordToeGizmos() { }
        void RecordOverrideGizmos() { }
        void RecordFrameGizmos() { }
        void RecordLimitGizmos() { }
        void RecordReachGizmos() { }
        void RecordNumberGizmos() { }
        void RecordSkeletonGizmos() { }
    }

    /// <summary>No-op stand-in for the upstream ProfilerMarker wrapper.</summary>
    struct IkMarker
    {
        public IkMarker(string name) { }
        public void Begin() { }
        public void End() { }
    }

    static class FbikMarkers
    {
        const string Group = "Fbik";
        const string SpineGroup = Group + ".Spine";

        public static readonly IkMarker Spine = new(Group + ".Spine"),
            Shoulders = new(Group + ".Shoulders"),
            Legs = new(Group + ".Legs"),
            Arms = new(Group + ".Arms"),
            Toes = new(Group + ".Toes"),
            TrackerOverrides = new(Group + ".TrackerOverrides");

        public static readonly IkMarker SpineHipsPlacement = new(SpineGroup + ".HipsPlacement"),
            SpineChainPrep = new(SpineGroup + ".ChainPrep"),
            SpineSequentialIK = new(SpineGroup + ".SequentialIK"),
            SpineLordosis = new(SpineGroup + ".Lordosis");
    }
}
