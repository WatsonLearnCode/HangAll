using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HangAll.Core;
using HangAll.Interop;

namespace HangAll.UI;

/// <summary>Shared author links and the controls that open them.</summary>
internal static class AuthorLinks
{
    internal const string Homepage = "https://github.com/WatsonLearnCode/HangAll";
    internal const string GitHubGlyph = "M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27s1.36.09 2 .27c1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8z";

    internal static System.Windows.Shapes.Path IconPath(string data, bool stroke, double size = 14)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            StrokeThickness = stroke ? 1.4 : 0,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        if (stroke)
            icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "MenuFg");
        else
            icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "MenuFg");
        return icon;
    }

    internal static void OpenHomepage(Window owner)
    {
        if (Shell.OpenUrl(Homepage)) return;
        MessageBox.Show(
            owner,
            Strings.HomepageOpenFailed,
            Strings.AppName,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

}
