using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VRMR
{
    /// <summary>
    ///     Converts VRCFaceTracking avatar parameters (FT/v2/* Unified Expressions, including the combined
    ///     +/- parameters such as SmileFrownLeft or EyeLidLeft) into standard blendshape weights (0..1).
    ///     Naming: Unified Expressions (VRCFT) or ARKit (Apple 52), or Raw (parameter names as-is).
    /// </summary>
    public sealed class FaceMapping
    {
        public enum Naming { UnifiedExpressions, ARKit, Raw }

        static string[] _allShapeNames;

        // shapes EvaluateUE writes besides the Direct ones (a test checks that this list is complete)
        static readonly string[] Derived =
        {
            "EyeLookUpLeft", "EyeLookDownLeft", "EyeLookInLeft", "EyeLookOutLeft", "EyeLookUpRight", "EyeLookDownRight",
            "EyeLookInRight", "EyeLookOutRight",
            "EyeDilation", "EyeConstrict", "BrowDownLeft", "BrowDownRight",
            "LipFunnel", "LipPucker", "LipSuckUpper", "LipSuckLower", "MouthLeft", "MouthRight", "MouthUpperUp",
            "MouthLowerDown",
            "TongueOutStep1", "TongueOutStep2", "MouthTongueOutMorph", "TongueUpLeftMorph", "TongueUpRightMorph",
            "TongueDownLeftMorph", "TongueDownRightMorph"
        };

        // Parameters whose UE shape has the same name (pass-through when present)
        static readonly string[] Direct =
        {
            "EyeSquintLeft", "EyeSquintRight", "EyeWideLeft", "EyeWideRight", "EyeClosedLeft", "EyeClosedRight",
            "BrowPinchLeft", "BrowPinchRight", "BrowLowererLeft", "BrowLowererRight", "BrowInnerUpLeft",
            "BrowInnerUpRight", "BrowOuterUpLeft", "BrowOuterUpRight",
            "NoseSneerLeft", "NoseSneerRight", "NasalDilationLeft", "NasalDilationRight", "NasalConstrictLeft",
            "NasalConstrictRight",
            "CheekSquintLeft", "CheekSquintRight", "CheekPuffLeft", "CheekPuffRight", "CheekSuckLeft", "CheekSuckRight",
            "JawOpen", "JawForward", "JawBackward", "JawClench", "JawMandibleRaise", "JawLeft", "JawRight",
            "MouthClosed",
            "LipSuckUpperLeft", "LipSuckUpperRight", "LipSuckLowerLeft", "LipSuckLowerRight", "LipSuckCornerLeft",
            "LipSuckCornerRight",
            "LipFunnelUpperLeft", "LipFunnelUpperRight", "LipFunnelLowerLeft", "LipFunnelLowerRight",
            "LipPuckerUpperLeft", "LipPuckerUpperRight", "LipPuckerLowerLeft", "LipPuckerLowerRight",
            "MouthUpperUpLeft", "MouthUpperUpRight", "MouthLowerDownLeft", "MouthLowerDownRight",
            "MouthUpperDeepenLeft", "MouthUpperDeepenRight",
            "MouthUpperLeft", "MouthUpperRight", "MouthLowerLeft", "MouthLowerRight",
            "MouthCornerPullLeft", "MouthCornerPullRight", "MouthCornerSlantLeft", "MouthCornerSlantRight",
            "MouthFrownLeft", "MouthFrownRight",
            "MouthStretchLeft", "MouthStretchRight", "MouthDimpleLeft", "MouthDimpleRight", "MouthRaiserUpper",
            "MouthRaiserLower",
            "MouthPressLeft", "MouthPressRight", "MouthTightenerLeft", "MouthTightenerRight",
            "TongueOut", "TongueUp", "TongueDown", "TongueLeft", "TongueRight", "TongueRoll", "TongueBendDown",
            "TongueCurlUp", "TongueSquish", "TongueFlat", "TongueTwistLeft", "TongueTwistRight",
            "SoftPalateClose", "ThroatSwallow", "NeckFlexLeft", "NeckFlexRight",
            "MouthSmileLeft", "MouthSmileRight", "MouthSadLeft", "MouthSadRight"
        };

        static readonly Dictionary<string, string> UEtoARKit = new()
        {
            ["EyeClosedLeft"] = "eyeBlinkLeft", ["EyeClosedRight"] = "eyeBlinkRight", ["EyeWideLeft"] = "eyeWideLeft",
            ["EyeWideRight"] = "eyeWideRight",
            ["EyeSquintLeft"] = "eyeSquintLeft", ["EyeSquintRight"] = "eyeSquintRight",
            ["EyeLookInLeft"] = "eyeLookInLeft", ["EyeLookOutLeft"] = "eyeLookOutLeft",
            ["EyeLookUpLeft"] = "eyeLookUpLeft", ["EyeLookDownLeft"] = "eyeLookDownLeft",
            ["EyeLookInRight"] = "eyeLookInRight", ["EyeLookOutRight"] = "eyeLookOutRight",
            ["EyeLookUpRight"] = "eyeLookUpRight", ["EyeLookDownRight"] = "eyeLookDownRight",
            ["BrowLowererLeft"] = "browDownLeft", ["BrowLowererRight"] = "browDownRight",
            ["BrowOuterUpLeft"] = "browOuterUpLeft", ["BrowOuterUpRight"] = "browOuterUpRight",
            ["JawOpen"] = "jawOpen", ["JawLeft"] = "jawLeft", ["JawRight"] = "jawRight", ["JawForward"] = "jawForward",
            ["MouthClosed"] = "mouthClose",
            ["MouthSmileLeft"] = "mouthSmileLeft", ["MouthSmileRight"] = "mouthSmileRight",
            ["MouthFrownLeft"] = "mouthFrownLeft", ["MouthFrownRight"] = "mouthFrownRight",
            ["MouthStretchLeft"] = "mouthStretchLeft", ["MouthStretchRight"] = "mouthStretchRight",
            ["MouthDimpleLeft"] = "mouthDimpleLeft", ["MouthDimpleRight"] = "mouthDimpleRight",
            ["MouthPressLeft"] = "mouthPressLeft", ["MouthPressRight"] = "mouthPressRight",
            ["MouthRaiserUpper"] = "mouthShrugUpper", ["MouthRaiserLower"] = "mouthShrugLower",
            ["MouthUpperUpLeft"] = "mouthUpperUpLeft", ["MouthUpperUpRight"] = "mouthUpperUpRight",
            ["MouthLowerDownLeft"] = "mouthLowerDownLeft", ["MouthLowerDownRight"] = "mouthLowerDownRight",
            ["CheekSquintLeft"] = "cheekSquintLeft", ["CheekSquintRight"] = "cheekSquintRight",
            ["NoseSneerLeft"] = "noseSneerLeft", ["NoseSneerRight"] = "noseSneerRight",
            ["TongueOut"] = "tongueOut"
        };

        static readonly Dictionary<string, string[]> Aliases = new()
        {
            ["JawOpen"] = new[] { "MouthOpen", "JawDrop", "jawOpen" },
            ["MouthClosed"] = new[] { "JawClosed", "mouthClose" },
            ["EyeClosedLeft"] = new[] { "EyeBlinkLeft", "BlinkLeft", "Blink_L" },
            ["EyeClosedRight"] = new[] { "EyeBlinkRight", "BlinkRight", "Blink_R" }
        };

        public Naming Mode = Naming.UnifiedExpressions;
        public string Prefix = "FT/v2/";

        /// <summary>Unified Expressions shape names this mapping can output (direct ones).</summary>
        public static IReadOnlyList<string> UnifiedNames => Direct;

        /// <summary>
        ///     Every shape name a recording's face can be turned into: <see cref="UnifiedNames" /> plus the shapes worked out
        ///     from them (eye looks, pupil size, the "blended" lip and mouth shapes, and the tongue's steps and diagonal
        ///     correctives that face-tracking add-ons put on the mesh instead of a plain TongueOut). Look these up on a mesh.
        /// </summary>
        public static IReadOnlyList<string> AllShapeNames => _allShapeNames ??= Direct.Concat(Derived).ToArray();

        /// <summary>Returns UE-named weights from parameter lookup (name without prefix).</summary>
        public Dictionary<string, float> EvaluateUE(Func<string, float?> get)
        {
            var c = new Context { Get = get };
            foreach (string d in Direct)
            {
                float? v = c.P(d);
                if (v.HasValue) c.Set(d, v.Value);
            }

            // eyelids: 0 closed .. 0.75 neutral .. 1 wide
            foreach (string side in new[] { "Left", "Right" })
            {
                float? lid = c.P("EyeLid" + side) ?? c.P("EyeLid") ?? c.P("EyeOpen" + side) ?? c.P("EyeOpen");
                if (lid.HasValue)
                {
                    c.Set("EyeClosed" + side, 1f - lid.Value / 0.75f);
                    c.Set("EyeWide" + side, (lid.Value - 0.75f) / 0.25f);
                }

                float? sq = c.P("EyeSquint" + side) ?? c.P("EyeSquint");
                if (sq.HasValue) c.Set("EyeSquint" + side, sq.Value);

                // gaze as shapes (for shape-driven eyes)
                float? x = c.P("Eye" + side + "X") ?? c.P("EyeX");
                float? y = c.P("Eye" + side + "Y") ?? c.P("EyeY");
                if (x.HasValue)
                {
                    bool left = side == "Left";
                    float inward = left ? x.Value : -x.Value; // +X looks to the user's right
                    c.Set("EyeLookIn" + side, inward);
                    c.Set("EyeLookOut" + side, -inward);
                }

                if (y.HasValue)
                {
                    c.Set("EyeLookUp" + side, y.Value);
                    c.Set("EyeLookDown" + side, -y.Value);
                }

                float? brow = c.P("BrowExpression" + side) ?? c.P("BrowExpression");
                if (brow.HasValue)
                {
                    c.Set("BrowInnerUp" + side, brow.Value);
                    c.Set("BrowOuterUp" + side, brow.Value);
                    c.Set("BrowLowerer" + side, -brow.Value);
                    c.Set("BrowPinch" + side, -brow.Value * 0.6f);
                }

                float? bu = c.P("BrowUp" + side) ?? c.P("BrowUp");
                if (bu.HasValue)
                {
                    c.Set("BrowInnerUp" + side, bu.Value);
                    c.Set("BrowOuterUp" + side, bu.Value);
                }

                float? bd = c.P("BrowDown" + side) ?? c.P("BrowDown");
                if (bd.HasValue) c.Set("BrowLowerer" + side, bd.Value);
                float? bi = c.P("BrowInnerUp");
                if (bi.HasValue) c.Set("BrowInnerUp" + side, bi.Value);
                float? bo = c.P("BrowOuterUp");
                if (bo.HasValue) c.Set("BrowOuterUp" + side, bo.Value);

                float? sf = c.P("SmileFrown" + side) ?? c.P("SmileFrown");
                if (sf.HasValue)
                {
                    c.Set("MouthCornerPull" + side, sf.Value);
                    c.Set("MouthCornerSlant" + side, sf.Value);
                    c.Set("MouthSmile" + side, sf.Value);
                    c.Set("MouthFrown" + side, -sf.Value);
                    c.Set("MouthSad" + side, -sf.Value);
                }

                float? ss = c.P("SmileSad" + side) ?? c.P("SmileSad");
                if (ss.HasValue)
                {
                    c.Set("MouthSmile" + side, ss.Value);
                    c.Set("MouthCornerPull" + side, ss.Value);
                    c.Set("MouthSad" + side, -ss.Value);
                    c.Set("MouthFrown" + side, -ss.Value);
                }

                float? cps = c.P("CheekPuffSuck" + side) ?? c.P("CheekPuffSuck");
                if (cps.HasValue)
                {
                    c.Set("CheekPuff" + side, cps.Value);
                    c.Set("CheekSuck" + side, -cps.Value);
                }

                float? cs = c.P("CheekSquint");
                if (cs.HasValue) c.Set("CheekSquint" + side, cs.Value);
                float? ns = c.P("NoseSneer");
                if (ns.HasValue) c.Set("NoseSneer" + side, ns.Value);
                float? uu = c.P("MouthUpperUp");
                if (uu.HasValue) c.Set("MouthUpperUp" + side, uu.Value);
                float? ld = c.P("MouthLowerDown");
                if (ld.HasValue) c.Set("MouthLowerDown" + side, ld.Value);
                float? st = c.P("MouthStretch");
                if (st.HasValue) c.Set("MouthStretch" + side, st.Value);
                float? dm = c.P("MouthDimple");
                if (dm.HasValue) c.Set("MouthDimple" + side, dm.Value);
                float? pr = c.P("MouthPress");
                if (pr.HasValue) c.Set("MouthPress" + side, pr.Value);
                float? tg = c.P("MouthTightener");
                if (tg.HasValue) c.Set("MouthTightener" + side, tg.Value);
                float? su = c.P("LipSuckUpper");
                if (su.HasValue) c.Set("LipSuckUpper" + side, su.Value);
                float? sl = c.P("LipSuckLower");
                if (sl.HasValue) c.Set("LipSuckLower" + side, sl.Value);
                float? fu = c.P("LipFunnel") ?? c.P("LipFunnelUpper");
                if (fu.HasValue) c.Set("LipFunnelUpper" + side, fu.Value);
                float? fl = c.P("LipFunnel") ?? c.P("LipFunnelLower");
                if (fl.HasValue) c.Set("LipFunnelLower" + side, fl.Value);
                float? pu = c.P("LipPucker") ?? c.P("LipPuckerUpper");
                if (pu.HasValue) c.Set("LipPuckerUpper" + side, pu.Value);
                float? pl = c.P("LipPucker") ?? c.P("LipPuckerLower");
                if (pl.HasValue) c.Set("LipPuckerLower" + side, pl.Value);
            }

            float? jx = c.P("JawX");
            if (jx.HasValue)
            {
                c.Set("JawRight", jx.Value);
                c.Set("JawLeft", -jx.Value);
            }

            float? jz = c.P("JawZ");
            if (jz.HasValue)
            {
                c.Set("JawForward", jz.Value);
                c.Set("JawBackward", -jz.Value);
            }

            float? mx = c.P("MouthX");
            if (mx.HasValue)
            {
                c.Set("MouthUpperRight", mx.Value);
                c.Set("MouthLowerRight", mx.Value);
                c.Set("MouthUpperLeft", -mx.Value);
                c.Set("MouthLowerLeft", -mx.Value);
            }

            float? mux = c.P("MouthUpperX");
            if (mux.HasValue)
            {
                c.Set("MouthUpperRight", mux.Value);
                c.Set("MouthUpperLeft", -mux.Value);
            }

            float? mlx = c.P("MouthLowerX");
            if (mlx.HasValue)
            {
                c.Set("MouthLowerRight", mlx.Value);
                c.Set("MouthLowerLeft", -mlx.Value);
            }

            float? tx = c.P("TongueX");
            if (tx.HasValue)
            {
                c.Set("TongueRight", tx.Value);
                c.Set("TongueLeft", -tx.Value);
            }

            float? ty = c.P("TongueY");
            if (ty.HasValue)
            {
                c.Set("TongueUp", ty.Value);
                c.Set("TongueDown", -ty.Value);
            }

            float? rais = c.P("MouthRaiser");
            if (rais.HasValue)
            {
                c.Set("MouthRaiserUpper", rais.Value);
                c.Set("MouthRaiserLower", rais.Value);
            }

            float? pd = c.P("PupilDilation") ?? c.P("EyeDilation");
            if (pd.HasValue)
            {
                c.Set("EyeDilation", (pd.Value - 0.5f) * 2f);
                c.Set("EyeConstrict", (0.5f - pd.Value) * 2f);
            }

            AddBlended(c.Out);
            return c.Out;
        }

        /// <summary>Unified Expressions "blended" shapes many avatars ship instead of (or besides) the base ones.</summary>
        static void AddBlended(Dictionary<string, float> o)
        {
            float G(string k)
            {
                return o.TryGetValue(k, out float v) ? v : 0f;
            }

            bool Has(params string[] ks)
            {
                foreach (string k in ks)
                    if (o.ContainsKey(k))
                        return true;
                return false;
            }

            void Avg(string name, params string[] parts)
            {
                if (Has(parts) && !o.ContainsKey(name)) o[name] = MathX.Clamp01(parts.Sum(G) / parts.Length);
            }

            Avg("LipFunnel", "LipFunnelUpperLeft", "LipFunnelUpperRight", "LipFunnelLowerLeft", "LipFunnelLowerRight");
            Avg("LipPucker", "LipPuckerUpperLeft", "LipPuckerUpperRight", "LipPuckerLowerLeft", "LipPuckerLowerRight");
            Avg("LipSuckUpper", "LipSuckUpperLeft", "LipSuckUpperRight");
            Avg("LipSuckLower", "LipSuckLowerLeft", "LipSuckLowerRight");
            Avg("MouthLeft", "MouthUpperLeft", "MouthLowerLeft");
            Avg("MouthRight", "MouthUpperRight", "MouthLowerRight");
            Avg("MouthUpperUp", "MouthUpperUpLeft", "MouthUpperUpRight");
            Avg("MouthLowerDown", "MouthLowerDownLeft", "MouthLowerDownRight");
            if (o.ContainsKey("BrowLowererLeft") && !o.ContainsKey("BrowDownLeft"))
                o["BrowDownLeft"] = G("BrowLowererLeft");
            if (o.ContainsKey("BrowLowererRight") && !o.ContainsKey("BrowDownRight"))
                o["BrowDownRight"] = G("BrowLowererRight");
            if (o.ContainsKey("TongueOut")) AddTongueOut(o, G("TongueOut"), G("JawOpen") - G("MouthClosed"));
            if (Has("TongueUp", "TongueDown") && Has("TongueLeft", "TongueRight"))
            {
                // diagonal tongue correctives (e.g. Triturbo/VRCFT templates)
                o["TongueUpLeftMorph"] = G("TongueUp") * G("TongueLeft");
                o["TongueUpRightMorph"] = G("TongueUp") * G("TongueRight");
                o["TongueDownLeftMorph"] = G("TongueDown") * G("TongueLeft");
                o["TongueDownRightMorph"] = G("TongueDown") * G("TongueRight");
            }
        }

        /// <summary>
        ///     The tongue of face-tracking add-ons that push it out with two step shapes, as their animator does it
        ///     (Triturbo's "Tongue Out" blend tree): the first step is fully out at 0.3, the second goes from 0.3 to 0.9.
        ///     Their "Tongue Out Mouth" tree also shapes the mouth around the tongue (MouthTongueOutMorph, and the lower
        ///     lip pulled back up): it fades in between 0.3 and 0.1 below that, and fades out as the mouth opens.
        ///     <paramref name="mouthOpenness" /> is the add-on's own openness value, JawOpen minus MouthClosed (-1 shut .. 1
        ///     wide), as its parameter setup links them.
        /// </summary>
        static void AddTongueOut(Dictionary<string, float> o, float tongue, float mouthOpenness)
        {
            o["TongueOutStep1"] = MathX.Clamp01(tongue / 0.3f);
            o["TongueOutStep2"] = MathX.Clamp01((tongue - 0.3f) / 0.6f);
            float morph = MathX.Clamp01((tongue - 0.1f) / 0.2f) * (1f - MathX.Clamp01(mouthOpenness));
            o["MouthTongueOutMorph"] = morph;
            foreach (string lip in new[] { "MouthLowerDownLeft", "MouthLowerDownRight" })
                if (o.TryGetValue(lip, out float v))
                    o[lip] = MathX.Clamp01(v - morph);
        }

        /// <summary>Evaluate shapes in the selected naming from a parameter lookup (full names, e.g. "FT/v2/JawOpen").</summary>
        public Dictionary<string, float> Evaluate(Func<string, float?> getFull)
        {
            Func<string, float?> get = n => getFull(Prefix + n) ?? getFull("v2/" + n);
            var ue = EvaluateUE(get);
            if (Mode == Naming.UnifiedExpressions) return ue;
            if (Mode == Naming.Raw) return ue; // raw handled separately by exporter (parameter names)
            var ak = new Dictionary<string, float>();
            foreach (var kv in ue)
                if (UEtoARKit.TryGetValue(kv.Key, out string a))
                    ak[a] = Math.Max(ak.TryGetValue(a, out float c) ? c : 0, kv.Value);

            float Avg(string a, string b)
            {
                return ((ue.TryGetValue(a, out float x) ? x : 0) + (ue.TryGetValue(b, out float y) ? y : 0)) * 0.5f;
            }

            ak["browInnerUp"] = Avg("BrowInnerUpLeft", "BrowInnerUpRight");
            ak["cheekPuff"] = Avg("CheekPuffLeft", "CheekPuffRight");
            ak["mouthFunnel"] = (Avg("LipFunnelUpperLeft", "LipFunnelUpperRight") +
                                 Avg("LipFunnelLowerLeft", "LipFunnelLowerRight")) * 0.5f;
            ak["mouthPucker"] = (Avg("LipPuckerUpperLeft", "LipPuckerUpperRight") +
                                 Avg("LipPuckerLowerLeft", "LipPuckerLowerRight")) * 0.5f;
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
            var all = Evaluate(n => set.Contains(n) ? 0.5f : null);
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
            foreach (string s in meshShapes)
            {
                string k = Norm(s);
                if (!norm.ContainsKey(k)) norm[k] = s;
            }

            foreach (string s in shapes)
            {
                if (exact.Contains(s)) map[s] = s;
                else if (norm.TryGetValue(Norm(s), out string m)) map[s] = m;
                else if (UEtoARKit.TryGetValue(s, out string ak) && norm.TryGetValue(Norm(ak), out m)) map[s] = m;
                else if (Aliases.TryGetValue(s, out string[] al))
                    foreach (string a in al)
                        if (norm.TryGetValue(Norm(a), out m))
                        {
                            map[s] = m;
                            break;
                        }
            }

            return map;
        }

        static string Norm(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s)
                if (char.IsLetterOrDigit(ch))
                    sb.Append(char.ToLowerInvariant(ch));
            string r = sb.ToString();
            foreach (string pre in new[] { "ue", "ft", "v2", "arkit", "blendshape" })
                if (r.StartsWith(pre) && r.Length > pre.Length + 3)
                {
                    r = r.Substring(pre.Length);
                    break;
                }

            return r;
        }

        public sealed class Context
        {
            public readonly Dictionary<string, float> Out = new();
            public Func<string, float?> Get;

            public float? P(string n)
            {
                return Get(n);
            }

            public void Set(string shape, float v)
            {
                v = MathX.Clamp01(v);
                if (Out.TryGetValue(shape, out float cur)) Out[shape] = Math.Max(cur, v);
                else Out[shape] = v;
            }
        }
    }
}
