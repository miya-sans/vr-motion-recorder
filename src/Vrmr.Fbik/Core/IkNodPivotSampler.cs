// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.

namespace VRMR.Fbik
{
    public sealed class IkNodPivotSampler
    {
        readonly float3[] samplePositions;
        readonly quaternion[] sampleRotations;
        public IkNodPivotResult LastResult;
        public float SampleIntervalSeconds = 1f / 30f, SolveIntervalSeconds = 0.25f, BlendPerAcceptance = 0.15f;
        float3 armEstimate;
        int filled, write;
        float sampleAccum, solveAccum;

        public IkNodPivotSampler(int capacity = 30)
        {
            if (capacity < 4) capacity = 4;
            samplePositions = new float3[capacity];
            sampleRotations = new quaternion[capacity];
        }

        public bool HasEstimate { get; private set; }

        public void Reset()
        {
            filled = 0;
            write = 0;
            sampleAccum = 0f;
            solveAccum = 0f;
            HasEstimate = false;
            armEstimate = default;
            LastResult = default;
        }

        public float3 Update(float3 eyePos, quaternion eyeRot, float dt, float3 priorArm,
            in IkNodPivotSettings settings)
        {
            if (!(dt > 0f) || !math.all(math.isfinite(eyePos)))
            {
                return HasEstimate ? armEstimate : priorArm;
            }

            sampleAccum += dt;
            if (sampleAccum >= SampleIntervalSeconds)
            {
                sampleAccum = 0f;
                samplePositions[write] = eyePos;
                sampleRotations[write] = eyeRot;
                write = (write + 1) % samplePositions.Length;
                if (filled < samplePositions.Length) filled++;
            }

            solveAccum += dt;
            if (solveAccum >= SolveIntervalSeconds && filled >= 4)
            {
                solveAccum = 0f;
                IkNodPivotEstimatorCore.Solve(samplePositions, sampleRotations, filled,
                    HasEstimate ? armEstimate : priorArm, in settings, out LastResult);

                if (LastResult.Accepted)
                {
                    if (!HasEstimate)
                    {
                        armEstimate = priorArm;
                        HasEstimate = true;
                    }

                    armEstimate = math.lerp(armEstimate, LastResult.Arm, math.saturate(BlendPerAcceptance));
                }
            }

            return HasEstimate ? armEstimate : priorArm;
        }
    }
}
