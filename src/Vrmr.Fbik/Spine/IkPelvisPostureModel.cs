// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.

namespace VRMR.Fbik
{
    public static class IkPelvisPostureModel
    {
        public const float MaxDrop = 0.65f, MaxLean = 0.60f;

        public static float Coupling(float drop, float lean)
        {
            if (!(drop >= 0f) || !(lean >= 0f))
            {
                return 0f;
            }

            float d = math.min(drop, MaxDrop), f = math.min(lean, MaxLean);
            float k = +8.57863984e-01f * 1f + -3.02568994e+00f * f + +2.31442802e+00f * d + -2.59377618e+00f * f * f +
                      +2.55773595e+00f * d * f + -4.91301461e+00f * d * d + -7.99234298e+00f * f * f * f +
                      +1.99478947e+01f * d * f * f;

            return math.clamp(k, 0f, 1f);
        }

        public static float PelvisDrop(float drop, float lean)
        {
            return Coupling(drop, lean) * math.max(drop, 0f);
        }
    }
}
