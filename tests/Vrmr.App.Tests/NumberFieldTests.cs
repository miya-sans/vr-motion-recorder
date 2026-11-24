using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Media;
using VRMR.App.Controls;
using Xunit;

namespace VRMR.App.Tests
{
    using Key = Avalonia.Input.Key;

    [Collection("app")]
    public class NumberFieldTests
    {
        [Theory]
        [InlineData(Key.Enter, 0.75)]
        [InlineData(Key.Escape, 0.25)]
        [InlineData(Key.None, 0.75)]
        public async Task FinishingAnEditReleasesFocus(Key key, double expected)
        {
            await HeadlessUi.RunAsync(() =>
            {
                var field = new NumberField
                {
                    Width = 110,
                    Value = 0.25,
                    Decimals = 2,
                    Unit = "s",
                    VerticalAlignment = VerticalAlignment.Top
                };
                var background = new Border { Background = Brushes.Transparent, Child = field };
                var window = new Window { Content = background, Width = 300, Height = 100 };
                window.Show();
                try
                {
                    var inside = field.TranslatePoint(new Point(2, field.Bounds.Height / 2), window).Value;
                    window.MouseDown(inside, MouseButton.Left);
                    window.MouseUp(inside, MouseButton.Left);
                    window.KeyTextInput("0.75");
                    Assert.Equal("0.75", field.Text);

                    if (key == Key.None)
                    {
                        var outside = new Point(10, 80);
                        window.MouseDown(outside, MouseButton.Left);
                        window.MouseUp(outside, MouseButton.Left);
                    }
                    else
                    {
                        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
                    }

                    Assert.False(field.IsFocused);
                    Assert.Equal(expected, field.Value);
                    string display = expected == 0.75 ? "0.75 s" : "0.25 s";
                    Assert.Equal(display, field.Text);
                    window.KeyTextInput("9");
                    Assert.Equal(display, field.Text);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        [Theory]
        [InlineData(2)]
        [InlineData(90)]
        public async Task DragScrubsAndClickEntersTextEditing(double x)
        {
            await HeadlessUi.RunAsync(() =>
            {
                var field = new NumberField
                {
                    Width = 110,
                    Value = 0.25,
                    Minimum = 0,
                    Maximum = 1,
                    Step = 0.05,
                    Decimals = 2,
                    Unit = "s"
                };
                var window = new Window { Content = field, Width = 300, Height = 100 };
                window.Show();
                try
                {
                    var start = field.TranslatePoint(new Point(x, field.Bounds.Height / 2), window).Value;
                    window.MouseDown(start, MouseButton.Left);
                    window.MouseMove(start + new Point(12, 0));
                    window.MouseUp(start + new Point(12, 0), MouseButton.Left);
                    Assert.Equal(0.45, field.Value);
                    Assert.Equal("0.45 s", field.Text);

                    window.MouseDown(start, MouseButton.Left);
                    window.MouseUp(start, MouseButton.Left);
                    Assert.True(field.IsFocused);
                    Assert.Equal("0.45", field.SelectedText);
                }
                finally
                {
                    window.Close();
                }
            });
        }
    }
}
