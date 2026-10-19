using System.Windows;

namespace SonicCube.Panel.Controls;

/// <summary>本地预览的状态不经过远端回包,可立即同步到控件。</summary>
internal static class StateSynchronization
{
    public static readonly DependencyProperty IsLocalProperty = DependencyProperty.RegisterAttached(
        "IsLocal", typeof(bool), typeof(StateSynchronization),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool IsLocal(DependencyObject owner) => (bool)owner.GetValue(IsLocalProperty);
}
