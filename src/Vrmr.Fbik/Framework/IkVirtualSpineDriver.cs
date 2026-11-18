// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Static framework reads (IkLocalBoneDriver.*Control, IkHeightDriver, IkSettingsDefaults,
// Time.deltaTime, IkLocalPlayer) are replaced by the owning FbikRig context and an explicit dt.

namespace VRMR.Fbik
{
    public sealed class IkVirtualSpineDriver
    {
        readonly IkNodPivotSampler nodPivotSampler = new();
        readonly FbikRig rig;
        public bool HipsFreezeToTpose = false;
        float chestRestAlong, spineRestAlong;
        float3 gazeSwingLever;
        float3 hipsFromEyeTposeXZ, headFromEyeTposeXZ, yawPivotFromEyeTposeXZ, eyeFromHeadTpose, hipsFromHeadTpose;
        bool initialized;

        float lenNeckToChest,
            lenChestToSpine,
            lenSpineToHips,
            lenSpineTotal,
            chestTransform,
            spineTransform,
            restHipsLocalY;

        bool lengthsDirty = true;
        float3 restChordDir, chestRestPerp, spineRestPerp;
#pragma warning disable CS0649 // hipsRestDropY is never assigned upstream either (always 0)
        float restHeadLocalY, hipsRestDropY;
#pragma warning restore CS0649
        NativeArray<IkVirtualSpineCore.SpineSolveState> solveState;
        float tposeNeckMinusEyeY;
        public IkVirtualSpineDriver(FbikRig rig) { this.rig = rig; }

        public void Initialize()
        {
            if (initialized) return;
            var b = rig.Bones;
            b.HeadControl.HasVirtualOverride = true;
            b.NeckControl.HasVirtualOverride = true;
            b.ChestControl.HasVirtualOverride = true;
            b.SpineControl.HasVirtualOverride = true;
            b.HipsControl.HasVirtualOverride = true;
            solveState = new NativeArray<IkVirtualSpineCore.SpineSolveState>(1, Allocator.Persistent);
            solveState[0] = default;
            lengthsDirty = true;
            initialized = true;
        }

        public void OnHeightChanged()
        {
            lengthsDirty = true;
            nodPivotSampler.Reset();
            gazeSwingLever = float3.zero;
            if (solveState.IsCreated)
            {
                var s = solveState[0];
                s.HeadBaselineInitialized = 0;
                solveState[0] = s;
            }
        }

        public void Simulate(float deltaTime)
        {
            if (!initialized) return;
            var bones = rig.Bones;
            var set = rig.Settings;
            var eye = bones.EyeControl;
            var head = bones.HeadControl;
            var neck = bones.NeckControl;
            var chest = bones.ChestControl;
            var spine = bones.SpineControl;
            var hips = bones.HipsControl;
            IkCalibratedCoords freshEye;
            if (eye.HasTracked)
            {
                freshEye = eye.IncomingData;
                if (eye.UseInverseOffset)
                {
                    var off = eye.InverseOffsetFromBone;
                    freshEye.position += freshEye.rotation * off.position;
                    freshEye.rotation *= off.rotation;
                }
            }
            else
            {
                freshEye = eye.OutGoingData;
            }

            if (lengthsDirty)
            {
                RecomputeSegmentLengths(eye, head, neck, chest, spine, hips);
                gazeSwingLever = eyeFromHeadTpose;
                lengthsDirty = false;
            }

            if (rig.IsVR && set.VSpineNodPivotEstimate)
            {
                var pivotSettings = IkNodPivotEstimatorCore.Defaults();
                pivotSettings.Scale = rig.AvatarToDefaultRatioScaledWithAvatarScale;
                gazeSwingLever = nodPivotSampler.Update(freshEye.position, freshEye.rotation, deltaTime,
                    eyeFromHeadTpose, in pivotSettings);
            }
            else
            {
                gazeSwingLever = eyeFromHeadTpose;
            }

            var simStates = bones.SimStates;
            var parentMatrix = rig.localToWorldMatrix;
            bool isVR = rig.IsVR;
            float torsoYawDeadzoneDeg = set.VSpineTorsoYawDeadzoneDeg;
            if (rig.IsProne)
            {
                torsoYawDeadzoneDeg = 0f;
            }

            var leftFoot = bones.LeftFootControl;
            var rightFoot = bones.RightFootControl;
            bool headPinnedHips = TrackerOwned(hips) == 0 && (TrackerOwned(chest) == 0 || chest.TrackerFollowsBody) &&
                                  !rig.IsProne;
            bool leftFootTracked = leftFoot.HasTracked;
            bool rightFootTracked = rightFoot.HasTracked;
            var p = new IkVirtualSpineCore.SpineSolveParams
            {
                Dt = deltaTime,
                Scale = rig.AvatarToDefaultRatioScaledWithAvatarScale,
                TrackingLiftY = rig.PlayspaceVerticalOffset * rig.DeviceScale,
                ParentMatrix = parentMatrix,
                ParentRotation = parentMatrix.rotation,
                EyeRot = freshEye.rotation,
                HeadTargetPos = ResolveTargetPos(head, eye, in freshEye),
                HeadTargetRot = ResolveTargetRot(head, eye, in freshEye),
                NeckTargetPos = ResolveTargetPos(neck, eye, in freshEye),
                NeckTargetRot = ResolveTargetRot(neck, eye, in freshEye),
                ChestTargetPos = ResolveTargetPos(chest, eye, in freshEye),
                ChestTargetRot = ResolveTargetRot(chest, eye, in freshEye),
                SpineTargetPos = ResolveTargetPos(spine, eye, in freshEye),
                SpineTargetRot = ResolveTargetRot(spine, eye, in freshEye),
                HeadScaledOffset = head.ScaledOffset,
                NeckScaledOffset = neck.ScaledOffset,
                ChestScaledOffset = chest.ScaledOffset,
                SpineScaledOffset = spine.ScaledOffset,
                ChestTposeY = chest.TposeLocalScaled.position.y,
                SpineTposeY = spine.TposeLocalScaled.position.y,
                TposeHips = hips.TposeLocalScaled.position,
                LeftFootPos = leftFootTracked ? leftFoot.OutGoingData.position : float3.zero,
                RightFootPos = rightFootTracked ? rightFoot.OutGoingData.position : float3.zero,
                LeftFootTracked = (byte)(leftFootTracked ? 1 : 0),
                RightFootTracked = (byte)(rightFootTracked ? 1 : 0),
                ChestPitchFrac = set.VSpineChestPitchFrac,
                ChestRollFrac = set.VSpineChestRollFrac,
                SpinePitchFrac = set.VSpineSpinePitchFrac,
                SpineRollFrac = set.VSpineSpineRollFrac,
                NeckRotationSpeed = set.VSpineNeckRotationSpeed,
                ChestRotationSpeed = set.VSpineChestRotationSpeed,
                SpineRotationSpeed = set.VSpineSpineRotationSpeed,
                HipsRotationSpeed = set.VSpineHipsRotationSpeed,
                HipsForwardBias = set.VSpineHipsForwardBias,
                NeckExtensionDamp = set.FBIKNeckExtensionDamp,
                NeckFlexionDamp = set.FBIKNeckFlexionDamp,
                TorsoYawDeadzoneDeg = torsoYawDeadzoneDeg,
                TorsoYawBlendSpeed = set.VSpineTorsoYawBlendSpeed,
                HipsFreeze = (byte)(HipsFreezeToTpose ? 1 : 0),
                IsLocomoting = (byte)(rig.IsLocomoting ? 1 : 0),
                LenTotal = lenSpineTotal,
                TChest = chestTransform,
                TSpine = spineTransform,
                StandingHipsLocalY = restHipsLocalY,
                StandingHeadLocalY = restHeadLocalY,
                EyePos = freshEye.position,
                GazeSwingLever = gazeSwingLever,
                TposeNeckMinusEyeY = tposeNeckMinusEyeY,
                GazeSwingRemoval = set.VSpineGazeSwingRemoval,
                HipsAnchorOffsetLocal = hipsFromEyeTposeXZ,
                HeadRestFromEyeLocal = headFromEyeTposeXZ,
                YawPivotFromEyeLocal = isVR ? yawPivotFromEyeTposeXZ : float3.zero,
                PostureModel = (byte)(set.VSpinePostureModel ? 1 : 0),
                HipsCompressionStrength = set.VSpineHipsCompressionStrength,
                HipsMaxDropMeters = set.VSpineHipsMaxDropMeters * rig.AvatarToDefaultRatioScaledWithAvatarScale,
                HipsRestDropY = hipsRestDropY,
                RestChordDir = restChordDir,
                ChestRestAlong = chestRestAlong,
                ChestRestPerp = chestRestPerp,
                SpineRestAlong = spineRestAlong,
                SpineRestPerp = spineRestPerp,
                HeadPinnedHips = (byte)(headPinnedHips ? 1 : 0),
                HipsFromHeadRest = hipsFromHeadTpose
            };
            new IkVirtualSpineCore.IkVirtualSpineSolveJob
            {
                States = simStates,
                State = solveState,
                P = p,
                IdxHead = head.Index,
                IdxNeck = neck.Index,
                IdxChest = chest.Index,
                IdxSpine = spine.Index,
                IdxHips = hips.Index,
                SkipHead = TrackerOwned(head),
                SkipNeck = TrackerOwned(neck),
                SkipChest = TrackerOwned(chest),
                SkipSpine = TrackerOwned(spine),
                SkipHips = TrackerOwned(hips)
            }.Run();
        }

        static byte TrackerOwned(IkBoneControl c)
        {
            return (byte)(c.HasTracked ? 1 : 0);
        }

        static float3 ResolveTargetPos(IkBoneControl c, IkBoneControl eye, in IkCalibratedCoords freshEye)
        {
            var target = ResolveTarget(c);
            return ReferenceEquals(target, eye) ? freshEye.position : (float3)target.OutGoingData.position;
        }

        static quaternion ResolveTargetRot(IkBoneControl c, IkBoneControl eye, in IkCalibratedCoords freshEye)
        {
            var target = ResolveTarget(c);
            return ReferenceEquals(target, eye) ? freshEye.rotation : (quaternion)target.OutGoingData.rotation;
        }

        static IkBoneControl ResolveTarget(IkBoneControl c)
        {
            return c.TargetIndex >= 0 ? c.Owner.Controls[c.TargetIndex] : c;
        }

        public static void RestOffsetFromChord(float3 bone, float3 hips, float3 chord, float chordLenSq,
            out float along, out float3 perp)
        {
            var d = bone - hips;
            along = chordLenSq > 1e-10f ? math.dot(d, chord) / chordLenSq : 0f;
            perp = d - chord * along;
        }

        void RecomputeSegmentLengths(IkBoneControl eye, IkBoneControl head, IkBoneControl neck, IkBoneControl chest,
            IkBoneControl spine, IkBoneControl hips)
        {
            float3 pHead = head.TposeLocalScaled.position;
            float3 pNeck = neck.TposeLocalScaled.position;
            float3 pChest = chest.TposeLocalScaled.position;
            float3 pSpine = spine.TposeLocalScaled.position;
            float3 pHips = hips.TposeLocalScaled.position;
            lenNeckToChest = math.distance(pNeck, pChest);
            lenChestToSpine = math.distance(pChest, pSpine);
            lenSpineToHips = math.distance(pSpine, pHips);
            lenSpineTotal = math.max(1e-4f, lenNeckToChest + lenChestToSpine + lenSpineToHips);
            chestTransform = math.saturate(lenNeckToChest / lenSpineTotal);
            spineTransform = math.saturate((lenNeckToChest + lenChestToSpine) / lenSpineTotal);
            restHipsLocalY = pNeck.y - lenSpineTotal;
            var restChord = pNeck - pHips;
            float restChordLenSq = math.lengthsq(restChord);
            restChordDir = restChordLenSq > 1e-10f ? restChord * math.rsqrt(restChordLenSq) : new float3(0f, 1f, 0f);
            RestOffsetFromChord(pChest, pHips, restChord, restChordLenSq, out chestRestAlong, out chestRestPerp);
            RestOffsetFromChord(pSpine, pHips, restChord, restChordLenSq, out spineRestAlong, out spineRestPerp);
            restHeadLocalY = math.max(pHead.y, 1e-3f);
            float3 pEye = eye.TposeLocalScaled.position;
            eyeFromHeadTpose = pEye - pHead;
            hipsFromHeadTpose = pHips - pHead;
            hipsFromEyeTposeXZ = new float3(pHips.x - pEye.x, 0f, pHips.z - pEye.z);
            headFromEyeTposeXZ = new float3(pHead.x - pEye.x, 0f, pHead.z - pEye.z);
            yawPivotFromEyeTposeXZ = new float3(pNeck.x - pEye.x, 0f, pNeck.z - pEye.z);
            tposeNeckMinusEyeY = pNeck.y - pEye.y;
        }
    }
}
