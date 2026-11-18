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
    /// <summary>One row of the takes list.</summary>
    public partial class TakeItemViewModel : ObservableObject
    {
        [ObservableProperty] bool _isOpen;

        /// <summary>The name shown (the open take shows its unsaved name with a dot).</summary>
        [ObservableProperty] string _name;

        public TakeItemViewModel(TakeInfo info)
        {
            Info = info;
            _name = info.Name;
        }

        public TakeInfo Info { get; private set; }
        public string Path => Info.Path;

        public string Length => TimeText.Format(Info.Duration, false);
        public string Date => Info.Modified.ToString("yyyy-MM-dd HH:mm");

        /// <summary>Second line: avatar · world.</summary>
        public string Subtitle => Info.Unreadable
            ? "Cannot be read as a take"
            : Info.Avatar + (string.IsNullOrEmpty(Info.World)
                ? ""
                : (Info.Avatar.Length > 0 ? "  ·  " : "") + Info.World);

        public bool HasFace => Info.HasFace;
        public bool HasFingers => Info.HasFingers;
        public bool HasVrcIk => Info.HasVrcIk;

        /// <summary>No SteamVR devices: there is nothing for the IK solve.</summary>
        public bool NoDevices => !Info.HasDevices && !Info.Unreadable;

        public bool Unreadable => Info.Unreadable;

        /// <summary>The whole story for the tooltip.</summary>
        public string Tip
        {
            get
            {
                var data = new List<string>();
                if (Info.HasDevices) data.Add("SteamVR devices");
                if (Info.HasVrcIk) data.Add("VRChat's own IK");
                if (Info.HasFace) data.Add("face tracking");
                if (Info.HasFingers) data.Add("finger tracking");
                return $"{Info.Name}\n{Date} · {Length}" + (Info.Avatar.Length > 0 ? "\nAvatar: " + Info.Avatar : "") +
                       (Info.World.Length > 0 ? "\nWorld: " + Info.World : "")
                       + (data.Count > 0 ? "\nContains: " + string.Join(", ", data) : "");
            }
        }

        /// <summary>The take's name: what a screen reader says for its row in the list.</summary>
        public override string ToString()
        {
            return Name ?? "";
        }

        public void Update(TakeInfo info)
        {
            Info = info;
            OnPropertyChanged(string.Empty);
        }
    }

    /// <summary>The library panel: the takes, newest first, with search, open, delete and reveal.</summary>
    public partial class LibraryViewModel : ObservableObject
    {
        readonly AppHost _host;
        readonly TakeLibrary _library;
        readonly TakeSession _session;
        [ObservableProperty] string _countText = "";
        [ObservableProperty] bool _isEmpty;
        [ObservableProperty] bool _isLoading = true;
        [ObservableProperty] bool _noMatches;

        [ObservableProperty] string _search = "";
        [ObservableProperty] TakeItemViewModel _selected;
        bool _syncingSelection;

        public LibraryViewModel(AppHost host)
        {
            _host = host;
            _library = host.Library;
            _session = host.Session;
            _library.Changed += Rebuild;
            _session.TakeChanged += SyncOpen;
            _session.DirtyChanged += SyncOpen;
            _library.Refresh();
        }

        public ObservableCollection<TakeItemViewModel> Items { get; } = new();

        /// <summary>Asks the view to confirm a deletion (the one dialog: it cannot be undone from inside the app).</summary>
        public Func<string, Task<bool>> ConfirmDelete { get; set; }

        /// <summary>Asks the view for a take file to open.</summary>
        public Func<Task<string>> PickFile { get; set; }

        /// <summary>The recordings folder, for the empty state.</summary>
        public string Folder => _library.Folder;

        partial void OnSearchChanged(string value)
        {
            Rebuild();
        }

        partial void OnSelectedChanged(TakeItemViewModel value)
        {
            if (_syncingSelection || value == null || value.Unreadable) return;
            if (_host.Recording.IsRecording) return;
            _ = _session.OpenAsync(value.Path);
        }

        void Rebuild()
        {
            var all = _library.Items;
            var shown = TakeLibrary.Filter(all, Search).ToList();
            var byPath = Items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
            _syncingSelection = true;
            try
            {
                string selectedPath = Selected?.Path ?? _session.Take?.FilePath;
                Items.Clear();
                foreach (var info in shown)
                {
                    if (byPath.TryGetValue(info.Path, out var vm)) vm.Update(info);
                    else vm = new TakeItemViewModel(info);
                    Items.Add(vm);
                }

                Selected = selectedPath == null
                    ? null
                    : Items.FirstOrDefault(i =>
                        string.Equals(i.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            }
            finally { _syncingSelection = false; }

            IsLoading = _library.Loading;
            IsEmpty = !IsLoading && all.Count == 0;
            NoMatches = !IsLoading && all.Count > 0 && shown.Count == 0;
            CountText = all.Count == 0 ? "" :
                shown.Count == all.Count ? all.Count == 1 ? "1 take" : all.Count + " takes" :
                $"{shown.Count} of {all.Count}";
            SyncOpen();
        }

        void SyncOpen()
        {
            var take = _session.Take;
            foreach (var i in Items)
            {
                bool open = take?.FilePath != null &&
                            string.Equals(i.Path, take.FilePath, StringComparison.OrdinalIgnoreCase);
                i.IsOpen = open;
                i.Name = open ? take.Name + (_session.Dirty ? " •" : "") : i.Info.Name;
            }

            if (take?.FilePath != null && (Selected == null ||
                                           !string.Equals(Selected.Path, take.FilePath,
                                               StringComparison.OrdinalIgnoreCase)))
            {
                _syncingSelection = true;
                try
                {
                    Selected = Items.FirstOrDefault(i =>
                        string.Equals(i.Path, take.FilePath, StringComparison.OrdinalIgnoreCase));
                }
                finally { _syncingSelection = false; }
            }
        }

        [RelayCommand]
        void ClearSearch()
        {
            Search = "";
        }

        [RelayCommand]
        void OpenFolder()
        {
            _library.OpenFolder();
        }

        [RelayCommand]
        void Refresh()
        {
            _library.Refresh();
        }

        [RelayCommand]
        async Task OpenFile()
        {
            if (PickFile == null) return;
            string path = await PickFile();
            if (path != null) await _session.OpenAsync(path);
        }

        [RelayCommand]
        void Reveal(TakeItemViewModel item)
        {
            item ??= Selected;
            if (item != null) FileActions.Reveal(item.Path);
        }

        [RelayCommand]
        async Task Delete(TakeItemViewModel item)
        {
            item ??= Selected;
            if (item == null) return;
            if (ConfirmDelete != null && !await ConfirmDelete(item.Info.Name)) return;
            try
            {
                bool open = _session.Take?.FilePath != null && string.Equals(_session.Take.FilePath, item.Path,
                    StringComparison.OrdinalIgnoreCase);
                if (open) _session.Close();
                _library.Delete(item.Path);
                _host.Status.Info($"Moved {item.Info.Name} to the Recycle Bin");
            }
            catch (Exception ex)
            {
                _host.Status.Show(new Toast
                {
                    Text = $"Could not delete {item.Info.Name}: {ex.Message}. It may be open in another program.",
                    Kind = StatusKind.Error, Duration = TimeSpan.FromSeconds(8)
                });
            }
        }

        /// <summary>Selects the next or previous take in the list (keyboard).</summary>
        public void Move(int delta)
        {
            if (Items.Count == 0) return;
            int i = Selected == null ? -1 : Items.IndexOf(Selected);
            i = Math.Max(0, Math.Min(Items.Count - 1, i + delta));
            Selected = Items[i];
        }
    }
}
