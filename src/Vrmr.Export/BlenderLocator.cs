using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace VRMR
{
    /// <summary>Finds the Blender to open exports in, and writes the startup script that imports them.</summary>
    public static class BlenderLocator
    {
        /// <summary>The Blender to use: the user's own (<paramref name="userPath"/>, Settings.BlenderPath) when it is valid, else the newest release found; null when there is none.</summary>
        public static string Find(string userPath = null) => FindAll(userPath).FirstOrDefault()?.Exe;

        /// <summary>Every Blender found on the machine in use, best first (see <see cref="BlenderInstalls"/>).</summary>
        public static List<BlenderInstall> FindAll(string userPath = null) => BlenderInstalls.Find(AppPaths.Current.Machine, userPath);

        /// <summary>Full version from the build folder name (blender-5.3.0-stable.hash), else the exe's file version (5.3).</summary>
        public static Version VersionOf(string exe) => BlenderInstalls.VersionOf(exe);

        /// <summary>The bundled add-on (blender\vrmr_bridge.py next to the app or one folder up), or null.</summary>
        public static string BundledAddon()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var p in new[] { Path.Combine(baseDir, "blender", "vrmr_bridge.py"), Path.Combine(baseDir, "..", "blender", "vrmr_bridge.py") })
            {
                string full = Path.GetFullPath(p);
                if (File.Exists(full)) return full;
            }
            return null;
        }

        /// <summary>Startup script: import through the VRMR add-on (installed, or loaded from the bundled copy) so the
        /// face/avatar parameters become armature curves; plain FBX import with the same settings as a fallback.</summary>
        public static string OpenScript(string fbx, int fps, string addon)
        {
            return
                "import bpy, os, sys, importlib.util\n" +
                "bpy.ops.wm.read_homefile(use_empty=True)\n" +
                $"fbx = r'''{fbx}'''\n" +
                $"addon = r'''{addon ?? ""}'''\n" +
                "def have_vrmr():\n" +
                "    return 'import_fbx' in dir(bpy.ops.vrmr)\n" +
                "if not have_vrmr() and addon and os.path.exists(addon):\n" +
                "    try:\n" +
                "        spec = importlib.util.spec_from_file_location('vrmr_bridge', addon)\n" +
                "        mod = importlib.util.module_from_spec(spec); spec.loader.exec_module(mod)\n" +
                "        sys.modules['vrmr_bridge'] = mod; mod.register()\n" +
                "    except Exception as e:\n" +
                "        print('VRMR add-on could not be loaded:', e)\n" +
                "scn = bpy.context.scene\n" +
                "if have_vrmr():\n" +
                "    bpy.ops.vrmr.import_fbx(filepath=fbx)\n" +
                "else:\n" +
                "    bpy.ops.import_scene.fbx(filepath=fbx, use_anim=True, anim_offset=0.0, ignore_leaf_bones=False, automatic_bone_orientation=False)\n" +
                $"    scn.render.fps = {fps}; scn.render.fps_base = 1.0\n" +
                "    acts = list(bpy.data.actions)\n" +
                "    if acts:\n" +
                "        scn.frame_start = int(min(a.frame_range[0] for a in acts)); scn.frame_end = int(max(a.frame_range[1] for a in acts))\n";
        }

        /// <summary>
        /// Opens an export in Blender (the user's own, else the newest found): a startup script imports the manifest (take
        /// or scene) through the bundled add-on. The script goes to the temp folder when the export is inside a Unity
        /// project (Unity would import it). Returns an error message, or null when Blender was started.
        /// </summary>
        public static string OpenExport(string manifest, bool inUnityProject, string blenderPath = null)
        {
            string exe = Find(blenderPath);
            if (exe == null) return "Blender was not found. Install it, or set its path in Settings.";
            try
            {
                string dir = inUnityProject ? Path.GetTempPath() : Path.GetDirectoryName(manifest);
                string script = Path.Combine(dir, "_vrmr_open_" + Path.GetFileName(manifest).Replace(SceneExport.ManifestSuffix, "").Replace(' ', '_') + ".py");
                File.WriteAllText(script, OpenSceneScript(manifest, BundledAddon()));
                AppLog.Info("Opening in Blender " + VersionOf(exe) + ": " + exe + " <- " + manifest);
                Process.Start(new ProcessStartInfo(exe, $"--python \"{script}\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
                return null;
            }
            catch (Exception ex) { return "Could not start Blender: " + ex.Message; }
        }

        /// <summary>Startup script for a scene export: import the manifest (every actor, frame range, fps, markers) through
        /// the VRMR add-on; without the add-on, a plain FBX import of every actor with the same settings.</summary>
        public static string OpenSceneScript(string manifest, string addon)
        {
            return
                "import bpy, os, sys, json, importlib.util\n" +
                "bpy.ops.wm.read_homefile(use_empty=True)\n" +
                $"man = r'''{manifest}'''\n" +
                $"addon = r'''{addon ?? ""}'''\n" +
                "def have_vrmr():\n" +
                "    return 'import_scene' in dir(bpy.ops.vrmr)\n" +
                "if not have_vrmr() and addon and os.path.exists(addon):\n" +
                "    try:\n" +
                "        spec = importlib.util.spec_from_file_location('vrmr_bridge', addon)\n" +
                "        mod = importlib.util.module_from_spec(spec); spec.loader.exec_module(mod)\n" +
                "        sys.modules['vrmr_bridge'] = mod; mod.register()\n" +
                "    except Exception as e:\n" +
                "        print('VRMR add-on could not be loaded:', e)\n" +
                "scn = bpy.context.scene\n" +
                "if have_vrmr():\n" +
                "    bpy.ops.vrmr.import_scene(filepath=man)\n" +
                "else:\n" +
                "    d = json.load(open(man, encoding='utf-8')); base = os.path.dirname(man)\n" +
                "    for a in d.get('actors', []):\n" +
                "        if a.get('fbx'):\n" +
                "            bpy.ops.import_scene.fbx(filepath=os.path.join(base, a['fbx']), use_anim=True, anim_offset=0.0, ignore_leaf_bones=False, automatic_bone_orientation=False)\n" +
                "    scn.render.fps = int(round(d.get('fps', 60))); scn.render.fps_base = 1.0\n" +
                "    scn.frame_start = int(d.get('frameStart', 0)); scn.frame_end = int(d.get('frameEnd', 250))\n" +
                "    for m in d.get('markers', []):\n" +
                "        scn.timeline_markers.new(m.get('name', ''), frame=int(m.get('frame', 0)))\n";
        }
    }
}
