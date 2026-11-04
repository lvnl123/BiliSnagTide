using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BiliLiveManager;

public partial class MainWindow
{
    private System.Windows.Controls.Border? _sidebarPane;
    private TextBlock? _sidebarTitle;
    private TextBlock? _sidebarSectionLabel;
    private FrameworkElement? _sidebarFooter;
    private TextBlock? _sidebarToggleGlyph;
    private bool _shellReady;
    private int _sidebarTransition;
    private bool _reduceMotion;

    private void SetupFluentMotion()
    {
        if (_shellReady) return;
        // The custom TabControl template owns the pane; after applying it we can locate
        // the named elements without changing any of the 9 existing TabItems or handlers.
        Pages.ApplyTemplate();
        _sidebarPane = Pages.Template.FindName("SidebarPane", Pages) as System.Windows.Controls.Border;
        _sidebarTitle = Pages.Template.FindName("SidebarTitle", Pages) as TextBlock;
        _sidebarSectionLabel = Pages.Template.FindName("SidebarSectionLabel", Pages) as TextBlock;
        _sidebarFooter = Pages.Template.FindName("SidebarFooter", Pages) as FrameworkElement;
        _sidebarToggleGlyph = Pages.Template.FindName("SidebarToggleGlyph", Pages) as TextBlock;
        _shellReady = _sidebarPane != null;
        _reduceMotion = _settings.ReduceMotion;
        MotionSettings.SetReducedMotion(this, _reduceMotion);
        if (!_shellReady) { AppLog.Write("导航侧栏模板加载失败，已保留完整菜单。"); return; }
        // Always open expanded at startup (☰). A previously saved collapsed state
        // should never unexpectedly hide the labels on the next launch.
        _settings.SidebarCollapsed = false;
        SetSidebarState(false, animate: false);
        foreach (var tab in Pages.Items.OfType<TabItem>())
        {
            if (tab.Header is StackPanel header)
            {
                var texts = header.Children.OfType<TextBlock>().ToArray();
                if (texts.Length > 1) tab.ToolTip = texts[^1].Text;
            }
        }
        // Animated reveals for the existing advanced panels, without new UI dependencies.
        AddHandler(Expander.ExpandedEvent, new RoutedEventHandler(OnExpanderExpanded));
        AnimateSelectedWorkspace();
        if (Environment.GetCommandLineArgs().Contains("--test-ui", StringComparer.OrdinalIgnoreCase))
            _ = RunUiMotionSelfTestAsync();
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_shellReady) SetupFluentMotion();
        _settings.SidebarCollapsed = !_settings.SidebarCollapsed;
        SetSidebarState(_settings.SidebarCollapsed, animate: true);
        try { SaveData(); } catch (Exception ex) { AppLog.Write("保存侧栏状态失败", ex); }
    }

    private void SetSidebarState(bool collapsed, bool animate)
    {
        if (_sidebarPane is null) return;
        double target = collapsed ? 68 : 226;
        int transition = ++_sidebarTransition;
        // Keep icons visible and interactive; change labels only after the width animation
        // on collapse, and before the animation on expansion.
        if (!collapsed) ApplyNavLabelVisibility(true);
        if (animate && !_reduceMotion)
        {
            double start = _sidebarPane.ActualWidth > 1 ? _sidebarPane.ActualWidth : _sidebarPane.Width;
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var widthAnimation = new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(185))
            {
                EasingFunction = ease
            };
            widthAnimation.Completed += (_, _) =>
            {
                if (_sidebarPane == null || transition != _sidebarTransition) return;
                _sidebarPane.Width = target;
                _sidebarPane.BeginAnimation(FrameworkElement.WidthProperty, null);
                if (collapsed) ApplyNavLabelVisibility(false);
            };
            _sidebarPane.BeginAnimation(FrameworkElement.WidthProperty, widthAnimation, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            _sidebarPane.BeginAnimation(FrameworkElement.WidthProperty, null);
            _sidebarPane.Width = target;
            ApplyNavLabelVisibility(!collapsed);
        }
        if (_sidebarToggleGlyph != null)
            _sidebarToggleGlyph.Text = collapsed ? ">" : "☰";
        // The button advertises its *next* action, not the current pane state.
        if (Pages.Template.FindName("SidebarToggleButton", Pages) is System.Windows.Controls.Button toggle)
        {
            string nextAction = collapsed ? "展开导航菜单" : "收起导航菜单";
            toggle.ToolTip = nextAction;
            System.Windows.Automation.AutomationProperties.SetName(toggle, nextAction);
        }
    }

    private void ApplyNavLabelVisibility(bool visible)
    {
        var state = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_sidebarTitle != null) _sidebarTitle.Visibility = state;
        if (_sidebarSectionLabel != null) _sidebarSectionLabel.Visibility = state;
        if (_sidebarFooter != null) _sidebarFooter.Visibility = state;
        foreach (var tab in Pages.Items.OfType<TabItem>())
        {
            tab.Margin = visible ? new Thickness(8, 2, 8, 2) : new Thickness(5, 2, 5, 2);
            if (tab.Header is not StackPanel header) continue;
            var texts = header.Children.OfType<TextBlock>().ToArray();
            if (texts.Length > 1) texts[^1].Visibility = state;
        }
    }

    private void AnimateSelectedWorkspace()
    {
        if (!_shellReady || _reduceMotion) return;
        // Deferred until layout has updated the selected ContentPresenter.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_shellReady || _reduceMotion) return;
            if (Pages.Template.FindName("PART_SelectedContentHost", Pages) is not ContentPresenter content) return;
            content.BeginAnimation(UIElement.OpacityProperty, null);
            content.Opacity = 1; // Stable base value; never reset it during a running animation.
            var translate = content.RenderTransform as TranslateTransform;
            if (translate == null) { translate = new TranslateTransform(0, 0); content.RenderTransform = translate; }
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 0;
            content.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.84, 1, TimeSpan.FromMilliseconds(155)) { FillBehavior = FillBehavior.Stop });
            translate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(185))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }));
    }

    private void AnimateStatusChange()
    {
        if (!_shellReady || _reduceMotion) return;
        StatusText.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(200)));
    }

    private void OnExpanderExpanded(object sender, RoutedEventArgs e)
    {
        if (_reduceMotion || e.OriginalSource is not Expander expander || expander.Content is not FrameworkElement body)
            return;
        body.BeginAnimation(UIElement.OpacityProperty, null);
        body.Opacity = 1;
        body.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
    }

    /// <summary> opt-in self-test. Checks layout with real WPF measurement on the user's PC. </summary>
    private async System.Threading.Tasks.Task RunUiMotionSelfTestAsync()
    {
        if (!_shellReady || _sidebarPane is null) return;
        bool initial = _settings.SidebarCollapsed;
        var content = Pages.Template.FindName("PART_SelectedContentHost", Pages) as ContentPresenter;
        if (content == null) return;
        try
        {
            SetSidebarState(false, animate: false);
            await System.Threading.Tasks.Task.Delay(170);
            double openWidth = content.ActualWidth;
            SetSidebarState(true, animate: false);
            await System.Threading.Tasks.Task.Delay(170);
            double closedWidth = content.ActualWidth;
            bool iconVisible = Pages.Items.OfType<TabItem>().All(t => t.Visibility == Visibility.Visible);
            bool labelsHidden = Pages.Items.OfType<TabItem>()
                .Where(t => t.Header is StackPanel).All(t => ((StackPanel)t.Header).Children.OfType<TextBlock>()
                    .Skip(1).All(label => label.Visibility == Visibility.Collapsed));
            bool passed = closedWidth > openWidth + 145 && iconVisible && labelsHidden;
            string result = passed
                ? $"PASS: expanded={openWidth:0}px compact={closedWidth:0}px; icons visible; labels hidden"
                : $"FAIL: expanded={openWidth:0}px compact={closedWidth:0}px; icons={iconVisible} labels={labelsHidden}";
            AppLog.Write("UI MOTION SELF TEST: " + result);
            System.Windows.MessageBox.Show(this, result, "BiliSnagTide · 导航布局自测",
                System.Windows.MessageBoxButton.OK, passed ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppLog.Write("UI motion self-test failed", ex);
        }
        finally { SetSidebarState(initial, animate: false); }
    }

    private void MotionSettings_Click(object sender, RoutedEventArgs e)
    {
        _reduceMotion = ReduceMotionToggle.IsChecked == true;
        _settings.ReduceMotion = _reduceMotion;
        MotionSettings.SetReducedMotion(this, _reduceMotion);
        if (_reduceMotion)
        {
            // Cancel ongoing navigation animation to avoid leaving a half-collapsed shell.
            SetSidebarState(_settings.SidebarCollapsed, animate: false);
        }
    }
}
