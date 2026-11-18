using System;
using System.IO;
using System.Linq;

namespace VRMR.Cli
{
    /// <summary>vrmr: command-line access to takes, solves and exports, for tests, batch work and benchmarks.</summary>
    static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "help")
            {
                Usage();
                return 0;
            }

            try
            {
                switch (args[0])
                {
                    case "info": return Info(args.Skip(1).ToArray());
                    case "dump": return DumpCommand.Run(args.Skip(1).ToArray());
                    case "humanoid": return HumanoidCommand.Run(args.Skip(1).ToArray());
                    default:
                        Console.Error.WriteLine("Unknown command: " + args[0]);
                        Usage();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("vrmr: " + ex.Message);
                return 1;
            }
        }

        static void Usage()
        {
            Console.WriteLine("vrmr <command> [arguments]");
            Console.WriteLine("  info <take.vrmr>                         what a take contains");
            Console.WriteLine("  dump <takesDir> <outDir> [filter] [--no-exports]");
            Console.WriteLine(
                "                                           solve every take and write hashes and raw curves (regression checks)");
            Console.WriteLine(
                "  humanoid <takesDir> <outDir> [filter]    write each take's Humanoid clip and report how closely it follows the solve");
        }

        static int Info(string[] a)
        {
            if (a.Length < 1)
            {
                Usage();
                return 2;
            }

            var r = RecordingIO.Load(Path.GetFullPath(a[0]));
            Console.WriteLine($"name      {r.Name}");
            Console.WriteLine($"created   {r.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"duration  {r.Duration:0.00} s");
            Console.WriteLine(
                $"avatar    {(string.IsNullOrEmpty(r.AvatarName) ? "(unnamed)" : r.AvatarName)}, skeleton {(r.Profile != null ? "embedded" : "not embedded")}");
            Console.WriteLine($"devices   {r.Streams.Count} pose streams");
            Console.WriteLine($"params    {r.Params.Count} avatar parameters");
            Console.WriteLine($"humr      {r.Humr.Count} VRChat IK frames");
            Console.WriteLine($"events    {r.Events.Count}");
            return 0;
        }
    }
}
