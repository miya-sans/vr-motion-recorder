// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Bone simulation job (verbatim math), the bone driver and the bone controls
// (control store, rotational locks, chain order; gizmos/events/UI dropped).
// The bone controls are the solver's "virtual body": tracker poses (with calibrated inverse offsets) or, for
// untracked bones, a pose that follows its lock target at the T-pose offset. The rig driver reads them to
// build solver targets.

using System;
using System.Collections.Generic;

namespace VRMR.Fbik
{
    public struct IkBoneSimInput
    {
        public float3 IncomingPosition;
        public quaternion IncomingRotation;
        public float3 InverseOffsetPosition;
        public quaternion InverseOffsetRotation;
        public float3 ScaledOffset;
        public int TargetIndex;
        public byte HasTracker;
        public byte UseInverseOffset;
        public byte HasVirtualOverride;
        public byte HasTarget;
    }

    public struct IkBoneSimState
    {
        public float3 OutgoingPosition;
        public quaternion OutgoingRotation;
        public float3 LastRunPosition;
        public quaternion LastRunRotation;
        public float3 OutgoingWorldPosition;
        public quaternion OutgoingWorldRotation;
        public float3 IKWorldPosition;
        public quaternion IKWorldRotation;
    }

    public struct IkBoneSimChainJob
    {
        public NativeArray<int> ChainIndices;
        public NativeArray<IkBoneSimInput> Inputs;
        public NativeArray<IkBoneSimState> States;
        public float4x4 ParentMatrix;
        public quaternion ParentRotation;
        public float DeltaTime;
        public byte InstantSnap;

        public void Run()
        {
            Execute();
        }

        public void Execute()
        {
            int len = ChainIndices.Length;
            float ts = InstantSnap != 0 ? 1f : math.saturate(IkBoneControl.trackersmooth);
            float posLerpFactor = InstantSnap != 0
                ? 1f
                : IkSmoothingProfiles.FramerateIndependentAlpha(IkBoneControl.PositionLerpAmount, DeltaTime);
            for (int k = 0; k < len; k++)
            {
                int i = ChainIndices[k];
                var input = Inputs[i];
                var state = States[i];
                if (input.HasTracker != 0)
                {
                    if (input.UseInverseOffset != 0)
                    {
                        var destPos = input.IncomingPosition +
                                      math.mul(input.IncomingRotation, input.InverseOffsetPosition);
                        var destRot = math.mul(input.IncomingRotation, input.InverseOffsetRotation);
                        state.OutgoingPosition = math.lerp(state.LastRunPosition, destPos, ts);
                        state.OutgoingRotation = math.slerp(state.LastRunRotation, destRot, ts);
                    }
                    else
                    {
                        state.OutgoingPosition = input.IncomingPosition;
                        state.OutgoingRotation = input.IncomingRotation;
                    }

                    ApplyWorldAndLast(ref state);
                    States[i] = state;
                }
                else if (input.HasVirtualOverride == 0 && input.HasTarget != 0)
                {
                    var targetState = States[input.TargetIndex];
                    state.OutgoingRotation = InstantSnap != 0
                        ? targetState.OutgoingRotation
                        : ApplyLerpToQuaternion(state.LastRunRotation, targetState.OutgoingRotation);
                    var customDirection = math.mul(targetState.OutgoingRotation, input.ScaledOffset);
                    var targetPosition = targetState.OutgoingPosition + customDirection;
                    state.OutgoingPosition = math.lerp(state.LastRunPosition, targetPosition, posLerpFactor);
                    ApplyWorldAndLast(ref state);
                    States[i] = state;
                }
            }
        }

        quaternion ApplyLerpToQuaternion(quaternion current, quaternion future)
        {
            float dot = math.dot(current.value, future.value);
            if (dot > 0.999999f)
                return future;
            float angle = math.acos(math.clamp(dot, -1f, 1f));
            if (angle < math.EPSILON)
                return future;
            float timing = math.min(angle / IkBoneControl.AngleBeforeSpeedup, 1f);
            float lerpAmount = IkBoneControl.QuaternionLerp +
                               (IkBoneControl.QuaternionLerpFastMovement - IkBoneControl.QuaternionLerp) * timing;
            float lerpFactor = IkSmoothingProfiles.FramerateIndependentAlpha(lerpAmount, DeltaTime);
            return math.slerp(current, future, lerpFactor);
        }

        void ApplyWorldAndLast(ref IkBoneSimState state)
        {
            state.LastRunPosition = state.OutgoingPosition;
            state.LastRunRotation = state.OutgoingRotation;
            var p = math.mul(ParentMatrix, new float4(state.OutgoingPosition, 1f));
            state.OutgoingWorldPosition = p.xyz;
            state.OutgoingWorldRotation = math.mul(ParentRotation, state.OutgoingRotation);
        }
    }

    public struct IkBoneWorldDestinationJob
    {
        public NativeArray<IkBoneSimState> States;
        public float4x4 ParentMatrix;
        public quaternion ParentRotation;

        public void Run(int count)
        {
            for (int i = 0; i < count; i++) Execute(i);
        }

        public void Execute(int i)
        {
            var state = States[i];
            var p = math.mul(ParentMatrix, new float4(state.OutgoingPosition, 1f));
            state.OutgoingWorldPosition = p.xyz;
            state.OutgoingWorldRotation = math.mul(ParentRotation, state.OutgoingRotation);
            States[i] = state;
        }
    }

    /// <summary>Port of IkLocalBoneControl: a view into the bone driver's sim input/state store.</summary>
    public sealed class IkBoneControl
    {
        public const float AngleBeforeSpeedup = 25f;
        public const float trackersmooth = 25;
        public const float QuaternionLerp = 14;
        public const float QuaternionLerpFastMovement = 56;
        public const float PositionLerpAmount = 40;
        public bool Exists; // false when the avatar has no bone for this role (fallback position used)
        public bool HasRigLayer; // IkHasRigLayer.HasRigLayer
        public bool HasTracked; // IkHasTracked.HasTracker
        public IkCalibratedCoords IncomingData = IkCalibratedCoords.Identity;
        public int Index;
        public IkCalibratedCoords InverseOffsetFromBone = IkCalibratedCoords.Identity;
        public Vector3 Offset;

        public IkBoneDriver Owner;
        public float RigLayerWeight = 1f;
        public IkBoneTrackedRole Role;
        public int TargetIndex = -1;
        public IkCalibratedCoords TposeLocal = IkCalibratedCoords.Identity;
        public IkCalibratedCoords TposeLocalScaled = IkCalibratedCoords.Identity;
        public bool TrackerFollowsBody;

        ref IkBoneSimInput Input => ref Owner.SimInputs.ElementAt(Index);
        ref IkBoneSimState State => ref Owner.SimStates.ElementAt(Index);
        public bool HasTarget => TargetIndex >= 0;
        public float3 ScaledOffset { get => Input.ScaledOffset; set => Input.ScaledOffset = value; }

        public bool HasVirtualOverride
        {
            get => Input.HasVirtualOverride != 0;
            set => Input.HasVirtualOverride = value ? (byte)1 : (byte)0;
        }

        public bool UseInverseOffset
        {
            get => Input.UseInverseOffset != 0;
            set => Input.UseInverseOffset = value ? (byte)1 : (byte)0;
        }

        public IkCalibratedCoords OutGoingData => new(State.OutgoingPosition, State.OutgoingRotation);
        public IkCalibratedCoords OutgoingWorldData => new(State.OutgoingWorldPosition, State.OutgoingWorldRotation);

        public void SetIncoming(Vector3 position, Quaternion rotation)
        {
            IncomingData = new IkCalibratedCoords(position, rotation);
            Input.IncomingPosition = position;
            Input.IncomingRotation = rotation;
        }

        public void SetOutgoing(Vector3 position, Quaternion rotation)
        {
            State.OutgoingPosition = position;
            State.OutgoingRotation = rotation;
        }

        public void SetInverseOffset(Vector3 position, Quaternion rotation)
        {
            InverseOffsetFromBone = new IkCalibratedCoords(position, rotation);
            Input.InverseOffsetPosition = position;
            Input.InverseOffsetRotation = rotation;
        }

        public void SetTposeLocal(Vector3 position, Quaternion rotation)
        {
            TposeLocal = new IkCalibratedCoords(position, rotation);
        }

        public void SetTposeScaled(Vector3 position, Quaternion rotation)
        {
            TposeLocalScaled = new IkCalibratedCoords(position, rotation);
        }

        public void SetTracked(bool tracked)
        {
            HasTracked = tracked;
            HasRigLayer = tracked;
            Input.HasTracker = tracked ? (byte)1 : (byte)0;
        }
    }

    /// <summary>Port of IkLocalBoneDriver's simulation core (controls, locks, chains).</summary>
    public sealed class IkBoneDriver
    {
        public const int RoleCount = 23;

        static readonly IkBoneTrackedRole[] SpineChainOrder =
        {
            IkBoneTrackedRole.CenterEye, IkBoneTrackedRole.Head, IkBoneTrackedRole.Neck, IkBoneTrackedRole.Chest,
            IkBoneTrackedRole.Spine, IkBoneTrackedRole.Hips, IkBoneTrackedRole.Mouth
        };

        static readonly IkBoneTrackedRole[] LeftArmChainOrder =
        {
            IkBoneTrackedRole.LeftShoulder, IkBoneTrackedRole.LeftUpperArm, IkBoneTrackedRole.LeftLowerArm,
            IkBoneTrackedRole.LeftHand
        };

        static readonly IkBoneTrackedRole[] RightArmChainOrder =
        {
            IkBoneTrackedRole.RightShoulder, IkBoneTrackedRole.RightUpperArm, IkBoneTrackedRole.RightLowerArm,
            IkBoneTrackedRole.RightHand
        };

        static readonly IkBoneTrackedRole[] LeftLegChainOrder =
        {
            IkBoneTrackedRole.LeftUpperLeg, IkBoneTrackedRole.LeftLowerLeg, IkBoneTrackedRole.LeftFoot,
            IkBoneTrackedRole.LeftToes
        };

        static readonly IkBoneTrackedRole[] RightLegChainOrder =
        {
            IkBoneTrackedRole.RightUpperLeg, IkBoneTrackedRole.RightLowerLeg, IkBoneTrackedRole.RightFoot,
            IkBoneTrackedRole.RightToes
        };

        public IkBoneControl[] Controls;
        public int ControlsLength;
        public IkBoneControl LeftUpperArmControl, RightUpperArmControl;

        public IkBoneControl LeftUpperLegControl,
            RightUpperLegControl,
            LeftLowerLegControl,
            RightLowerLegControl,
            LeftLowerArmControl,
            RightLowerArmControl,
            LeftToeControl,
            RightToeControl,
            LeftShoulderControl,
            RightShoulderControl;

        public IkBoneControl NeckControl,
            HeadControl,
            SpineControl,
            HipsControl,
            EyeControl,
            MouthControl,
            LeftFootControl,
            RightFootControl,
            LeftHandControl,
            RightHandControl,
            ChestControl;

        public NativeArray<IkBoneSimInput> SimInputs;
        public NativeArray<IkBoneSimState> SimStates;
        NativeArray<int> allChainIndices;
        bool chainsBuilt;
        public IkBoneTrackedRole[] trackedRoles;

        public IkBoneDriver()
        {
            ControlsLength = RoleCount;
            Controls = new IkBoneControl[RoleCount];
            trackedRoles = new IkBoneTrackedRole[RoleCount];
            SimInputs = new NativeArray<IkBoneSimInput>(RoleCount, Allocator.Persistent);
            SimStates = new NativeArray<IkBoneSimState>(RoleCount, Allocator.Persistent);
            for (int i = 0; i < RoleCount; i++)
            {
                trackedRoles[i] = (IkBoneTrackedRole)i;
                Controls[i] = new IkBoneControl { Owner = this, Index = i, Role = (IkBoneTrackedRole)i };
                var inp = SimInputs[i];
                inp.IncomingRotation = quaternion.identity;
                inp.InverseOffsetRotation = quaternion.identity;
                inp.TargetIndex = -1;
                SimInputs[i] = inp;
                var st = SimStates[i];
                st.OutgoingRotation = quaternion.identity;
                st.LastRunRotation = quaternion.identity;
                st.OutgoingWorldRotation = quaternion.identity;
                SimStates[i] = st;
            }

            EyeControl = Find(IkBoneTrackedRole.CenterEye);
            SpineControl = Find(IkBoneTrackedRole.Spine);
            NeckControl = Find(IkBoneTrackedRole.Neck);
            HeadControl = Find(IkBoneTrackedRole.Head);
            HipsControl = Find(IkBoneTrackedRole.Hips);
            MouthControl = Find(IkBoneTrackedRole.Mouth);
            LeftFootControl = Find(IkBoneTrackedRole.LeftFoot);
            RightFootControl = Find(IkBoneTrackedRole.RightFoot);
            LeftHandControl = Find(IkBoneTrackedRole.LeftHand);
            RightHandControl = Find(IkBoneTrackedRole.RightHand);
            ChestControl = Find(IkBoneTrackedRole.Chest);
            LeftUpperLegControl = Find(IkBoneTrackedRole.LeftUpperLeg);
            RightUpperLegControl = Find(IkBoneTrackedRole.RightUpperLeg);
            LeftLowerLegControl = Find(IkBoneTrackedRole.LeftLowerLeg);
            RightLowerLegControl = Find(IkBoneTrackedRole.RightLowerLeg);
            LeftLowerArmControl = Find(IkBoneTrackedRole.LeftLowerArm);
            RightLowerArmControl = Find(IkBoneTrackedRole.RightLowerArm);
            LeftToeControl = Find(IkBoneTrackedRole.LeftToes);
            RightToeControl = Find(IkBoneTrackedRole.RightToes);
            LeftShoulderControl = Find(IkBoneTrackedRole.LeftShoulder);
            RightShoulderControl = Find(IkBoneTrackedRole.RightShoulder);
            LeftUpperArmControl = Find(IkBoneTrackedRole.LeftUpperArm);
            RightUpperArmControl = Find(IkBoneTrackedRole.RightUpperArm);
        }

        public IkBoneControl Find(IkBoneTrackedRole role)
        {
            return Controls[(int)role];
        }

        public bool FindBone(out IkBoneControl control, IkBoneTrackedRole role)
        {
            control = Controls[(int)role];
            return true;
        }

        public void CreateRotationalLock(IkBoneControl addToBone, IkBoneControl target)
        {
            addToBone.TargetIndex = target.Index;
            addToBone.Offset = addToBone.TposeLocalScaled.position - target.TposeLocalScaled.position;
            addToBone.ScaledOffset = addToBone.Offset;
            chainsBuilt = false;
        }

        public void SetAndCreateLock(IkBoneTrackedRole lockToBoneRole, IkBoneTrackedRole assignedTo)
        {
            CreateRotationalLock(Find(assignedTo), Find(lockToBoneRole));
        }

        void BuildChainData()
        {
            for (int i = 0; i < ControlsLength; i++)
            {
                var c = Controls[i];
                int targetIdx = c.TargetIndex;
                var inp = SimInputs[i];
                inp.TargetIndex = targetIdx;
                inp.HasTarget = targetIdx >= 0 ? (byte)1 : (byte)0;
                SimInputs[i] = inp;
            }

            var covered = new HashSet<int>();
            var ordered = new List<int>(ControlsLength);
            AppendChainIndices(SpineChainOrder, covered, ordered);
            AppendChainIndices(LeftArmChainOrder, covered, ordered);
            AppendChainIndices(RightArmChainOrder, covered, ordered);
            AppendChainIndices(LeftLegChainOrder, covered, ordered);
            AppendChainIndices(RightLegChainOrder, covered, ordered);
            for (int i = 0; i < ControlsLength; i++)
            {
                if (!covered.Contains(i))
                {
                    ordered.Add(i);
                }
            }

            allChainIndices = new NativeArray<int>(ordered.ToArray(), Allocator.Persistent);
            chainsBuilt = true;
        }

        void AppendChainIndices(IkBoneTrackedRole[] order, HashSet<int> covered, List<int> ordered)
        {
            for (int i = 0; i < order.Length; i++)
            {
                int idx = Array.IndexOf(trackedRoles, order[i]);
                if (idx >= 0 && idx < ControlsLength && covered.Add(idx))
                {
                    ordered.Add(idx);
                }
            }
        }

        void SeedLastRunFromOutgoing()
        {
            for (int i = 0; i < ControlsLength; i++)
            {
                var s = SimStates[i];
                s.LastRunPosition = s.OutgoingPosition;
                s.LastRunRotation = s.OutgoingRotation;
                SimStates[i] = s;
            }
        }

        public void Simulate(float deltaTime, Matrix4x4 parentMatrix)
        {
            RunSimulation(parentMatrix, deltaTime, false, false);
        }

        public void SimulateWithoutLerp(Matrix4x4 parentMatrix, float deltaTime)
        {
            RunSimulation(parentMatrix, deltaTime, true, true);
        }

        void RunSimulation(Matrix4x4 parentMatrix, float deltaTime, bool seedLastRunFromOutgoing, bool instantSnap)
        {
            if (!chainsBuilt)
            {
                BuildChainData();
            }

            if (seedLastRunFromOutgoing)
            {
                SeedLastRunFromOutgoing();
            }

            float4x4 parentMatrix44 = parentMatrix;
            quaternion parentRot = parentMatrix.rotation;
            byte snap = instantSnap ? (byte)1 : (byte)0;
            if (allChainIndices.IsCreated && allChainIndices.Length > 0)
            {
                new IkBoneSimChainJob
                {
                    ChainIndices = allChainIndices,
                    Inputs = SimInputs,
                    States = SimStates,
                    ParentMatrix = parentMatrix44,
                    ParentRotation = parentRot,
                    DeltaTime = deltaTime,
                    InstantSnap = snap
                }.Run();
            }

            new IkBoneWorldDestinationJob
                { States = SimStates, ParentMatrix = parentMatrix44, ParentRotation = parentRot }.Run(ControlsLength);
        }
    }
}
