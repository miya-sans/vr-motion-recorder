// Portions Copyright (c) 2024 MR LUKE B DOOLAN. MIT License: see THIRD_PARTY_NOTICES.md.
// Plain C#: game-engine, Burst and job-system dependencies are replaced by the VRMR.Fbik shim.

namespace VRMR.Fbik
{
    public static class IkSwivelHintCore
    {
        const float sqrEpsilon = 1e-10f;

        public static IkSwivelFrame BuildFrame(Vector3 leftAnchor, Vector3 rightAnchor, Vector3 upFrom, Vector3 upTo)
        {
            IkSwivelFrame f = default;
            var up = upTo - upFrom;
            float upSqr = up.sqrMagnitude;

            if (!(upSqr > sqrEpsilon))
            {
                return f;
            }

            up /= Mathf.Sqrt(upSqr);

            var right = rightAnchor - leftAnchor;
            right -= up * Vector3.Dot(right, up);
            float rightSqr = right.sqrMagnitude;
            if (!(rightSqr > sqrEpsilon))
            {
                return f;
            }

            right /= Mathf.Sqrt(rightSqr);

            f.Right = right;
            f.Up = up;
            f.Forward = Vector3.Cross(right, up);
            f.Valid = true;
            return f;
        }
    }
}
