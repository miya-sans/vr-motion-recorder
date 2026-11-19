using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VRMR.App.ViewModels;

namespace VRMR.App.Views.Settings
{
    /// <summary>Selects projects from one manager without modifying its data.</summary>
    public partial class UnityProjectPicker : Window
    {
        public UnityProjectPicker()
        {
            InitializeComponent();
            Opened += async (o, e) =>
            {
                if (DataContext is UnityProjectPickerViewModel vm) await vm.LoadAsync();
            };
        }

        void SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is UnityProjectPickerViewModel vm)
                vm.Select(ProjectList.SelectedItems.OfType<UnityProjectRowViewModel>());
        }

        void Cancel(object sender, RoutedEventArgs e)
        {
            Close(Array.Empty<string>());
        }

        void AddSelected(object sender, RoutedEventArgs e)
        {
            if (DataContext is UnityProjectPickerViewModel { CanAdd: true } vm) Close(vm.SelectedPaths());
        }
    }
}
