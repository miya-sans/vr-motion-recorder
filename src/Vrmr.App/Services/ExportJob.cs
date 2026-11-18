using System;
using System.Collections.Generic;
using System.IO;

namespace VRMR.App.Services
{
    /// <summary>One solved clip of an export: the whole take, or one avatar's stretch of it.</summary>
    public sealed class ExportPart
    {
        public Recording Take { get; init; }
        public BakedClip Clip { get; init; }
        /// <summary>The folder its files go into.</summary>
        public string Dir { get; init; } = "";
        /// <summary>What every file name of the part starts with.</summary>
        public string Name { get; init; } = "";

        /// <summary>The path of one of its files: the name plus a suffix ("_osc.json").</summary>
        public string PathFor(string suffix) => Path.Combine(Dir, Name + suffix);
    }

    /// <summary>What one export writes; the chosen formats and options fill it in before the export starts.</summary>
    public sealed class ExportJob
    {
        /// <summary>The files of the clip writer the scene export shares (FBX, Unity clips, face .json); all off until a format asks for one.</summary>
        public ClipExportOptions Clip { get; } = new ClipExportOptions
        {
            Fbx = false, FbxFace = false, FbxArmature = false, FbxCamera = false, FbxTrackers = false,
            BodyAnim = false, FaceAnim = false, ParamsAnim = false, FaceJson = false,
        };

        /// <summary>Formats with a writer of their own: each writes one file of a part and returns its path, or null when the part has nothing for it.</summary>
        public List<Func<ExportPart, string>> Writers { get; } = new List<Func<ExportPart, string>>();

        /// <summary>The ids of the formats that are on.</summary>
        public List<string> Formats { get; } = new List<string>();

        /// <summary>True when one of the FBX files is written (they get a manifest next to them).</summary>
        public bool AnyFbx => Clip.Fbx || Clip.FbxArmature;
    }

    /// <summary>How an export ended: the files written and the notes about them, or why nothing was written.</summary>
    public sealed class ExportResult
    {
        /// <summary>Success, Warning (written with notes, or skipped) or Error.</summary>
        public StatusKind Kind { get; init; }
        /// <summary>True when the files were written.</summary>
        public bool Exported { get; init; }
        /// <summary>One or two sentences in plain words: what was written where, or what went wrong and what to do.</summary>
        public string Message { get; init; } = "";
        public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
        /// <summary>Things worth knowing about the files (an avatar without an FBX, say).</summary>
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
        /// <summary>The folder the files are in (null when nothing was written).</summary>
        public string Folder { get; init; }
        /// <summary>Clips written: one, or one per avatar worn during the take.</summary>
        public int Parts { get; init; }

        /// <summary>Nothing was written and nothing is broken: something has to be chosen or done first.</summary>
        public static ExportResult Skipped(string message) => new ExportResult { Kind = StatusKind.Warning, Message = message };

        /// <summary>The export started and failed.</summary>
        public static ExportResult Failed(string message) => new ExportResult { Kind = StatusKind.Error, Message = message };

        /// <summary>The files were written.</summary>
        public static ExportResult Done(IReadOnlyList<string> files, IReadOnlyList<string> notes, string folder, int parts) => new ExportResult
        {
            Kind = notes.Count > 0 ? StatusKind.Warning : StatusKind.Success, Exported = true, Files = files, Notes = notes, Folder = folder, Parts = parts,
            Message = $"Exported {files.Count} {(files.Count == 1 ? "file" : "files")}" + (parts > 1 ? $" in {parts} parts (one per avatar)" : "") + $" to {folder}",
        };
    }
}
