using System.Windows;

namespace BiliLiveManager;

/// <summary>Inheritable window preference used by all WPF control templates.</summary>
public static class MotionSettings
{
    public static readonly DependencyProperty ReducedMotionProperty =
        DependencyProperty.RegisterAttached(
            "ReducedMotion", typeof(bool), typeof(MotionSettings),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetReducedMotion(DependencyObject obj) => (bool)obj.GetValue(ReducedMotionProperty);
    public static void SetReducedMotion(DependencyObject obj, bool value) => obj.SetValue(ReducedMotionProperty, value);
}
