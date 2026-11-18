using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VRMR.App.Composition;
using VRMR.App.Services;

namespace VRMR.App.ViewModels
{
    /// <summary>One base model in the avatar library list.</summary>
    public sealed class AvatarRowViewModel
    {
        public AvatarEntry Entry { get; init; }
        public string Name { get; init; } = "";

        /// <summary>"eye height 1.32 m · 2 linked".</summary>
        public string Summary { get; init; } = "";

        public bool IsDefault { get; init; }
        public bool IsBuiltIn { get; init; }

        /// <summary>The model file is gone: the take still solves, the 3D view shows the skeleton only.</summary>
        public bool FbxMissing { get; init; }
    }

    /// <summary>
    ///     Settings → Avatars: the base models takes are solved onto. Add one from the files an avatar ships with, name
    ///     it, say which VRChat avatar names mean it, pick the default, remove it; and the two things that only matter
    ///     for avatars living in a Unity project (linking the avatar worn now, an FBX copy with face shapes).
    /// </summary>
    public partial class SettingsAvatarsViewModel : ObservableObject
    {
        readonly AppHost _host;
        readonly AvatarService _service;

        /// <summary>The selected entry is the user's own (the built-in mannequin cannot be renamed or removed).</summary>
        [ObservableProperty] bool _canEdit;

        [ObservableProperty] bool _canMakeDefault;
        [ObservableProperty] string _details = "";
        [ObservableProperty] bool _hasSelection;
        [ObservableProperty] string _importMessage = "";

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasImportReport))]
        string _importReport = "";

        /// <summary>The user has no base model of their own yet: the page says what to add.</summary>
        [ObservableProperty] bool _isEmpty;

        [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddCommand))]
        bool _isImporting;

        [ObservableProperty] string _name = "";

        [ObservableProperty] AvatarRowViewModel _selected;
        bool _syncing;
        [ObservableProperty] string _vrChatNames = "";
        [ObservableProperty] string _wornAvatar = "";

        public SettingsAvatarsViewModel(AppHost host)
        {
            _host = host;
            _service = host.AvatarService;
            _service.Changed += Rebuild;
            Rebuild();
        }

        public ObservableCollection<AvatarRowViewModel> Items { get; } = new();

        public bool HasImportReport => !string.IsNullOrEmpty(ImportReport);

        /// <summary>The library folder, for the details.</summary>
        public string Folder => _service.Folder;

        /// <summary>Asks the view for the files of an avatar. Null or empty when cancelled.</summary>
        public Func<Task<IReadOnlyList<string>>> PickFiles { get; set; }

        /// <summary>Asks the view to confirm removing the named avatar.</summary>
        public Func<string, Task<bool>> ConfirmRemove { get; set; }

        /// <summary>Asks the view for a model inside a Unity project. Null when cancelled.</summary>
        public Func<Task<string>> PickUnityModel { get; set; }

        /// <summary>Asks the view where to save an FBX: the suggested file name and the folder to start in. Null when cancelled.</summary>
        public Func<string, string, Task<string>> PickSaveFbx { get; set; }

        // ------------------------------------------------------------------------------------------ commands

        bool CanAdd => !IsImporting;

        partial void OnSelectedChanged(AvatarRowViewModel value)
        {
            if (!_syncing) ShowSelection();
        }

        partial void OnNameChanged(string value)
        {
            if (_syncing || Selected == null) return;
            if (!_service.Rename(Selected.Entry, value))
                ShowSelection(); // nothing to rename to, or not saved: show the name it has
        }

        partial void OnVrChatNamesChanged(string value)
        {
            if (_syncing || Selected == null) return;
            if (_service.SetNames(Selected.Entry, value))
                _host.Status.Info(
                    $"VRChat avatars named like {AvatarService.NamesText(Selected.Entry)} now use {Selected.Entry.Label}");
            ShowSelection();
        }

        void Rebuild()
        {
            string selected = Selected?.Entry.Id;
            var def = _service.Default;
            _syncing = true;
            try
            {
                Items.Clear();
                foreach (var e in _service.Entries)
                    Items.Add(new AvatarRowViewModel
                    {
                        Entry = e, Name = e.Label, Summary = _service.Summary(e), IsDefault = e == def,
                        IsBuiltIn = e.BuiltIn, FbxMissing = !AvatarService.HasFbx(e)
                    });
                Selected = Items.FirstOrDefault(i =>
                    string.Equals(i.Entry.Id, selected, StringComparison.OrdinalIgnoreCase)) ?? Items.FirstOrDefault();
            }
            finally { _syncing = false; }

            IsEmpty = !_service.HasOwnEntries;
            IsImporting = _service.Importing;
            ImportMessage = _service.ImportMessage;
            ImportReport = _service.LastImportReport;
            ShowSelection();
            ShowWornAvatar();
        }

        void ShowSelection()
        {
            var entry = Selected?.Entry;
            _syncing = true;
            try
            {
                Name = entry?.Name ?? "";
                VrChatNames = AvatarService.NamesText(entry);
            }
            finally { _syncing = false; }

            Details = _service.Details(entry);
            HasSelection = entry != null;
            CanEdit = entry != null && !entry.BuiltIn;
            CanMakeDefault = entry != null && !Selected.IsDefault;
        }

        void ShowWornAvatar()
        {
            string worn = _service.WornAvatar;
            WornAvatar = !_host.CaptureOn ? "Live sources are off in this session, so VRChat reports no avatar."
                : worn.Length == 0 ? "VRChat has not reported an avatar yet."
                : "Wearing now: " + worn;
        }

        /// <summary>The page came on screen: the avatar worn may have changed since.</summary>
        public void SetActive(bool active)
        {
            if (active) ShowWornAvatar();
        }

        [RelayCommand(CanExecute = nameof(CanAdd))]
        async Task Add()
        {
            if (PickFiles == null) return;
            var files = await PickFiles();
            if (files == null || files.Count == 0) return;
            var entry = await _service.AddAsync(files);
            if (entry != null) Selected = Items.FirstOrDefault(i => i.Entry.Id == entry.Id) ?? Selected;
        }

        [RelayCommand]
        async Task Remove()
        {
            var entry = Selected?.Entry;
            if (entry == null || entry.BuiltIn) return;
            if (ConfirmRemove != null && !await ConfirmRemove(entry.Label)) return;
            try { _service.Remove(entry); }
            catch (Exception ex)
            {
                _host.Status.Notify(
                    $"{entry.Label} could not be removed: {ex.Message}. Its model may be open in another program.",
                    StatusKind.Error);
            }
        }

        [RelayCommand]
        void MakeDefault()
        {
            if (Selected != null) _service.SetDefault(Selected.Entry);
        }

        [RelayCommand]
        void OpenFolder()
        {
            _service.OpenFolder();
        }

        [RelayCommand]
        void Reload()
        {
            _service.Reload();
            int own = Items.Count(i => !i.IsBuiltIn);
            _host.Status.Info(own == 1
                ? "Read the avatar library again: 1 avatar"
                : $"Read the avatar library again: {own} avatars");
        }

        [RelayCommand]
        async Task LinkWorn()
        {
            if (_service.WornAvatarId.Length == 0)
            {
                _host.Status.Notify(
                    "VRChat has not reported an avatar yet. Turn OSC on in VRChat (Action Menu → Options → OSC → Enabled), then try again.",
                    StatusKind.Warning);
                return;
            }

            if (PickUnityModel == null) return;
            string model = await PickUnityModel();
            if (model == null) return;
            string problem = _service.LinkWornAvatar(model);
            if (problem != null) _host.Status.Notify(problem, StatusKind.Warning);
        }

        /// <summary>Keeps the face-tracking add-on's animator with the selected base model.</summary>
        [RelayCommand]
        async Task AddFaceAnimator()
        {
            var entry = Selected?.Entry;
            if (entry == null || entry.BuiltIn || PickFiles == null) return;
            var files = await PickFiles();
            if (files == null || files.Count == 0) return;
            await _service.AddFaceAnimatorAsync(entry, files);
            Details = _service.Details(entry);
        }

        [RelayCommand]
        async Task ExportFaceFbx()
        {
            var plan = await _service.PlanFaceFbxAsync(AvatarOnScreen());
            if (plan.Problem != null)
            {
                _host.Status.Notify(plan.Problem, StatusKind.Warning);
                return;
            }

            if (PickSaveFbx == null) return;
            string file = await PickSaveFbx(plan.SuggestedName + ".fbx", _host.Settings.LastExportDir);
            if (file != null) await _service.ExportFaceFbxAsync(plan, file);
        }

        /// <summary>The avatar of the open take, else the base model of the avatar worn now.</summary>
        AvatarProfile AvatarOnScreen()
        {
            return _host.Session.Clip?.Profile ?? _host.Session.Take?.Profile ??
                (_host.CaptureOn && _service.WornAvatarId.Length > 0
                    ? _host.Live.CurrentMatch()?.Entry?.Profile()
                    : null);
        }
    }
}
