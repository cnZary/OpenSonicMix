using System.Windows;
using System.Windows.Media;

namespace SonicCube.Panel.Controls;

internal static class DrawingThemeResources
{
    // Cached drawings require frozen brushes. Resolve dynamic theme colors before
    // freezing so a resource expression cannot make the drawing fail to freeze.
    public static Brush GetDrawingBrush(this FrameworkElement owner, string key)
    {
        var brush = ((Brush)owner.FindResource(key)).CloneCurrentValue();
        brush.Freeze();
        return brush;
    }
}
