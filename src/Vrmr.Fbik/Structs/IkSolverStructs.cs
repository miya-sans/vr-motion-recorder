// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.
using System.Runtime.CompilerServices;
using System;
namespace VRMR.Fbik
{
    public struct IkArmState
    {
        public float SwivelDeg, SwitchTimer, PriorDeg, RawDeg, ReachRatio, ElbowDeg, HumeralDeg, PronationDeg, WristFlexDeg, WristDevDeg, Cost, ShoulderBlend;
        public Vector3 LastTarget, LastAxis, ElbowDir, PriorDir, HintPosition, ConstrainedHintPosition;
        public Quaternion ShoulderHold;
        public bool Seeded, Switched, ShoulderHeld;
    }
    public struct IkLegSlotState
    {
        public Vector3 KneeDir, PoleDir, ConstrainedHintPosition;
    }
    public struct IkChestSpringState
    {
        public Vector3 Pos, Vel;
        public bool Seeded;
    }
    public struct IkGizmoDraw
    {
        public Vector3 A, B;
        public uint Color;
        public float Size;
        public byte Stage;
        public IkGizmoKind Kind;
    }
    public struct IkGizmoLabel
    {
        public Vector3 Position;
        public uint Color;
        public byte Stage;
        public FixedString64Bytes Text;
    }
}
