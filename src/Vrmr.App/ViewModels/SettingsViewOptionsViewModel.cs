using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VRMR.App.Composition;
using VRMR.App.Services;

namespace VRMR.App.ViewModels
{
    /// <summary>One mesh of the avatar on screen: ticked while it is shown.</summary>
    public partial class MeshRowViewModel : ObservableObject
    {
        readonly Action<MeshRowViewModel> _changed;

        public MeshRowViewModel(string name, int triangles, bool shown, Action<MeshRowViewModel> changed)
        {
            Name = name; _shown = shown; _changed = changed;
            Tip = $"{name}: {triangles:N0} triangles";
        }

        public string Name { get; }
        public string Tip { get; }
        [ObservableProperty] bool _shown;

        partial void OnShownChanged(bool value) => _changed(this);
    }

    /// <summary>Settings → View: the theme, and which meshes of the avatar on screen are hidden (an outfit piece, say).</summary>
    public partial class SettingsViewOptionsViewModel : ObservableObject
    {
        /// <summary>The theme names as <c>ui-state.json</c> stores them, in the order of the switch.</summary>
        public static readonly string[] Themes = { "dark", "light", "system" };

        readonly AppHost _host;
        AvatarProfile _listed;
        AvatarMeshList _list = new AvatarMeshList();
        bool _active, _syncing;
        int _load;

        public SettingsViewOptionsViewModel(AppHost host)
        {
            _host = host;
            _themeIndex = Math.Max(0, Array.IndexOf(Themes, host.UiState.GetString("theme", "dark")));
            host.Session.ClipChanged += () => { if (_active) LoadMeshes(); };
        }

        [ObservableProperty] int _themeIndex;
        [ObservableProperty] string _meshInfo = "";
        [ObservableProperty] bool _hasMeshes;
        [ObservableProperty] bool _anyHidden;

        public ObservableCollection<MeshRowViewModel> Meshes { get; } = new ObservableCollection<MeshRowViewModel>();

        /// <summary>Applies a theme ("dark", "light", "system") to the running app; set by the view.</summary>
        public Action<string> ApplyTheme { get; set; }

        partial void OnThemeIndexChanged(int value)
        {
            if (value < 0 || value >= Themes.Length) return;
            _host.UiState.Set("theme", Themes[value]);
            ApplyTheme?.Invoke(Themes[value]);
        }

        /// <summary>The mesh list is read only while this category is on screen: reading a model the view has not loaded yet takes a moment.</summary>
        public void SetActive(bool active)
        {
            _active = active;
            if (active) LoadMeshes();
        }

        async void LoadMeshes()
        {
            var profile = _host.Session.Clip?.Profile;
            if (profile != null && profile == _listed) return;
            _listed = profile;
            int load = ++_load;
            if (profile != null && Meshes.Count == 0) MeshInfo = "Reading the avatar's meshes…";
            var list = await _host.AvatarService.LoadMeshesAsync(profile);
            if (load != _load) return; // another take was opened meanwhile
            var hidden = _host.AvatarService.HiddenMeshes;
            _syncing = true;
            try
            {
                Meshes.Clear();
                foreach (var part in list.Parts) Meshes.Add(new MeshRowViewModel(part.Name, part.Triangles, !hidden.Contains(part.Name), OnMeshToggled));
            }
            finally { _syncing = false; }
            HasMeshes = Meshes.Count > 0;
            _list = list;
            ShowInfo();
        }

        void OnMeshToggled(MeshRowViewModel row)
        {
            if (_syncing) return;
            _host.AvatarService.SetMeshHidden(row.Name, !row.Shown);
            ShowInfo();
        }

        void ShowInfo()
        {
            int shown = Meshes.Count(m => m.Shown);
            AnyHidden = shown < Meshes.Count;
            MeshInfo = Meshes.Count == 0 ? _list.Note
                : $"{_list.File}: {shown} of {Meshes.Count} meshes shown, {_list.Triangles:N0} triangles in all";
        }

        [RelayCommand]
        void ShowAllMeshes()
        {
            foreach (var m in Meshes) m.Shown = true;
        }
    }
}
