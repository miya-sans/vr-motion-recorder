// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.
namespace VRMR.Fbik
{
    public static class IkBodyFitCore
    {
        public const float DefaultMaxDeviation = 0.1f, MaxDeviationCeiling = 0.1f;
        const float minSegmentMeters = 0.05f, minRatio = 0.5f, maxRatio = 2f, minHipFraction = 0.35f;
        const float maxHipFraction = 0.75f;
        public static string Describe(IkBodyFitStatus status) => status switch
        {
            IkBodyFitStatus.Fitted => "fitted",
            IkBodyFitStatus.Disabled => "turned off",
            IkBodyFitStatus.PlayerEyeHeightMissing => $"your eye height has not been measured (needs more than {minSegmentMeters:F2} m)",
            IkBodyFitStatus.AvatarEyeHeightMissing => $"the avatar's eye height is unreadable (needs more than {minSegmentMeters:F2} m)",
            IkBodyFitStatus.PlayerArmSpanMissing => "your arm span is unknown because your height has not been measured yet — calibrate",
            IkBodyFitStatus.AvatarArmSpanMissing => "the avatar's arm span is unreadable — its hand bones may be missing",
            IkBodyFitStatus.ArmLengthDegenerate => "the avatar's shoulders are as wide as its arm span, leaving no arm to resize",
            IkBodyFitStatus.ArmRatioOutOfBand => $"your arms differ from the avatar's by more than the sane band ({minRatio:F1}x to {maxRatio:F1}x) — likely a bad calibration frame",
            IkBodyFitStatus.HipsTrackerMissing => "no hips tracker, so your leg-to-torso split is unknown",
            IkBodyFitStatus.HipHeightImplausible => $"your hips tracker is not sitting at a hip height (expected {minHipFraction:P0} to {maxHipFraction:P0} of your eye height) — it may be assigned to the wrong body part",
            IkBodyFitStatus.AvatarHipHeightMissing => "the avatar's hip bone height is unreadable",
            IkBodyFitStatus.AvatarLegSpanDegenerate => "the avatar has no measurable thigh-to-ankle length to resize",
            IkBodyFitStatus.AvatarSpineSpanDegenerate => "the avatar has no measurable hips-to-shoulders length to resize",
            IkBodyFitStatus.HipRatioOutOfBand => $"your hip height differs from the avatar's by more than the sane band ({minRatio:F1}x to {maxRatio:F1}x) — likely a bad calibration frame",
            _ => "unknown",
        };
        public static float ArmSpanSlack(in IkBodyFitMeasurements m, float maxDeviation)
        {
            float deviation = Mathf.Clamp(maxDeviation, 0f, MaxDeviationCeiling);
            float armOnly = m.AvatarArmSpan - Mathf.Max(0f, m.AvatarShoulderWidth);
            return armOnly > 0f ? deviation * armOnly : 0f;
        }
        public static IkBodyFitResult Solve(in IkBodyFitMeasurements m, float maxDeviation)
        {
            IkBodyFitResult result = IkBodyFitResult.Identity;
            float deviation = Mathf.Clamp(maxDeviation, 0f, MaxDeviationCeiling);
            if (deviation <= 0f)
            {
                return result;
            }

            if (!Plausible(m.PlayerEyeHeight))
            {
                result.ArmStatus = IkBodyFitStatus.PlayerEyeHeightMissing;
                return result;
            }

            if (!Plausible(m.AvatarEyeHeight))
            {
                result.ArmStatus = IkBodyFitStatus.AvatarEyeHeightMissing;
                return result;
            }

            float toAvatarSpace = m.UniformScale > 0f && !float.IsNaN(m.UniformScale) && !float.IsInfinity(m.UniformScale) ? m.UniformScale : m.AvatarEyeHeight / m.PlayerEyeHeight;

            SolveArms(in m, toAvatarSpace, deviation, ref result);

            return result;
        }
        static void SolveArms(in IkBodyFitMeasurements m, float toAvatarSpace, float deviation, ref IkBodyFitResult result)
        {
            if (!Plausible(m.PlayerArmSpan))
            {
                result.ArmStatus = IkBodyFitStatus.PlayerArmSpanMissing;
                return;
            }

            if (!Plausible(m.AvatarArmSpan))
            {
                result.ArmStatus = IkBodyFitStatus.AvatarArmSpanMissing;
                return;
            }

            float shoulderWidth = Mathf.Max(0f, m.AvatarShoulderWidth);
            float avatarArm = (m.AvatarArmSpan - shoulderWidth) * 0.5f;
            float playerArm = (m.PlayerArmSpan * toAvatarSpace - shoulderWidth) * 0.5f;

            if (!Plausible(avatarArm) || !Plausible(playerArm))
            {
                result.ArmStatus = IkBodyFitStatus.ArmLengthDegenerate;
                return;
            }

            float ratio = playerArm / avatarArm;
            if (!InRatioBand(ratio))
            {
                result.ArmStatus = IkBodyFitStatus.ArmRatioOutOfBand;
                return;
            }

            result.ArmScale = Mathf.Clamp(ratio, 1f - deviation, 1f + deviation);
            result.ArmStatus = IkBodyFitStatus.Fitted;
        }
        static bool Plausible(float value) => value > minSegmentMeters && !float.IsNaN(value) && !float.IsInfinity(value);
        static bool InRatioBand(float ratio) => ratio >= minRatio && ratio <= maxRatio;
    }
    public static class IkBodyEvidenceCore
    {
        public const int Capacity = 8, OutlierRejection = 2, MinSamplesForConfidence = 24;
        public const int SamplesForFullConfidence = 120;
        public const float MaxEyeSettleSpeed = 0.35f, MaxSpanSettleSpeed = 0.8f, DifferentPersonDrop = 0.12f;
        public const int DifferentPersonStreak = 900;
        public static void Reset(ref IkBodyEvidenceState state)
        {
            state = default;
        }
        public static void Fold( ref IkBodyEvidenceState state, in IkBodyEvidenceSample sample, bool hasFloor, float floorY, float minPlausible, float maxPlausible)
        {
            if (sample.HeadValid)
            {
                float eye = hasFloor ? sample.HeadY - floorY : sample.HeadY - sample.InjectedVerticalOffset;
                FoldOne(ref state.Eye, eye, sample.DeltaSeconds, MaxEyeSettleSpeed, minPlausible, maxPlausible);
            }

            if (sample.HandsValid)
            {
                FoldOne(ref state.ArmSpan, sample.HandSpan, sample.DeltaSeconds, MaxSpanSettleSpeed, minPlausible, maxPlausible);
            }
        }
        static void FoldOne(ref IkBodyEvidenceTrack track, float value, float deltaSeconds, float maxSpeed, float minPlausible, float maxPlausible)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f || value > maxPlausible)
            {
                track.HasPrevious = false;
                return;
            }

            bool settled = true;
            if (track.HasPrevious && deltaSeconds > 0f)
            {
                settled = Mathf.Abs(value - track.Previous) / deltaSeconds <= maxSpeed;
            }
            track.Previous = value;
            track.HasPrevious = true;

            if (!settled)
            {
                return;
            }

            track.SampleCount++;

            if (TryGetEstimate(track, out float onRecord, out _))
            {
                if (value < onRecord * (1f - DifferentPersonDrop))
                {
                    if (track.LowStreak < int.MaxValue) track.LowStreak++;
                }
                else
                {
                    track.LowStreak = 0;
                }
            }

            if (value < minPlausible)
            {
                return;
            }
            InsertDescending(ref track.Top, value);
        }
        public static bool LooksLikeADifferentPerson(in IkBodyEvidenceTrack track)
        {
            return track.LowStreak >= DifferentPersonStreak;
        }
        static void InsertDescending(ref FixedList64Bytes<float> top, float value)
        {
            int length = top.Length, index = length;
            for (int i = 0; i < length; i++)
            {
                if (value > top[i])
                {
                    index = i;
                    break;
                }
            }

            if (index >= Capacity)
            {
                return;
            }

            if (length < Capacity)
            {
                top.Add(value);
            }

            for (int i = top.Length - 1; i > index; i--)
            {
                top[i] = top[i - 1];
            }
            top[index] = value;
        }
        public static bool TryGetEstimate(in IkBodyEvidenceTrack track, out float estimate, out float confidence)
        {
            estimate = 0f;
            confidence = 0f;
            int length = track.Top.Length;
            if (length == 0 || track.SampleCount < MinSamplesForConfidence)
            {
                return false;
            }

            int index = OutlierRejection < length ? OutlierRejection : length - 1;
            estimate = track.Top[index];

            float bySamples = Mathf.InverseLerp(MinSamplesForConfidence, SamplesForFullConfidence, track.SampleCount);
            float byDepth = Mathf.Clamp01((float)length / Capacity);
            confidence = Mathf.Clamp01(Mathf.Min(bySamples, byDepth));
            return estimate > 0f;
        }
        public static bool TryEstimateFloor( in FixedList128Bytes<float> trackerHeights, float headY, float footMountAllowance, float footBand, int minFootBandTrackers, float minPlausible, float maxPlausible, out float floorY)
        {
            floorY = 0f;
            int count = trackerHeights.Length;
            if (count < minFootBandTrackers)
            {
                return false;
            }

            float lowest = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (trackerHeights[i] < lowest) lowest = trackerHeights[i];
            }

            int inFootBand = 0;
            for (int i = 0; i < count; i++)
            {
                if (trackerHeights[i] <= lowest + footBand) inFootBand++;
            }
            if (inFootBand < minFootBandTrackers)
            {
                return false;
            }

            floorY = lowest - footMountAllowance;
            float impliedEye = headY - floorY;
            return impliedEye >= minPlausible && impliedEye <= maxPlausible;
        }
    }
    public static class IkScaleFitCore
    {
        public const float MinMeasureMeters = 0.05f, MinRatio = 0.1f, MaxRatio = 10f, DefaultMaxEyeDeviation = 0.05f;
        public const float EyeWeight = 1f, ArmSpanWeight = 0.7f, HipWeight = 0.4f;
        public static IkScaleFitResult Solve(in IkScaleFitInput input)
        {
            IkScaleFitResult result = IkScaleFitResult.Invalid;
            bool hasEye = TryRatio(input.Eye, out float eyeRatio), hasArm = TryRatio(input.ArmSpan, out float armRatio);
            bool hasHip = TryRatio(input.HipHeight, out float hipRatio);
            int used = (hasEye ? 1 : 0) + (hasArm ? 1 : 0) + (hasHip ? 1 : 0);
            if (used == 0)
            {
                return result;
            }
            result.UsedCount = used;

            float lo = float.NegativeInfinity, hi = float.PositiveInfinity;
            AccumulateBand(hasArm, input.ArmSpan, ref lo, ref hi);
            AccumulateBand(hasHip, input.HipHeight, ref lo, ref hi);
            bool feasible = lo <= hi;
            float logScale;
            if (hasEye)
            {
                float logEye = Mathf.Log(eyeRatio);
                if (feasible)
                {
                    logScale = Mathf.Clamp(logEye, lo, hi);

                    result.Status = logScale == logEye ? IkScaleFitStatus.EyeExact : IkScaleFitStatus.Adjusted;
                }
                else
                {
                    logScale = WeightedLogMean(in input, hasEye, eyeRatio, hasArm, armRatio, hasHip, hipRatio);
                    result.Status = IkScaleFitStatus.Compromised;
                }

                float maxDeviation = input.MaxEyeDeviation > 0f ? input.MaxEyeDeviation : DefaultMaxEyeDeviation;
                float logBudget = Mathf.Log(1f + maxDeviation);
                float clamped = Mathf.Clamp(logScale, logEye - logBudget, logEye + logBudget);
                if (clamped != logScale)
                {
                    logScale = clamped;
                    result.Status = IkScaleFitStatus.Compromised;
                }
            }
            else if (feasible && !float.IsInfinity(lo) && !float.IsInfinity(hi))
            {
                logScale = (lo + hi) * 0.5f;
                result.Status = IkScaleFitStatus.Adjusted;
            }
            else
            {
                logScale = WeightedLogMean(in input, hasEye, eyeRatio, hasArm, armRatio, hasHip, hipRatio);
                result.Status = feasible ? IkScaleFitStatus.Adjusted : IkScaleFitStatus.Compromised;
            }

            float scale = Mathf.Exp(logScale);
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f)
            {
                return IkScaleFitResult.Invalid;
            }

            result.Scale = scale;
            result.EyeResidual = hasEye ? eyeRatio / scale : 1f;
            result.ArmResidual = hasArm ? armRatio / scale : 1f;
            result.HipResidual = hasHip ? hipRatio / scale : 1f;
            return result;
        }
        static void AccumulateBand(bool has, in IkScaleFitSample sample, ref float lo, ref float hi)
        {
            if (!has)
            {
                return;
            }
            float slack = Mathf.Max(0f, sample.Slack), lowTarget = sample.Avatar - slack;
            if (lowTarget < MinMeasureMeters)
            {
                lowTarget = MinMeasureMeters;
            }
            float low = Mathf.Log(lowTarget / sample.Player), high = Mathf.Log((sample.Avatar + slack) / sample.Player);
            if (low > lo) lo = low;
            if (high < hi) hi = high;
        }
        static float WeightedLogMean( in IkScaleFitInput input, bool hasEye, float eyeRatio, bool hasArm, float armRatio, bool hasHip, float hipRatio)
        {
            float sum = 0f, weights = 0f;
            Accumulate(hasEye, eyeRatio, input.Eye.Weight, ref sum, ref weights);
            Accumulate(hasArm, armRatio, input.ArmSpan.Weight, ref sum, ref weights);
            Accumulate(hasHip, hipRatio, input.HipHeight.Weight, ref sum, ref weights);
            return weights > 0f ? sum / weights : 0f;
        }
        static void Accumulate(bool has, float ratio, float weight, ref float sum, ref float weights)
        {
            if (!has)
            {
                return;
            }
            float w = Mathf.Max(0f, weight);
            if (w <= 0f)
            {
                return;
            }
            sum += w * Mathf.Log(ratio);
            weights += w;
        }
        static bool TryRatio(in IkScaleFitSample sample, out float ratio)
        {
            ratio = 1f;
            if (!Usable(sample.Player) || !Usable(sample.Avatar) || sample.Weight <= 0f)
            {
                return false;
            }
            float r = sample.Avatar / sample.Player;
            if (float.IsNaN(r) || float.IsInfinity(r) || r < MinRatio || r > MaxRatio)
            {
                return false;
            }
            ratio = r;
            return true;
        }
        static bool Usable(float value) => value > MinMeasureMeters && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
