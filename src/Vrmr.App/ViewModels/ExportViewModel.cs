using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VRMR.App.Composition;
using VRMR.App.Controls;
using VRMR.App.Services;

namespace VRMR.App.ViewModels
{
    /// <summary>One check box of the Export tab: a format, or an option of one.</summary>
    public partial class ExportChoiceViewModel : ObservableObject
    {
        readonly ExportService _export;
        readonly ExportChoice _choice;

        public ExportChoiceViewModel(ExportService export, ExportChoice choice)
        {
            _export = export; _choice = choice;
            Refresh();
        }

        public string Title => _choice.Title;
        public string Description => _choice.Description;

        /// <summary>An option of the format right above it: shown indented.</summary>
        public bool IsNested => _choice is ExportOption option && option.IsNested;

        [ObservableProperty] bool _isOn;
        /// <summary>False for an option while none of its formats is on.</summary>
        [ObservableProperty] bool _isAvailable = true;

        partial void OnIsOnChanged(bool value) => _export.SetOn(_choice, value);

        /// <summary>Reads the state again (another choice or the settings changed).</summary>
        public void Refresh()
        {
            IsOn = _export.IsOn(_choice);
            IsAvailable = !(_choice is ExportOption option) || _export.IsAvailable(option);
        }
    }

    /// <summary>
    /// The Export tab of the Takes inspector: the folder, the formats (built from the export catalogue), the options,
    /// and the export with its progress and result. The work is done by <see cref="ExportService"/>.
    /// </summary>
    public partial class ExportViewModel : ObservableObject
    {
        static readonly string[] FaceNamingIds = { "UnifiedExpressions", "ARKit" };

        readonly AppHost _host;
        readonly ExportService _export;
        readonly TakeSession _session;
        readonly List<ExportChoiceViewModel> _choices = new List<ExportChoiceViewModel>();
        bool _refreshing;
        string _shownFolder;
        string _resultFolder;

        public ExportViewModel(AppHost host)
        {
            _host = host; _export = host.Export; _session = host.Session;
            MainFormats = Choices(ExportGroup.Main);
            MoreFormats = Choices(ExportGroup.More);
            FrameRates = ExportService.FrameRates.Select(f => f + " fps").ToArray();
            _export.Changed += Refresh;
            _export.ProgressChanged += OnProgress;
            _session.TakeChanged += OnTakeChanged;
            Refresh();
            OnTakeChanged();
        }

        /// <summary>The formats shown right away, each followed by its options.</summary>
        public IReadOnlyList<ExportChoiceViewModel> MainFormats { get; }
        /// <summary>The formats behind "More formats".</summary>
        public IReadOnlyList<ExportChoiceViewModel> MoreFormats { get; }
        public bool HasMoreFormats => MoreFormats.Count > 0;

        public IReadOnlyList<string> FrameRates { get; }
        public IReadOnlyList<string> FaceNamings { get; } = new[] { "Unified Expressions", "ARKit" };

        /// <summary>Asks the view for a folder, starting in the one given; null when the picker was closed.</summary>
        public Func<string, Task<string>> PickFolder { get; set; }

        [ObservableProperty] bool _hasTake;

        [ObservableProperty] string _folderText = "";
        [ObservableProperty] string _folderError = "";
        [ObservableProperty] string _folderSummary = "";
        [ObservableProperty] bool _hasUnityProject;
        [ObservableProperty] bool _takeFolders;

        [ObservableProperty] string _formatsSummary = "";
        [ObservableProperty] Tone _formatsTone;
        [ObservableProperty] string _moreSummary = "";

        [ObservableProperty] int _frameRateIndex = -1;
        [ObservableProperty] int _faceNamingIndex;
        [ObservableProperty] string _faceMeshPath = "";
        [ObservableProperty] bool _splitAtAvatarChanges;
        [ObservableProperty] string _optionsSummary = "";

        [ObservableProperty] bool _isExporting;
        [ObservableProperty] string _progressText = "";
        [ObservableProperty] double _progressValue;
        [ObservableProperty] bool _progressIndeterminate = true;

        [ObservableProperty] bool _hasResult;
        [ObservableProperty] string _resultText = "";
        [ObservableProperty] bool _resultIsSuccess;
        [ObservableProperty] bool _resultIsWarning;
        [ObservableProperty] bool _resultIsError;
        [ObservableProperty] IReadOnlyList<string> _resultNotes = Array.Empty<string>();
        [ObservableProperty] bool _canOpenResult;

        List<ExportChoiceViewModel> Choices(ExportGroup group)
        {
            var list = new List<ExportChoiceViewModel>();
            foreach (var format in _export.Formats.Where(f => f.Group == group))
            {
                list.Add(new ExportChoiceViewModel(_export, format));
                list.AddRange(format.Options.Select(option => new ExportChoiceViewModel(_export, option)));
            }
            _choices.AddRange(list);
            return list;
        }

        // ------------------------------------------------------------------------------------------ state

        /// <summary>Shows what the service and the settings say now.</summary>
        void Refresh()
        {
            _refreshing = true;
            try
            {
                var folders = _export.Folders;
                string folder = folders.ExportDir;
                // only when the folder itself changed: a path being typed is left alone
                if (folder != _shownFolder) { _shownFolder = folder; FolderText = folder; }
                FolderSummary = folders.Summary;
                HasUnityProject = folders.UnityProjectRoot != null;
                TakeFolders = folders.TakeFolders;

                foreach (var choice in _choices) choice.Refresh();
                int all = _export.CountOn(), more = _export.CountOn(ExportGroup.More);
                FormatsSummary = all == 0 ? "none chosen" : all == 1 ? "1 format" : all + " formats";
                FormatsTone = all == 0 ? Tone.Warning : Tone.Neutral;
                MoreSummary = more == 0 ? "" : more + " on";

                int rate = _export.FrameRate;
                int rateIndex = -1;
                for (int i = 0; i < ExportService.FrameRates.Count; i++) if (ExportService.FrameRates[i] == rate) rateIndex = i;
                FrameRateIndex = rateIndex;
                FaceNamingIndex = _export.FaceNaming == "ARKit" ? 1 : 0;
                if ((FaceMeshPath ?? "").Trim() != _export.FaceMeshPath) FaceMeshPath = _export.FaceMeshPath;
                SplitAtAvatarChanges = _export.SplitAtAvatarChanges;
                OptionsSummary = rate + " fps";
            }
            finally { _refreshing = false; }
        }

        void OnTakeChanged()
        {
            HasTake = _session.Take != null;
            if (!IsExporting) ClearResult(); // a result belongs to the take it was made from
        }

        void OnProgress()
        {
            IsExporting = _export.IsExporting;
            ProgressText = _export.ProgressText;
            ProgressIndeterminate = _export.ProgressFraction < 0;
            ProgressValue = Math.Max(0, Math.Min(1, _export.ProgressFraction));
        }

        partial void OnTakeFoldersChanged(bool value) { if (!_refreshing) _export.Folders.TakeFolders = value; }
        partial void OnFrameRateIndexChanged(int value) { if (!_refreshing && value >= 0 && value < ExportService.FrameRates.Count) _export.FrameRate = ExportService.FrameRates[value]; }
        partial void OnFaceNamingIndexChanged(int value) { if (!_refreshing && value >= 0 && value < FaceNamingIds.Length) _export.FaceNaming = FaceNamingIds[value]; }
        partial void OnFaceMeshPathChanged(string value) { if (!_refreshing) _export.FaceMeshPath = value; }
        partial void OnSplitAtAvatarChangesChanged(bool value) { if (!_refreshing) _export.SplitAtAvatarChanges = value; }

        // ------------------------------------------------------------------------------------------ folder

        /// <summary>Makes the path in the folder box the export folder (Enter, or leaving the box).</summary>
        [RelayCommand]
        void CommitFolder() => SetFolder(FolderText);

        /// <summary>Sets the export folder; a path that cannot be one leaves the folder as it was and says why.</summary>
        bool SetFolder(string path)
        {
            bool ok = _export.Folders.SetExportDir(path, out string error);
            FolderError = error ?? "";
            _shownFolder = _export.Folders.ExportDir;
            FolderText = _shownFolder;
            return ok;
        }

        [RelayCommand]
        async Task Browse()
        {
            if (PickFolder == null) return;
            string folder = _export.Folders.ExportDir;
            string picked = await PickFolder(Directory.Exists(folder) ? folder : _host.Settings.RecordingsDir);
            if (picked != null) SetFolder(picked);
        }

        [RelayCommand]
        void UseUnityProject()
        {
            string folder = _export.Folders.UnityExportDir;
            if (folder != null) SetFolder(folder);
        }

        [RelayCommand]
        void OpenFolder()
        {
            string folder = _export.Folders.FolderToOpen;
            if (folder != null) FileActions.OpenFolder(folder);
            else _host.Status.Info("The export folder does not exist yet: the first export creates it.");
        }

        // ------------------------------------------------------------------------------------------ export

        [RelayCommand]
        async Task Export()
        {
            // a path typed but not confirmed yet counts
            if ((FolderText ?? "").Trim() != _export.Folders.ExportDir && !SetFolder(FolderText)) { Show(ExportResult.Skipped(FolderError)); return; }
            ClearResult();
            Show(await _export.ExportAsync());
        }

        [RelayCommand]
        void OpenResult()
        {
            if (_resultFolder != null) FileActions.OpenFolder(_resultFolder);
        }

        void Show(ExportResult result)
        {
            ResultText = result.Message;
            ResultIsSuccess = result.Exported;
            ResultIsWarning = !result.Exported && result.Kind == StatusKind.Warning;
            ResultIsError = result.Kind == StatusKind.Error;
            ResultNotes = result.Notes;
            _resultFolder = result.Exported ? result.Folder : null;
            CanOpenResult = _resultFolder != null;
            HasResult = true;
        }

        void ClearResult()
        {
            HasResult = false;
            ResultText = "";
            ResultIsSuccess = ResultIsWarning = ResultIsError = false;
            ResultNotes = Array.Empty<string>();
            _resultFolder = null;
            CanOpenResult = false;
        }
    }
}
