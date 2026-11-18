using CommunityToolkit.Mvvm.ComponentModel;
using VRMR.App.Services;

namespace VRMR.App.ViewModels
{
    /// <summary>
    ///     One thing the app looked at, as Settings and the first-run guide show it: a dot (green found, amber needs a
    ///     look, grey off or optional), what was found, and what to do when something is missing.
    /// </summary>
    public partial class StatusRowViewModel : ObservableObject
    {
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAction))]
        string _action = "";

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsOk), nameof(NeedsAttention))]
        SourceLevel _level;

        [ObservableProperty] string _summary = "";

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasTitle))]
        string _title = "";

        /// <summary>Which line of the report this row shows ("" for rows filled by hand).</summary>
        public string Id { get; init; } = "";

        public bool HasTitle => !string.IsNullOrEmpty(Title);
        public bool HasAction => !string.IsNullOrEmpty(Action);
        public bool IsOk => Level == SourceLevel.Ok;
        public bool NeedsAttention => Level == SourceLevel.Waiting || Level == SourceLevel.Warning;

        /// <summary>Fills the row by hand.</summary>
        public void Set(SourceLevel level, string summary, string action = "")
        {
            Level = level;
            Summary = summary ?? "";
            Action = action ?? "";
        }

        /// <summary>Shows a live source's state (its facts on one line each, then the fix).</summary>
        public void Set(SourceState state)
        {
            Set(state.Level, state.Detail, state.Fix);
        }

        /// <summary>Shows one line of the environment report; null while the first look is still running.</summary>
        public void Set(ProbeItem item)
        {
            if (item == null) Set(SourceLevel.Off, "Checking…");
            else Set(LevelOf(item), item.Summary, item.Action);
        }

        /// <summary>
        ///     Green when found; amber when it needs a look or something the app needs is missing; grey when an optional
        ///     thing is missing or unknown.
        /// </summary>
        public static SourceLevel LevelOf(ProbeItem item)
        {
            switch (item.State)
            {
                case ProbeState.Ok: return SourceLevel.Ok;
                case ProbeState.Attention: return SourceLevel.Warning;
                case ProbeState.Missing: return item.Optional ? SourceLevel.Off : SourceLevel.Warning;
                default: return SourceLevel.Off;
            }
        }
    }
}
