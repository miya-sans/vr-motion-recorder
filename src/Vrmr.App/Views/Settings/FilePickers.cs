using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace VRMR.App.Views.Settings
{
    /// <summary>The file and folder pickers of Settings and the first-run guide, and the one confirm question. Each returns null when cancelled.</summary>
    static class FilePickers
    {
        /// <summary>Asks for a folder, starting in <paramref name="start"/> when it exists.</summary>
        public static async Task<string> FolderAsync(Visual from, string title, string start)
        {
            var top = TopLevel.GetTopLevel(from);
            if (top == null) return null;
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title, AllowMultiple = false, SuggestedStartLocation = await StartAsync(top, start),
            });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }

        /// <summary>Asks for the files an avatar ships with: its package or archive and a face-tracking add-on, or a Unity model.</summary>
        public static async Task<IReadOnlyList<string>> AvatarFilesAsync(Visual from)
        {
            var top = TopLevel.GetTopLevel(from);
            if (top == null) return null;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Pick the avatar's .unitypackage (or .zip) and its face-tracking add-on, or a Unity model (.fbx with its .meta)",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Avatar files") { Patterns = new[] { "*.unitypackage", "*.zip", "*.fbx", "*.meta" } },
                    FilePickerFileTypes.All,
                },
            });
            return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList();
        }

        /// <summary>Asks for a humanoid model inside a Unity project.</summary>
        public static async Task<string> UnityModelAsync(Visual from, string start)
        {
            var top = TopLevel.GetTopLevel(from);
            if (top == null) return null;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Pick your avatar's model inside a Unity project (its rig must be Humanoid)", AllowMultiple = false,
                SuggestedStartLocation = await StartAsync(top, start),
                FileTypeFilter = new[] { new FilePickerFileType("Humanoid model") { Patterns = new[] { "*.fbx", "*.meta", "*.blend" } } },
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }

        /// <summary>Asks for Blender's program file.</summary>
        public static async Task<string> BlenderAsync(Visual from)
        {
            var top = TopLevel.GetTopLevel(from);
            if (top == null) return null;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Pick Blender's program file", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Blender") { Patterns = new[] { OperatingSystem.IsWindows() ? "blender.exe" : "blender" } }, FilePickerFileTypes.All },
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }

        /// <summary>Asks where to save an FBX file.</summary>
        public static async Task<string> SaveFbxAsync(Visual from, string suggestedName, string start)
        {
            var top = TopLevel.GetTopLevel(from);
            if (top == null) return null;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the FBX with face shapes", SuggestedFileName = suggestedName, DefaultExtension = "fbx", ShowOverwritePrompt = true,
                SuggestedStartLocation = await StartAsync(top, start),
                FileTypeChoices = new[] { new FilePickerFileType("FBX") { Patterns = new[] { "*.fbx" } } },
            });
            return file?.TryGetLocalPath();
        }

        /// <summary>Asks a question about something that cannot be undone from inside the app.</summary>
        public static async Task<bool> ConfirmAsync(Visual from, string question, string detail, string confirmLabel)
        {
            if (TopLevel.GetTopLevel(from) is not Window owner) return false;
            return await new ConfirmDialog(question, detail, confirmLabel).ShowDialog<bool>(owner);
        }

        static async Task<IStorageFolder> StartAsync(TopLevel top, string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            return await top.StorageProvider.TryGetFolderFromPathAsync(folder);
        }
    }
}
