#if UNITY_EDITOR
// Copy of VR Motion Recorder's FaceMapping (src/Vrmr.Avatars/FaceMapping.cs) so the bridge can map face-tracking
// parameters edited in Blender onto the avatar's real blendshapes. Keep in sync with the app.
using System;
using System.Collections.Generic;
using System.Linq;

namespace VRMotionBridge
{
    /// <summary>
    /// Converts VRCFaceTracking avatar parameters (FT/v2/* Unified Expressions, including the combined
    /// +/- parameters such as SmileFrownLeft or EyeLidLeft) into standard blendshape weights (0..1).
    /// Naming: Unified Expressions (VRCFT) or ARKit (Apple 52), or Raw (parameter names as-is).
    /// </summary>
    public sealed class FaceMapping
    {
        public enum Naming { UnifiedExpressions, ARKit, Raw }
        public Naming Mode = Naming.UnifiedExpressions;
        public string Prefix = "FT/v2/";

        // Parameters whose UE shape has the same name (pass-through when present)
        static readonly string[] Direct =
        {
            "EyeSquintLeft","EyeSquintRight","EyeWideLeft","EyeWideRight","EyeClosedLeft","EyeClosedRight",
            "BrowPinchLeft","BrowPinchRight","BrowLowererLeft","BrowLowererRight","BrowInnerUpLeft","BrowInnerUpRight","BrowOuterUpLeft","BrowOuterUpRight",
            "NoseSneerLeft","NoseSneerRight","NasalDilationLeft","NasalDilationRight","NasalConstrictLeft","NasalConstrictRight",
            "CheekSquintLeft","CheekSquintRight","CheekPuffLeft","CheekPuffRight","CheekSuckLeft","CheekSuckRight",
            "JawOpen","JawForward","JawBackward","JawClench","JawMandibleRaise","JawLeft","JawRight","MouthClosed",
            "LipSuckUpperLeft","LipSuckUpperRight","LipSuckLowerLeft","LipSuckLowerRight","LipSuckCornerLeft","LipSuckCornerRight",
            "LipFunnelUpperLeft","LipFunnelUpperRight","LipFunnelLowerLeft","LipFunnelLowerRight",
            "LipPuckerUpperLeft","LipPuckerUpperRight","LipPuckerLowerLeft","LipPuckerLowerRight",
            "MouthUpperUpLeft","MouthUpperUpRight","MouthLowerDownLeft","MouthLowerDownRight","MouthUpperDeepenLeft","MouthUpperDeepenRight",
            "MouthUpperLeft","MouthUpperRight","MouthLowerLeft","MouthLowerRight",
            "MouthCornerPullLeft","MouthCornerPullRight","MouthCornerSlantLeft","MouthCornerSlantRight","MouthFrownLeft","MouthFrownRight",
            "MouthStretchLeft","MouthStretchRight","MouthDimpleLeft","MouthDimpleRight","MouthRaiserUpper","MouthRaiserLower",
            "MouthPressLeft","MouthPressRight","MouthTightenerLeft","MouthTightenerRight",
            "TongueOut","TongueUp","TongueDown","TongueLeft","TongueRight","TongueRoll","TongueBendDown","TongueCurlUp","TongueSquish","TongueFlat","TongueTwistLeft","TongueTwistRight",
            "SoftPalateClose","ThroatSwallow","NeckFlexLeft","NeckFlexRight",
            "MouthSmileLeft","MouthSmileRight","MouthSadLeft","MouthSadRight",
        };

        public sealed class Context
        {
            public Func<string, float?> Get;
            public readonly Dictionary<string, float> Out = new Dictionary<string, float>();
            public float? P(string n) => Get(n);
            public void Set(string shape, float v)
            {
                v = UnityEngine.Mathf.Clamp01(v);
                if (Out.TryGetValue(shape, out float cur)) Out[shape] = Math.Max(cur, v); else Out[shape] = v;
            }
        }

        /// <summary>Returns UE-named weights from parameter lookup (name without prefix).</summary>
        public Dictionary<string, float> EvaluateUE(Func<string, float?> get)
        {
            var c = new Context { Get = get };
            foreach (var d in Direct) { var v = c.P(d); if (v.HasValue) c.Set(d, v.Value); }

            // eyelids: 0 closed .. 0.75 neutral .. 1 wide
            foreach (var side in new[] { "Left", "Right" })
            {
                var lid = c.P("EyeLid" + side) ?? c.P("EyeLid") ?? c.P("EyeOpen" + side) ?? c.P("EyeOpen");
                if (lid.HasValue)
                {
                    c.Set("EyeClosed" + side, 1f - lid.Value / 0.75f);
                    c.Set("EyeWide" + side, (lid.Value - 0.75f) / 0.25f);
                }
                var sq = c.P("EyeSquint" + side) ?? c.P("EyeSquint");
                if (sq.HasValue) c.Set("EyeSquint" + side, sq.Value);

                // gaze as shapes (for shape-driven eyes)
                var x = c.P("Eye" + side + "X") ?? c.P("EyeX");
                var y = c.P("Eye" + side + "Y") ?? c.P("EyeY");
                if (x.HasValue)
                {
                    bool left = side == "Left";
                    float inward = left ? x.Value : -x.Value; // +X looks to the user's right
                    c.Set("EyeLookIn" + side, inward); c.Set("EyeLookOut" + side, -inward);
                }
                if (y.HasValue) { c.Set("EyeLookUp" + side, y.Value); c.Set("EyeLookDown" + side, -y.Value); }

                var brow = c.P("BrowExpression" + side) ?? c.P("BrowExpression");
                if (brow.HasValue)
                {
                    c.Set("BrowInnerUp" + side, brow.Value); c.Set("BrowOuterUp" + side, brow.Value);
                    c.Set("BrowLowerer" + side, -brow.Value); c.Set("BrowPinch" + side, -brow.Value * 0.6f);
                }
                var bu = c.P("BrowUp" + side) ?? c.P("BrowUp"); if (bu.HasValue) { c.Set("BrowInnerUp" + side, bu.Value); c.Set("BrowOuterUp" + side, bu.Value); }
                var bd = c.P("BrowDown" + side) ?? c.P("BrowDown"); if (bd.HasValue) c.Set("BrowLowerer" + side, bd.Value);
                var bi = c.P("BrowInnerUp"); if (bi.HasValue) c.Set("BrowInnerUp" + side, bi.Value);
                var bo = c.P("BrowOuterUp"); if (bo.HasValue) c.Set("BrowOuterUp" + side, bo.Value);

                var sf = c.P("SmileFrown" + side) ?? c.P("SmileFrown");
                if (sf.HasValue)
                {
                    c.Set("MouthCornerPull" + side, sf.Value); c.Set("MouthCornerSlant" + side, sf.Value); c.Set("MouthSmile" + side, sf.Value);
                    c.Set("MouthFrown" + side, -sf.Value); c.Set("MouthSad" + side, -sf.Value);
                }
                var ss = c.P("SmileSad" + side) ?? c.P("SmileSad");
                if (ss.HasValue) { c.Set("MouthSmile" + side, ss.Value); c.Set("MouthCornerPull" + side, ss.Value); c.Set("MouthSad" + side, -ss.Value); c.Set("MouthFrown" + side, -ss.Value); }
                var cps = c.P("CheekPuffSuck" + side) ?? c.P("CheekPuffSuck");
                if (cps.HasValue) { c.Set("CheekPuff" + side, cps.Value); c.Set("CheekSuck" + side, -cps.Value); }
                var cs = c.P("CheekSquint"); if (cs.HasValue) c.Set("CheekSquint" + side, cs.Value);
                var ns = c.P("NoseSneer"); if (ns.HasValue) c.Set("NoseSneer" + side, ns.Value);
                var uu = c.P("MouthUpperUp"); if (uu.HasValue) c.Set("MouthUpperUp" + side, uu.Value);
                var ld = c.P("MouthLowerDown"); if (ld.HasValue) c.Set("MouthLowerDown" + side, ld.Value);
                var st = c.P("MouthStretch"); if (st.HasValue) c.Set("MouthStretch" + side, st.Value);
                var dm = c.P("MouthDimple"); if (dm.HasValue) c.Set("MouthDimple" + side, dm.Value);
                var pr = c.P("MouthPress"); if (pr.HasValue) c.Set("MouthPress" + side, pr.Value);
                var tg = c.P("MouthTightener"); if (tg.HasValue) c.Set("MouthTightener" + side, tg.Value);
                var su = c.P("LipSuckUpper"); if (su.HasValue) c.Set("LipSuckUpper" + side, su.Value);
                var sl = c.P("LipSuckLower"); if (sl.HasValue) c.Set("LipSuckLower" + side, sl.Value);
                var fu = c.P("LipFunnel") ?? c.P("LipFunnelUpper"); if (fu.HasValue) c.Set("LipFunnelUpper" + side, fu.Value);
                var fl = c.P("LipFunnel") ?? c.P("LipFunnelLower"); if (fl.HasValue) c.Set("LipFunnelLower" + side, fl.Value);
                var pu = c.P("LipPucker") ?? c.P("LipPuckerUpper"); if (pu.HasValue) c.Set("LipPuckerUpper" + side, pu.Value);
                var pl = c.P("LipPucker") ?? c.P("LipPuckerLower"); if (pl.HasValue) c.Set("LipPuckerLower" + side, pl.Value);
            }
            var jx = c.P("JawX"); if (jx.HasValue) { c.Set("JawRight", jx.Value); c.Set("JawLeft", -jx.Value); }
            var jz = c.P("JawZ"); if (jz.HasValue) { c.Set("JawForward", jz.Value); c.Set("JawBackward", -jz.Value); }
            var mx = c.P("MouthX"); if (mx.HasValue) { c.Set("MouthUpperRight", mx.Value); c.Set("MouthLowerRight", mx.Value); c.Set("MouthUpperLeft", -mx.Value); c.Set("MouthLowerLeft", -mx.Value); }
            var mux = c.P("MouthUpperX"); if (mux.HasValue) { c.Set("MouthUpperRight", mux.Value); c.Set("MouthUpperLeft", -mux.Value); }
            var mlx = c.P("MouthLowerX"); if (mlx.HasValue) { c.Set("MouthLowerRight", mlx.Value); c.Set("MouthLowerLeft", -mlx.Value); }
            var tx = c.P("TongueX"); if (tx.HasValue) { c.Set("TongueRight", tx.Value); c.Set("TongueLeft", -tx.Value); }
            var ty = c.P("TongueY"); if (ty.HasValue) { c.Set("TongueUp", ty.Value); c.Set("TongueDown", -ty.Value); }
            var rais = c.P("MouthRaiser"); if (rais.HasValue) { c.Set("MouthRaiserUpper", rais.Value); c.Set("MouthRaiserLower", rais.Value); }
            var pd = c.P("PupilDilation") ?? c.P("EyeDilation");
            if (pd.HasValue) { c.Set("EyeDilation", (pd.Value - 0.5f) * 2f); c.Set("EyeConstrict", (0.5f - pd.Value) * 2f); }
            AddBlended(c.Out);
            return c.Out;
        }

        /// <summary>Unified Expressions "blended" shapes many avatars ship instead of (or besides) the base ones.</summary>
        static void AddBlended(Dictionary<string, float> o)
        {
            float G(string k) => o.TryGetValue(k, out var v) ? v : 0f;
            bool Has(params string[] ks) { foreach (var k in ks) if (o.ContainsKey(k)) return true; return false; }
            void Avg(string name, params string[] parts) { if (Has(parts) && !o.ContainsKey(name)) o[name] = UnityEngine.Mathf.Clamp01(parts.Sum(G) / parts.Length); }
            Avg("LipFunnel", "LipFunnelUpperLeft", "LipFunnelUpperRight", "LipFunnelLowerLeft", "LipFunnelLowerRight");
            Avg("LipPucker", "LipPuckerUpperLeft", "LipPuckerUpperRight", "LipPuckerLowerLeft", "LipPuckerLowerRight");
            Avg("LipSuckUpper", "LipSuckUpperLeft", "LipSuckUpperRight");
            Avg("LipSuckLower", "LipSuckLowerLeft", "LipSuckLowerRight");
            Avg("MouthLeft", "MouthUpperLeft", "MouthLowerLeft");
            Avg("MouthRight", "MouthUpperRight", "MouthLowerRight");
            Avg("MouthUpperUp", "MouthUpperUpLeft", "MouthUpperUpRight");
            Avg("MouthLowerDown", "MouthLowerDownLeft", "MouthLowerDownRight");
            if (o.ContainsKey("BrowLowererLeft") && !o.ContainsKey("BrowDownLeft")) o["BrowDownLeft"] = G("BrowLowererLeft");
            if (o.ContainsKey("BrowLowererRight") && !o.ContainsKey("BrowDownRight")) o["BrowDownRight"] = G("BrowLowererRight");
            if (o.ContainsKey("TongueOut")) { float t = G("TongueOut"); o["TongueOutStep1"] = UnityEngine.Mathf.Clamp01(t * 2f); o["TongueOutStep2"] = UnityEngine.Mathf.Clamp01(t * 2f - 1f); }
            if (Has("TongueUp", "TongueDown") && Has("TongueLeft", "TongueRight"))
            {
                // diagonal tongue correctives (some face-tracking templates have them)
                o["TongueUpLeftMorph"] = G("TongueUp") * G("TongueLeft"); o["TongueUpRightMorph"] = G("TongueUp") * G("TongueRight");
                o["TongueDownLeftMorph"] = G("TongueDown") * G("TongueLeft"); o["TongueDownRightMorph"] = G("TongueDown") * G("TongueRight");
            }
        }

        static readonly Dictionary<string, string> UEtoARKit = new Dictionary<string, string>
        {
            ["EyeClosedLeft"] = "eyeBlinkLeft", ["EyeClosedRight"] = "eyeBlinkRight", ["EyeWideLeft"] = "eyeWideLeft", ["EyeWideRight"] = "eyeWideRight",
            ["EyeSquintLeft"] = "eyeSquintLeft", ["EyeSquintRight"] = "eyeSquintRight",
            ["EyeLookInLeft"] = "eyeLookInLeft", ["EyeLookOutLeft"] = "eyeLookOutLeft", ["EyeLookUpLeft"] = "eyeLookUpLeft", ["EyeLookDownLeft"] = "eyeLookDownLeft",
            ["EyeLookInRight"] = "eyeLookInRight", ["EyeLookOutRight"] = "eyeLookOutRight", ["EyeLookUpRight"] = "eyeLookUpRight", ["EyeLookDownRight"] = "eyeLookDownRight",
            ["BrowLowererLeft"] = "browDownLeft", ["BrowLowererRight"] = "browDownRight", ["BrowOuterUpLeft"] = "browOuterUpLeft", ["BrowOuterUpRight"] = "browOuterUpRight",
            ["JawOpen"] = "jawOpen", ["JawLeft"] = "jawLeft", ["JawRight"] = "jawRight", ["JawForward"] = "jawForward", ["MouthClosed"] = "mouthClose",
            ["MouthSmileLeft"] = "mouthSmileLeft", ["MouthSmileRight"] = "mouthSmileRight", ["MouthFrownLeft"] = "mouthFrownLeft", ["MouthFrownRight"] = "mouthFrownRight",
            ["MouthStretchLeft"] = "mouthStretchLeft", ["MouthStretchRight"] = "mouthStretchRight", ["MouthDimpleLeft"] = "mouthDimpleLeft", ["MouthDimpleRight"] = "mouthDimpleRight",
            ["MouthPressLeft"] = "mouthPressLeft", ["MouthPressRight"] = "mouthPressRight", ["MouthRaiserUpper"] = "mouthShrugUpper", ["MouthRaiserLower"] = "mouthShrugLower",
            ["MouthUpperUpLeft"] = "mouthUpperUpLeft", ["MouthUpperUpRight"] = "mouthUpperUpRight", ["MouthLowerDownLeft"] = "mouthLowerDownLeft", ["MouthLowerDownRight"] = "mouthLowerDownRight",
            ["CheekSquintLeft"] = "cheekSquintLeft", ["CheekSquintRight"] = "cheekSquintRight", ["NoseSneerLeft"] = "noseSneerLeft", ["NoseSneerRight"] = "noseSneerRight",
            ["TongueOut"] = "tongueOut",
        };

        /// <summary>Evaluate shapes in the selected naming from a parameter lookup (full names, e.g. "FT/v2/JawOpen").</summary>
        public Dictionary<string, float> Evaluate(Func<string, float?> getFull)
        {
            Func<string, float?> get = n => getFull(Prefix + n) ?? getFull("v2/" + n);
            var ue = EvaluateUE(get);
            if (Mode == Naming.UnifiedExpressions) return ue;
            if (Mode == Naming.Raw) return ue; // raw handled separately by exporter (parameter names)
            var ak = new Dictionary<string, float>();
            foreach (var kv in ue) if (UEtoARKit.TryGetValue(kv.Key, out var a)) ak[a] = Math.Max(ak.TryGetValue(a, out var c) ? c : 0, kv.Value);
            float Avg(string a, string b) => ((ue.TryGetValue(a, out var x) ? x : 0) + (ue.TryGetValue(b, out var y) ? y : 0)) * 0.5f;
            ak["browInnerUp"] = Avg("BrowInnerUpLeft", "BrowInnerUpRight");
            ak["cheekPuff"] = Avg("CheekPuffLeft", "CheekPuffRight");
            ak["mouthFunnel"] = (Avg("LipFunnelUpperLeft", "LipFunnelUpperRight") + Avg("LipFunnelLowerLeft", "LipFunnelLowerRight")) * 0.5f;
            ak["mouthPucker"] = (Avg("LipPuckerUpperLeft", "LipPuckerUpperRight") + Avg("LipPuckerLowerLeft", "LipPuckerLowerRight")) * 0.5f;
            ak["mouthRollUpper"] = Avg("LipSuckUpperLeft", "LipSuckUpperRight");
            ak["mouthRollLower"] = Avg("LipSuckLowerLeft", "LipSuckLowerRight");
            ak["mouthLeft"] = Avg("MouthUpperLeft", "MouthLowerLeft");
            ak["mouthRight"] = Avg("MouthUpperRight", "MouthLowerRight");
            return ak;
        }

        /// <summary>The shape names this mapping can output given the recorded parameter names.</summary>
        public List<string> ShapeNames(IEnumerable<string> paramNames)
        {
            var set = new HashSet<string>(paramNames);
            var all = Evaluate(n => set.Contains(n) ? 0.5f : (float?)null);
            return all.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        }

        /// <summary>Resolve output shape names to the avatar's actual blendshapes (case/underscore-insensitive, ARKit aliases).</summary>
        public static Dictionary<string, string> MatchToMesh(IEnumerable<string> shapes, IEnumerable<string> meshShapes)
        {
            var map = new Dictionary<string, string>();
            if (meshShapes == null) return map;
            var norm = new Dictionary<string, string>();
            // an exact name wins over a look-alike (one test avatar's face mesh has its own "tongue_up" and the face-tracking "TongueUp")
            var exact = new HashSet<string>(meshShapes);
            foreach (var s in meshShapes)
            {
                string k = Norm(s);
                if (!norm.ContainsKey(k)) norm[k] = s;
            }
            foreach (var s in shapes)
            {
                if (exact.Contains(s)) map[s] = s;
                else if (norm.TryGetValue(Norm(s), out var m)) map[s] = m;
                else if (UEtoARKit.TryGetValue(s, out var ak) && norm.TryGetValue(Norm(ak), out m)) map[s] = m;
                else if (Aliases.TryGetValue(s, out var al)) foreach (var a in al) if (norm.TryGetValue(Norm(a), out m)) { map[s] = m; break; }
            }
            return map;
        }

        static readonly Dictionary<string, string[]> Aliases = new Dictionary<string, string[]>
        {
            ["JawOpen"] = new[] { "MouthOpen", "JawDrop", "jawOpen" },
            ["MouthClosed"] = new[] { "JawClosed", "mouthClose" },
            ["EyeClosedLeft"] = new[] { "EyeBlinkLeft", "BlinkLeft", "Blink_L" },
            ["EyeClosedRight"] = new[] { "EyeBlinkRight", "BlinkRight", "Blink_R" },
        };

        static string Norm(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            string r = sb.ToString();
            foreach (var pre in new[] { "ue", "ft", "v2", "arkit", "blendshape" }) if (r.StartsWith(pre) && r.Length > pre.Length + 3) { r = r.Substring(pre.Length); break; }
            return r;
        }
    }
}

#endif
