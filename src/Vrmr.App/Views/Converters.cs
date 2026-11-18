using Avalonia.Data.Converters;
using VRMR.App.Services;

namespace VRMR.App.Views
{
    /// <summary>Small value converters the views share.</summary>
    public static class Converters
    {
        public static readonly IValueConverter IsError =
            new FuncValueConverter<StatusKind, bool>(k => k == StatusKind.Error);

        public static readonly IValueConverter IsWarning =
            new FuncValueConverter<StatusKind, bool>(k => k == StatusKind.Warning);

        public static readonly IValueConverter IsSuccess =
            new FuncValueConverter<StatusKind, bool>(k => k == StatusKind.Success);

        /// <summary>True for a non-empty string.</summary>
        public static readonly IValueConverter HasText =
            new FuncValueConverter<string, bool>(s => !string.IsNullOrEmpty(s));

        public static readonly IValueConverter Not = new FuncValueConverter<bool, bool>(b => !b);
    }
}
