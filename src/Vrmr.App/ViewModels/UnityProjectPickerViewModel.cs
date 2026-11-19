using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VRMR.App.ViewModels
{
    /// <summary>An explicitly opened, read-only manager project picker.</summary>
    public partial class UnityProjectPickerViewModel : ObservableObject
    {
        readonly HashSet<string> _linked;
        readonly IMachine _machine;
        readonly UnityProjectSource _source;
        [ObservableProperty] string _folder;
        [ObservableProperty] bool _loading;
        [ObservableProperty] string _message = "";

        public UnityProjectPickerViewModel(IMachine machine, UnityProjectSource source, IEnumerable<string> linked)
        {
            _machine = machine;
            _source = source;
            _linked = new HashSet<string>(linked.Select(UnityProjects.NormalizePath), StringComparer.OrdinalIgnoreCase);
            _folder = UnityProjectSources.DefaultFolder(machine, source);
        }

        public string Title => "Add projects from " + UnityProjectSources.Name(_source);
        public ObservableCollection<UnityProjectRowViewModel> Projects { get; } = new();
        public ObservableCollection<UnityProjectRowViewModel> SelectedProjects { get; } = new();
        public bool CanAdd => !Loading && SelectedProjects.Any(p => p.CanAdd);
        public Func<string, Task<string>> PickFolder { get; set; }

        /// <summary>Loads the selected manager only; no settings are changed.</summary>
        public async Task LoadAsync()
        {
            if (Loading) return;
            Loading = true;
            OnPropertyChanged(nameof(CanAdd));
            Projects.Clear();
            SelectedProjects.Clear();
            Message = "Loading projects…";
            try
            {
                var result = await Task.Run(() => UnityProjectSources.Discover(_machine, _source, Folder));
                foreach (var project in result.Projects)
                    Projects.Add(new UnityProjectRowViewModel(project, _linked.Contains(project.Path)));
                Message = result.Errors.Count > 0
                    ? string.Join(Environment.NewLine, result.Errors)
                    : Projects.Count == 0
                        ? "No projects in this list."
                        : "Select projects to add.";
            }
            catch (Exception ex) { Message = ex.Message; }
            finally
            {
                Loading = false;
                OnPropertyChanged(nameof(CanAdd));
            }
        }

        /// <summary>Updates the selection from the view; unavailable entries cannot be accepted.</summary>
        public void Select(IEnumerable<UnityProjectRowViewModel> projects)
        {
            SelectedProjects.Clear();
            foreach (var project in projects.Where(p => p.CanAdd)) SelectedProjects.Add(project);
            OnPropertyChanged(nameof(CanAdd));
        }

        public string[] SelectedPaths()
        {
            return Loading
                ? Array.Empty<string>()
                : SelectedProjects.Where(p => p.CanAdd).Select(p => p.Path).ToArray();
        }

        [RelayCommand]
        async Task LocateDataFolder()
        {
            if (PickFolder == null || Loading) return;
            string folder = await PickFolder(Folder);
            if (folder == null) return;
            Folder = folder;
            await LoadAsync();
        }
    }
}
