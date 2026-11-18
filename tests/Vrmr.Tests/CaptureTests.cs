using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Xunit;

namespace VRMR.Tests
{
    public class LogTailerTests
    {
        const string Log1 = "output_log_2026-01-01_10-00-00.txt", Log2 = "output_log_2026-01-01_11-00-00.txt";
        const string Stamp = "2026.01.01 10:00:00 Debug      -  ";

        static string HumrLine(string target, long take, double time, int bones = HumanBones.Count)
        {
            var sb = new StringBuilder(Stamp + "[HUMR] RECORDING;BoneRotations;" + target + ";" + take + ";" +
                                       time.ToString("R", CultureInfo.InvariantCulture) + ";0.1,0.9,-0.2");
            for (int b = 0; b < bones; b++)
                sb.Append(";0,0,").Append((b * 0.01).ToString("R", CultureInfo.InvariantCulture)).Append(",1");
            return sb.Append("\r\n").ToString();
        }

        [Fact]
        public void HistoryIsReadForContextButRaisesNoEvents()
        {
            string history = Stamp + "User Authenticated: Tester (usr_1)\n" + Stamp +
                             "[Behaviour] Entering Room: First World\n" + Stamp +
                             "[Behaviour] Switching Tester to avatar Old Avatar\n";
            using (var rig = new Rig(Log1, history))
            {
                Assert.False(rig.Tailer.Poll());
                Assert.Equal(rig.PathOf(Log1), rig.Tailer.CurrentFile);
                Assert.Equal("Tester", rig.Tailer.LocalUser);
                Assert.Equal("First World", rig.Tailer.WorldName);
                Assert.Equal("Old Avatar", rig.Tailer.LocalAvatarName);
                Assert.Empty(rig.Events);
            }
        }

        [Fact]
        public void NewLinesRaiseEventsAsTheFileGrows()
        {
            using (var rig = new Rig(Log1, Stamp + "User Authenticated: Tester (usr_1)\n"))
            {
                rig.Tailer.Poll();
                rig.Append(Log1,
                    Stamp + "[Behaviour] Joining wrld_abc:123~private\r\n" + Stamp +
                    "[Behaviour] Entering Room: Second World\r\n" + Stamp + "Something unrelated\r\n");
                Assert.True(rig.Tailer.Poll());
                rig.Append(Log1, Stamp + "[Behaviour] Switching Someone Else to avatar Theirs\n" + Stamp +
                                 "[Behaviour] Switching Tester to avatar Mine\n"
                                 + Stamp + "[Behaviour] Initialize SixPoint Avatar VRCPlayer[Local] 1 on Tester\n" +
                                 Stamp + "Measure Human Avatar Avatar isRemeasure:True\n");
                Assert.True(rig.Tailer.Poll());
                Assert.False(rig.Tailer.Poll());
                Assert.Equal(
                    new[] { "joining:wrld_abc", "world:Second World", "avatar:Mine", "ikInit:SixPoint", "remeasure:" },
                    rig.Events);
                Assert.Equal("wrld_abc", rig.Tailer.WorldId);
            }
        }

        [Fact]
        public void ALineWrittenInTwoPiecesIsHandledOnceItIsComplete()
        {
            using (var rig = new Rig(Log1))
            {
                rig.Tailer.Poll();
                rig.Append(Log1, Stamp + "[Behaviour] Entering Ro");
                rig.Tailer.Poll();
                Assert.Empty(rig.Events);
                rig.Append(Log1, "om: Split World\n");
                rig.Tailer.Poll();
                Assert.Equal(new[] { "world:Split World" }, rig.Events);
            }
        }

        [Fact]
        public void HumrFramesAreParsed()
        {
            using (var rig = new Rig(Log1))
            {
                rig.Tailer.Poll();
                rig.Append(Log1, HumrLine("Tester", 638000000000000000, 1.25) +
                                 HumrLine("Tester", 638000000000000000, 1.2666667, 30) + HumrLine("Tester", 1, 2, 10)
                                 + Stamp + "[HUMR] RECORDING;BoneRotations;Tester;1;not-a-number;0,0,0;0,0,0,1\n");
                rig.Tailer.Poll();
                Assert.Equal(2, rig.Frames.Count); // ten bones are too few, and a broken time is skipped
                var f = rig.Frames[0];
                Assert.Equal("Tester", f.Target);
                Assert.Equal(638000000000000000, f.Take);
                Assert.Equal(1.25, f.HumrTime);
                Assert.Equal(new Vec3(0.1f, 0.9f, -0.2f), f.Hips);
                Assert.Equal(HumanBones.Count, f.Rot.Length);
                Assert.Equal(0.07f, f.Rot[7].z);
                Assert.Equal(1f, f.Rot[54].w);
                Assert.Same(f.Target, rig.Frames[1].Target);
                Assert.Equal(1f, rig.Frames[1].Rot[29].w);
                Assert.Equal(0f, rig.Frames[1].Rot[30].w); // a bone the line does not have
                Assert.Equal(2, rig.Tailer.HumrFrames);
                Assert.True(rig.Tailer.HumrActive);
            }
        }

        [Fact]
        public void ATruncatedLogIsReadFromItsNewBeginning()
        {
            using (var rig = new Rig(Log1, Stamp + "old line one\n" + Stamp + "old line two\n"))
            {
                rig.Tailer.Poll();
                using (var fs = new FileStream(rig.PathOf(Log1), FileMode.Open, FileAccess.Write,
                           FileShare.ReadWrite | FileShare.Delete)) fs.SetLength(0);
                rig.Append(Log1, Stamp + "[Behaviour] Entering Room: After Truncation\n");
                Assert.True(rig.Tailer.Poll());
                Assert.Equal(new[] { "world:After Truncation" }, rig.Events);
            }
        }

        [Fact]
        public void ANewerLogIsFollowedFromItsBeginning()
        {
            using (var rig = new Rig(Log1, Stamp + "User Authenticated: Tester (usr_1)\n"))
            {
                rig.Tailer.Poll();
                File.WriteAllText(rig.PathOf(Log2),
                    Stamp + "User Authenticated: Tester (usr_1)\n" + Stamp +
                    "[Behaviour] Entering Room: Next Session\n");
                File.SetLastWriteTimeUtc(rig.PathOf(Log2), DateTime.UtcNow.AddMinutes(1));
                Assert.True(rig.Tailer.Poll(true));
                Assert.Equal(rig.PathOf(Log2), rig.Tailer.CurrentFile);
                Assert.Equal(new[] { "world:Next Session" }, rig.Events);
                // the old log can be deleted while the new one is followed, and the new one too while it is open
                File.Delete(rig.PathOf(Log1));
                rig.Append(Log2, Stamp + "[Behaviour] Entering Room: Still Following\n");
                rig.Tailer.Poll();
                Assert.Equal("Still Following", rig.Tailer.WorldName);
                File.Delete(rig.PathOf(Log2));
            }
        }

        [Fact]
        public void ALogDeletedAndWrittenAnewUnderTheSameNameIsPickedUp()
        {
            using (var rig = new Rig(Log1, Stamp + "one\n" + Stamp + "two\n" + Stamp + "three\n"))
            {
                rig.Tailer.Poll();
                File.Delete(rig.PathOf(Log1));
                File.WriteAllText(rig.PathOf(Log1), Stamp + "[Behaviour] Entering Room: Replaced\n");
                rig.Tailer.Poll(true);
                Assert.Equal("Replaced", rig.Tailer.WorldName);
            }
        }

        [Fact]
        public void TheWriterIsNeverBlocked()
        {
            using (var rig = new Rig(Log1))
            {
                rig.Tailer.Poll();
                // an exclusive-as-possible writer, as a game would hold its log: it only allows others to read
                using (var game = new FileStream(rig.PathOf(Log1), FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    byte[] b = Encoding.UTF8.GetBytes(Stamp + "[Behaviour] Entering Room: Held Open\n");
                    game.Write(b, 0, b.Length);
                    game.Flush();
                    rig.Tailer.Poll();
                    Assert.Equal("Held Open", rig.Tailer.WorldName);
                }
            }
        }

        [Fact]
        public void AVeryLongLineDoesNotStopTheLinesAfterIt()
        {
            using (var rig = new Rig(Log1))
            {
                rig.Tailer.Poll();
                rig.Append(Log1,
                    Stamp + new string('x', 300000) + "\n" + Stamp +
                    "[Behaviour] Entering Room: After The Long Line\n");
                rig.Tailer.Poll();
                Assert.Equal(new[] { "world:After The Long Line" }, rig.Events);
            }
        }

        /// <summary>A log folder with a tailer that the test drives by hand, and a writer that appends like VRChat does.</summary>
        sealed class Rig : IDisposable
        {
            public readonly List<string> Events = new();
            public readonly List<VrcLogTailer.HumrFrame> Frames = new();
            public readonly VrcLogTailer Tailer;
            readonly TempDir _tmp = new();

            public Rig(string existingLog = null, string content = "")
            {
                if (existingLog != null) File.WriteAllText(Path.Combine(_tmp.Path, existingLog), content);
                Tailer = new VrcLogTailer(_tmp.Path, false)
                    { OnEvent = (type, text) => Events.Add(type + ":" + text), OnHumr = Frames.Add };
            }

            public void Dispose()
            {
                Tailer.Dispose();
                _tmp.Dispose();
            }

            public string PathOf(string log)
            {
                return Path.Combine(_tmp.Path, log);
            }

            public void Append(string log, string text)
            {
                using (var fs = new FileStream(PathOf(log), FileMode.Append, FileAccess.Write,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] b = Encoding.UTF8.GetBytes(text);
                    fs.Write(b, 0, b.Length);
                }
            }
        }
    }

    public class OscTests
    {
        static byte[] Pad(byte[] b)
        {
            byte[] r = new byte[(b.Length / 4 + 1) * 4];
            Array.Copy(b, r, b.Length);
            return r;
        }

        static byte[] Str(string s)
        {
            return Pad(Encoding.UTF8.GetBytes(s));
        }

        static byte[] Int(int v)
        {
            return new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        }

        static byte[] Float(float v)
        {
            return Int(BitConverter.SingleToInt32Bits(v));
        }

        static byte[] Join(params byte[][] parts)
        {
            return parts.SelectMany(p => p).ToArray();
        }

        static byte[] Message(string address, string tags, params byte[][] args)
        {
            return Join(new[] { Str(address), Str("," + tags) }.Concat(args).ToArray());
        }

        /// <summary>Sends packets to a receiver on a free loopback port and returns copies of what it delivered.</summary>
        static List<(string Address, int Argc, string Tags, float F0, string S)> Receive(params byte[][] packets)
        {
            var got = new List<(string, int, string, float, string)>();
            using (var done = new ManualResetEventSlim(false))
            using (var rx = new OscReceiver("test", 0))
            using (var udp = new UdpClient(AddressFamily.InterNetwork))
            {
                rx.OnMessage = m =>
                {
                    lock (got)
                        got.Add((m.Address, m.Argc, new string(m.Tag, 0, m.Argc), m.Argc > 0 ? m.Float(0) : 0, m.S));
                    if (m.Address == "/end") done.Set();
                };
                var to = new IPEndPoint(IPAddress.Loopback, rx.Port);
                foreach (byte[] p in packets) udp.Send(p, p.Length, to);
                byte[] end = Message("/end", "");
                udp.Send(end, end.Length, to);
                Assert.True(done.Wait(5000), "the receiver did not deliver the messages");
            }

            lock (got) return got.Where(g => g.Item1 != "/end").ToList();
        }

        [Fact]
        public void FloatsIntsBoolsAndStringsAreDecoded()
        {
            var got = Receive(
                Message("/avatar/parameters/FT/v2/JawOpen", "f", Float(0.75f)),
                Message("/avatar/parameters/VRCEmote", "i", Int(3)),
                Message("/avatar/parameters/IsLocal", "T"),
                Message("/avatar/parameters/Seated", "F"),
                Message("/avatar/change", "s", Str("avtr_00000000-0000-0000-0000-000000000001")));
            Assert.Equal(5, got.Count);
            Assert.Equal(("/avatar/parameters/FT/v2/JawOpen", 1, "f", 0.75f),
                (got[0].Address, got[0].Argc, got[0].Tags, got[0].F0));
            Assert.Equal(3f, got[1].F0);
            Assert.Equal(("T", 1f), (got[2].Tags, got[2].F0));
            Assert.Equal(("F", 0f), (got[3].Tags, got[3].F0));
            Assert.Equal("avtr_00000000-0000-0000-0000-000000000001", got[4].S);
        }

        [Fact]
        public void BundlesAreUnpackedAndPosesKeepTheirSixFloats()
        {
            byte[] pose = Message("/tracking/vrsystem/head/pose", "ffffff", Float(0.1f), Float(1.6f), Float(-0.2f),
                Float(10), Float(20), Float(30));
            byte[] param = Message("/avatar/parameters/GestureLeft", "i", Int(2));
            byte[] inner = Join(Str("#bundle"), new byte[8], Int(param.Length), param);
            byte[] bundle = Join(Str("#bundle"), new byte[8], Int(pose.Length), pose, Int(inner.Length), inner);
            var got = Receive(bundle);
            Assert.Equal(2, got.Count);
            Assert.Equal(("/tracking/vrsystem/head/pose", 6, "ffffff", 0.1f),
                (got[0].Address, got[0].Argc, got[0].Tags, got[0].F0));
            Assert.Equal(("/avatar/parameters/GestureLeft", 2f), (got[1].Address, got[1].F0));
        }

        [Fact]
        public void BrokenPacketsAreSkippedWithoutStoppingTheReceiver()
        {
            byte[] cut = Message("/avatar/parameters/Cut", "f", Float(1f)).Take(30)
                .ToArray(); // the float is missing its last bytes
            var got = Receive(new byte[] { 1, 2, 3 }, cut, Message("/avatar/parameters/Blob", "b", Int(0)),
                Message("/avatar/parameters/Ok", "f", Float(0.5f)));
            Assert.Equal(new[] { "/avatar/parameters/Ok" }, got.Select(g => g.Address).ToArray());
        }
    }

    public class DeviceSnapshotTests
    {
        [Fact]
        public void ADeviceCopyCarriesEveryValue()
        {
            var a = new OpenVRSource.Device
            {
                Index = 3, Serial = "S", Model = "M", ControllerType = "c", TrackingSystem = "t", Manufacturer = "m",
                RenderModel = "r", Class = 3, Role = 1, Valid = true, HasPose = true,
                Pos = new Vec3(1, 2, 3), Rot = Quat.YawRotation(30), Result = 200, LastCode = 200, Battery = 0.5f,
                Charging = true, Wireless = true,
                FovL = 1, FovR = 2, FovT = 3, FovB = 4, RangeMin = 5, RangeMax = 6, ModeLabel = "x",
                TrackerRole = "Waist"
            };
            var b = new OpenVRSource.Device();
            b.CopyFrom(a);
            foreach (var field in typeof(OpenVRSource.Device).GetFields())
                Assert.Equal(field.GetValue(a), field.GetValue(b));
        }

        [Fact]
        public void ASourceThatIsNotConnectedFillsNothing()
        {
            using (var source = new OpenVRSource(90)) // never started: no thread, no SteamVR
            {
                var buffer = new OpenVRSource.Device[OpenVRSource.MaxDevices];
                Assert.Equal(0, source.CopyDevices(buffer));
                Assert.Equal(0, source.CopyDevices(buffer, OpenVRSource.DeviceFilter.TrackedBody));
                Assert.All(buffer, Assert.Null);
            }
        }
    }
}
