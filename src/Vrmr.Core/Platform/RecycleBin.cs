using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VRMR
{
    /// <summary>
    ///     Deletes files and folders so that the user can get them back: to the Recycle Bin on Windows, to the desktop's
    ///     trash elsewhere.
    /// </summary>
    public static class RecycleBin
    {
        /// <summary>
        ///     Moves a file or a folder (with everything in it) to the Recycle Bin, without asking and without dialogs.
        ///     Does nothing when the path does not exist; throws <see cref="IOException" /> when it could not be moved.
        /// </summary>
        public static void Delete(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            path = Path.GetFullPath(path);
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            if (OperatingSystem.IsWindows()) ShellDelete(path);
            else TrashDelete(path);
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException("Could not move to the Recycle Bin: " + path);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        static extern int SHFileOperation(ref FileOp op);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        static extern int SHFileOperation32(ref FileOp32 op);

        static void ShellDelete(string path)
        {
            const uint Delete = 3;
            const ushort Silent = 0x4, NoConfirmation = 0x10, AllowUndo = 0x40, NoErrorUi = 0x400;
            IntPtr from = Marshal.StringToHGlobalUni(path + "\0"); // the list of paths ends with a second terminator
            try
            {
                const ushort flags = Silent | NoConfirmation | AllowUndo | NoErrorUi;
                int result;
                bool aborted;
                if (IntPtr.Size == 8)
                {
                    var op = new FileOp { Function = Delete, From = from, Flags = flags };
                    result = SHFileOperation(ref op);
                    aborted = op.AnyAborted != 0;
                }
                else
                {
                    var op = new FileOp32 { Function = Delete, From = from, Flags = flags };
                    result = SHFileOperation32(ref op);
                    aborted = op.AnyAborted != 0;
                }

                if (result != 0 || aborted)
                    throw new IOException($"Could not move to the Recycle Bin (shell error {result}): {path}");
            }
            finally { Marshal.FreeHGlobal(from); }
        }

        // ------------------------------------------------------------------ other desktops (freedesktop.org trash)

        static void TrashDelete(string path)
        {
            string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(data))
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local",
                    "share");
            string files = Path.Combine(data, "Trash", "files"), info = Path.Combine(data, "Trash", "info");
            Directory.CreateDirectory(files);
            Directory.CreateDirectory(info);
            string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            string target = Path.Combine(files, name);
            for (int k = 2; File.Exists(target) || Directory.Exists(target); k++)
                target = Path.Combine(files, name + "." + k);
            File.WriteAllText(Path.Combine(info, Path.GetFileName(target) + ".trashinfo"),
                "[Trash Info]\nPath=" + Uri.EscapeDataString(path).Replace("%2F", "/") + "\nDeletionDate=" +
                DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + "\n");
            if (Directory.Exists(path)) Directory.Move(path, target);
            else File.Move(path, target);
        }

        // ------------------------------------------------------------------ Windows

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FileOp
        {
            public IntPtr Window;
            public uint Function;
            public IntPtr From, To;
            public ushort Flags;
            public int AnyAborted;
            public IntPtr NameMappings, ProgressTitle;
        }

        /// <summary>The same structure as 32-bit Windows lays it out (byte-packed).</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        struct FileOp32
        {
            public IntPtr Window;
            public uint Function;
            public IntPtr From, To;
            public ushort Flags;
            public int AnyAborted;
            public IntPtr NameMappings, ProgressTitle;
        }
    }
}
