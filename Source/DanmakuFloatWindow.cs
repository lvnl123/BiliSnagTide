using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using Binding = System.Windows.Data.Binding;

namespace BiliLiveManager;

/// <summary>
/// A small, always-following chat surface intended to sit beside PotPlayer.
/// Items are retained only in memory; no visible OS scrollbars or chat export.
/// </summary>
public sealed class DanmakuFloatWindow : Window
{
    private readonly ObservableCollection<DanmakuLine> _lines = new();
    private readonly ItemsControl _messages;
    private readonly ScrollViewer _viewport;
    private bool _followScheduled;
    private int _maxLines = 12;

    public bool DisableEntranceMotion { get; set; }

    public DanmakuFloatWindow()
    {
        Title = "BiliSnagTide · 弹幕悬浮窗";
        Width = 410; Height = 330; MinWidth = 260; MinHeight = 170;
        Topmost = true; ShowInTaskbar = false;
        AllowsTransparency = true; WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = Brushes.Transparent;
        Opacity = 0.87;

        var card = new Border
        {
            Background = System.Windows.Application.Current.Resources["FloatBg"] as System.Windows.Media.Brush ?? Brushes.Black,
            CornerRadius = new CornerRadius(14),
            BorderBrush = System.Windows.Application.Current.Resources["FloatBorder"] as System.Windows.Media.Brush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14)
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true, Cursor = Cursors.SizeAll };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                try { DragMove(); } catch (InvalidOperationException) { }
        };
        var dismiss = new Button
        {
            Content = "×", Width = 30, Height = 25, FontSize = 16,
            Foreground = System.Windows.Application.Current.Resources["FloatText"] as System.Windows.Media.Brush ?? Brushes.White, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };
        dismiss.Click += (_, _) => Close();
        DockPanel.SetDock(dismiss, Dock.Right); header.Children.Add(dismiss);
        var floatTitle = new TextBlock
        {
            Text = "●  实时弹幕  /  BILISNAGTIDE", FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        floatTitle.SetResourceReference(TextBlock.ForegroundProperty, "FloatTitle");
        header.Children.Add(floatTitle);
        Grid.SetRow(header, 0); grid.Children.Add(header);

        _messages = new ItemsControl { ItemsSource = _lines, Focusable = false };
        _messages.ItemTemplate = BuildTemplate();
        _viewport = new ScrollViewer
        {
            Content = _messages,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Focusable = false,
            IsHitTestVisible = false
        };
        _viewport.SizeChanged += (_, _) => RequestFollow();
        Grid.SetRow(_viewport, 1); grid.Children.Add(_viewport);
        card.SetResourceReference(Border.BackgroundProperty, "FloatBg");
        card.SetResourceReference(Border.BorderBrushProperty, "FloatBorder");
        dismiss.SetResourceReference(Button.ForegroundProperty, "FloatText");
        card.Child = grid;
        Content = card;
        Loaded += (_, _) =>
        {
            RequestFollow();
            if (!DisableEntranceMotion)
            {
                double current = Opacity;
                BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.05, current, TimeSpan.FromMilliseconds(190))
                { FillBehavior = FillBehavior.Stop });
            }
        };
    }

    private static DataTemplate BuildTemplate()
    {
        var root = new FrameworkElementFactory(typeof(StackPanel));
        root.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        root.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 4, 4));

        var marker = new FrameworkElementFactory(typeof(TextBlock));
        marker.SetValue(TextBlock.TextProperty, "★");
        marker.SetResourceReference(TextBlock.ForegroundProperty, "FloatStar");
        marker.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 5, 0));
        marker.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(DanmakuLine.IsHighlighted))
        { Converter = new System.Windows.Controls.BooleanToVisibilityConverter() });

        var user = new FrameworkElementFactory(typeof(TextBlock));
        user.SetBinding(TextBlock.TextProperty, new Binding(nameof(DanmakuLine.User)));
        user.SetResourceReference(TextBlock.ForegroundProperty, "FloatUser");
        user.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        user.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        user.SetValue(FrameworkElement.MaxWidthProperty, 95.0);
        user.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

        var msg = new FrameworkElementFactory(typeof(TextBlock));
        msg.SetBinding(TextBlock.TextProperty, new Binding(nameof(DanmakuLine.Text)));
        msg.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(DanmakuLine.IsHighlighted))
        { Converter = new FocusTextColorConverter() });
        msg.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        msg.SetValue(FrameworkElement.MaxWidthProperty, 240.0);

        root.AppendChild(marker); root.AppendChild(user); root.AppendChild(msg);
        return new DataTemplate { VisualTree = root };
    }

    // Coalesce multiple additions into one follow operation after the WPF layout pass.
    private void RequestFollow()
    {
        if (_followScheduled || !IsLoaded) return;
        _followScheduled = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _followScheduled = false;
            if (IsLoaded && _viewport.IsVisible)
                _viewport.ScrollToEnd();
        }), DispatcherPriority.Loaded);
    }

    public void Add(DanmakuLine line)
    {
        _lines.Add(line);
        while (_lines.Count > _maxLines) _lines.RemoveAt(0);
        RequestFollow();
    }
    public void ClearMessages() { _lines.Clear(); RequestFollow(); }
    public (bool AtBottom, bool HiddenScrollbar, double Offset, double MaxOffset) GetScrollCheck() =>
        (_viewport.ScrollableHeight - _viewport.VerticalOffset <= 3,
         _viewport.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden,
         _viewport.VerticalOffset, _viewport.ScrollableHeight);
    public void SetFontSize(double size)
    {
        _messages.FontSize = Math.Clamp(size, 10, 22);
        RequestFollow();
    }
    public void SetMaxLines(int value)
    {
        _maxLines = Math.Clamp(value, 3, 25);
        while (_lines.Count > _maxLines) _lines.RemoveAt(0);
        RequestFollow();
    }

    private sealed class FocusTextColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            System.Windows.Application.Current.Resources[value is bool highlighted && highlighted ? "FloatHighlight" : "FloatText"]
            as System.Windows.Media.Brush ?? Brushes.White;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}
