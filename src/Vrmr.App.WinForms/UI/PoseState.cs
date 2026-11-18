using System;
using System.Collections.Generic;

namespace VRMR.UI
{
    /// <summary>What the posing tool and the action inspector share: mode, selection, gizmo, auto-key, pins.</summary>
    public static class PoseState
    {
        public enum Gizmo { Move, Rotate }

        public enum Mode { Devices, Bones }

        /// <summary>
        ///     Devices: pose the headset / controllers / trackers (the IK re-solves the body). Bones: pose the skeleton
        ///     directly.
        /// </summary>
        public static Mode Current = Mode.Devices;

        public static Gizmo Handle = Gizmo.Move;

        /// <summary>Gizmo axes along the device's / bone's own axes instead of the world's.</summary>
        public static bool Local;

        /// <summary>Every move writes keys at the playhead (off: moves are shown until the frame changes; I keys them).</summary>
        public static bool AutoKey = true;

        public static readonly HashSet<BodyRole> Devices = new();
        public static readonly HashSet<int> Bones = new();

        /// <summary>Hands / feet that stay where they are while other bones move (bone mode).</summary>
        public static readonly HashSet<int> Pins = new() { HumanBones.LeftFoot, HumanBones.RightFoot };

        /// <summary>Ramp length for switching a device off / on (seconds).</summary>
        public static double InfluenceRamp = 0.3;

        /// <summary>Bumped on every scene edit / evaluation (tools cache per version).</summary>
        public static int Version;

        public static event Action Changed;

        public static void Notify()
        {
            Version++;
            try { Changed?.Invoke(); }
            catch (Exception ex) { AppLog.Error("Pose state: " + ex.Message); }
        }

        public static string RoleLabel(BodyRole r)
        {
            switch (r)
            {
                case BodyRole.Head: return "Headset";
                case BodyRole.LeftHand: return "L controller";
                case BodyRole.RightHand: return "R controller";
                case BodyRole.LeftFoot: return "L foot";
                case BodyRole.RightFoot: return "R foot";
                case BodyRole.LeftKnee: return "L knee";
                case BodyRole.RightKnee: return "R knee";
                case BodyRole.LeftElbow: return "L elbow";
                case BodyRole.RightElbow: return "R elbow";
                default: return r.ToString();
            }
        }

        public static string BoneLabel(int b)
        {
            string n = HumanBones.Names[b];
            if (n.StartsWith("Left")) n = "L " + n.Substring(4);
            else if (n.StartsWith("Right")) n = "R " + n.Substring(5);
            return n.Replace("UpperLeg", "thigh").Replace("LowerLeg", "shin").Replace("UpperArm", "upper arm")
                .Replace("LowerArm", "forearm").Replace("UpperChest", "Upper chest");
        }
    }
}
