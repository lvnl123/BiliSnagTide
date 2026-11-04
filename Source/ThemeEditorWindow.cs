using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace BiliLiveManager;

/// <summary>
/// Local theme editor. All edits stay in working copies until Save; Close/Cancel rolls back
/// the resource preview without changing saved settings. Supports all ThemeCatalog color roles.
/// </summary>
public sealed class ThemeEditorWindow : Window
{
    private readonly SettingsModel _settings;
    private readonly bool _originalDark;
    private readonly Dictionary<bool, string> _preset;
    private readonly Dictionary<bool, Dictionary<string, string>> _overrides;
    private bool _dark;
    private bool _rebuilding;
    private bool _saved;
    private ComboBox _modeSelect = null!;
    private ComboBox _presetSelect = null!;
    private ComboBox _groupSelect = null!;
    private TextBox _search = null!;
    private StackPanel _roleList = null!;
    private TextBlock _contrast = null!;
    private TextBlock _currentSummary = null!;
    private Border _previewCard = null!;

    public bool SavedDarkMode { get; private set; }

    public ThemeEditorWindow(SettingsModel settings, bool activeDark)
    {
        _settings = settings;
        _originalDark = activeDark;
        _dark = activeDark;
        _preset = new Dictionary<bool, string>
        {
            [false] = ThemeCatalog.SafePreset(settings.LightThemePreset),
            [true] = ThemeCatalog.SafePreset(settings.DarkThemePreset)
        };
        _overrides = new Dictionary<bool, Dictionary<string, string>>
        {
            [false] = CopyValid(settings.LightThemeColors),
            [true] = CopyValid(settings.DarkThemeColors)
        };

        Title = "BiliSnagTide · 主题与配色工作台";
        Width = 1160; Height = 800; MinWidth = 910; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resource(this, Window.BackgroundProperty, "DialogBg");
        Resource(this, Window.ForegroundProperty, "MainText");
        BuildUI();
        RefreshModeUI();
        Closed += (_, _) =>
        {
            if (!_saved) ThemeCatalog.Apply(ThemeCatalog.Resolve(_settings, _originalDark));
        };
    }

    private static Dictionary<string, string> CopyValid(IDictionary<string, string>? source)
    {
        var value = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source != null)
            foreach (var (key, hex) in source)
                if (ThemeCatalog.RoleByKey.ContainsKey(key) && ThemeCatalog.IsHexColor(hex))
                    value[key] = hex.ToUpperInvariant();
        return value;
    }

    private Dictionary<string, string> Colors(bool dark) =>
        ThemeCatalog.Resolve(_preset[dark], dark, _overrides[dark]);

    private static TextBlock Txt(string value, int size = 13, FontWeight? weight = null)
    {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = System.Windows.VerticalAlignment.Center };
        if (weight.HasValue) text.FontWeight = weight.Value;
        Resource(text, TextBlock.ForegroundProperty, "MainText");
        return text;
    }

    private static void Resource(FrameworkElement element, DependencyProperty property, string key) =>
        element.SetResourceReference(property, key);

    private Button MakeButton(string content, Action action, bool primary = false)
    {
        var button = new Button { Content = content, MinHeight = 36, Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(0, 0, 8, 0) };
        if (primary) button.Style = (Style)System.Windows.Application.Current.FindResource("PrimaryButton");
        button.Click += (_, _) => action();
        return button;
    }

    private void BuildUI()
    {
        var main = new Grid { Margin = new Thickness(22, 19, 22, 18) };
        Resource(main, System.Windows.Controls.Panel.BackgroundProperty, "DialogBg");
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = main;

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(Txt("主题与配色工作台", 24, FontWeights.SemiBold));
        var caption = Txt("经典默认延续 v4.3。其他预设会改变整套界面配色；浅色与深色独立调整，修改可预览、可撤销。", 12);
        Resource(caption, TextBlock.ForegroundProperty, "SubText");
        caption.Margin = new Thickness(0, 6, 0, 0);
        head.Children.Add(caption);
        Grid.SetRow(head, 0); main.Children.Add(head);

        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        toolbar.Children.Add(Txt("编辑模式："));
        _modeSelect = new ComboBox { Width = 120, Margin = new Thickness(7, 0, 15, 0), ItemsSource = new[] { "浅色模式", "深色模式" } };
        _modeSelect.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || _modeSelect.SelectedIndex < 0) return;
            _dark = _modeSelect.SelectedIndex == 1;
            RefreshModeUI();
        };
        toolbar.Children.Add(_modeSelect);
        toolbar.Children.Add(Txt("预设："));
        _presetSelect = new ComboBox { Width = 155, Margin = new Thickness(7, 0, 15, 0), ItemsSource = ThemeCatalog.PresetNames };
        _presetSelect.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || _presetSelect.SelectedItem is not string preset) return;
            _preset[_dark] = preset;
            _overrides[_dark].Clear();
            RebuildRoles(); Preview();
        };
        toolbar.Children.Add(_presetSelect);
        toolbar.Children.Add(MakeButton("重置当前模式为默认", () =>
        {
            _preset[_dark] = ThemeCatalog.DefaultPreset;
            _overrides[_dark].Clear(); RefreshModeUI();
        }));
        toolbar.Children.Add(MakeButton("导出配色", Export));
        toolbar.Children.Add(MakeButton("导入配色", Import));
        Grid.SetRow(toolbar, 1); main.Children.Add(toolbar);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(columns, 2); main.Children.Add(columns);

        var editorPanel = new Grid();
        editorPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editorPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editorPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(editorPanel, 0); columns.Children.Add(editorPanel);
        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        _groupSelect = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 10, 0) };
        _groupSelect.Items.Add("全部区域");
        foreach (var group in ThemeCatalog.Roles.Select(x => x.Group).Distinct()) _groupSelect.Items.Add(group);
        _groupSelect.SelectedIndex = 0;
        _groupSelect.SelectionChanged += (_, _) => RebuildRoles();
        filters.Children.Add(_groupSelect);
        _search = new TextBox { Width = 195, MinHeight = 38, ToolTip = "搜索部件、颜色名称或资源 Key" };
        _search.TextChanged += (_, _) => RebuildRoles();
        filters.Children.Add(_search);
        Grid.SetRow(filters, 0); editorPanel.Children.Add(filters);
        _currentSummary = Txt("", 12);
        Resource(_currentSummary, TextBlock.ForegroundProperty, "SubText");
        _currentSummary.Margin = new Thickness(0, 0, 0, 9);
        Grid.SetRow(_currentSummary, 1); editorPanel.Children.Add(_currentSummary);
        _roleList = new StackPanel();
        var scroller = new ScrollViewer { Content = _roleList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 2); editorPanel.Children.Add(scroller);

        var right = new StackPanel();
        Grid.SetColumn(right, 2); columns.Children.Add(right);
        _previewCard = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12) };
        Resource(_previewCard, Border.BackgroundProperty, "CardBg");
        Resource(_previewCard, Border.BorderBrushProperty, "LineBrush");
        var pv = new StackPanel();
        _previewCard.Child = pv;
        pv.Children.Add(Txt("实时视觉预览", 17, FontWeights.SemiBold));
        var description = Txt("下方会跟随当前配色变化；实际主窗口也同步预览。", 12);
        description.Margin = new Thickness(0, 6, 0, 12);
        Resource(description, TextBlock.ForegroundProperty, "SubText");
        pv.Children.Add(description);
        var previewField = new TextBox { Text = "直播间号 / live.bilibili.com/6", IsReadOnly = true, Margin = new Thickness(0, 0, 0, 12) };
        pv.Children.Add(previewField);
        var buttons = new WrapPanel();
        var actionSample = MakeButton("解析直播源", () => { }, true);
        actionSample.IsHitTestVisible = false; actionSample.Focusable = false;
        var copySample = MakeButton("复制 URL", () => { });
        copySample.IsHitTestVisible = false; copySample.Focusable = false;
        buttons.Children.Add(actionSample);
        buttons.Children.Add(copySample);
        pv.Children.Add(buttons);
        var sampleTip = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 12, 0, 12), BorderThickness = new Thickness(1) };
        Resource(sampleTip, Border.BackgroundProperty, "HintBg");
        Resource(sampleTip, Border.BorderBrushProperty, "HintBorder");
        var tipText = Txt("提示：画质不完整，可扫码登录重新解析", 12);
        Resource(tipText, TextBlock.ForegroundProperty, "HintText");
        sampleTip.Child = tipText; pv.Children.Add(sampleTip);
        var row = new Border { Padding = new Thickness(10, 12, 10, 12), CornerRadius = new CornerRadius(7) };
        Resource(row, Border.BackgroundProperty, "GridSelectedBg");
        var rowText = Txt("2K 极清（高帧率）   ·   H.264 / AVC", 12);
        Resource(rowText, TextBlock.ForegroundProperty, "GridSelectedFg");
        row.Child = rowText; pv.Children.Add(row);
        right.Children.Add(_previewCard);
        var infoPanel = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14), BorderThickness = new Thickness(1) };
        Resource(infoPanel, Border.BorderBrushProperty, "LineBrush");
        Resource(infoPanel, Border.BackgroundProperty, "CardBg");
        var info = new StackPanel();
        info.Children.Add(Txt("文字可读性检查", 15, FontWeights.SemiBold));
        _contrast = Txt("", 12);
        _contrast.Margin = new Thickness(0, 10, 0, 0);
        info.Children.Add(_contrast);
        var manual = Txt("每次改变颜色都会检查 15 组文字与背景的对比度。预设主题优先考虑浅色和深色的可读性，自定义方案不强制替你修改颜色。", 12);
        Resource(manual, TextBlock.ForegroundProperty, "SubText");
        manual.Margin = new Thickness(0, 12, 0, 0);
        info.Children.Add(manual);
        infoPanel.Child = info; right.Children.Add(infoPanel);

        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 16, 0, 0) };
        var footerInfo = Txt("不会修改系统主题。关闭编辑器不保存任何未确认的配色修改。", 12);
        Resource(footerInfo, TextBlock.ForegroundProperty, "SubText");
        DockPanel.SetDock(footerInfo, Dock.Left); footer.Children.Add(footerInfo);
        var footerButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        footerButtons.Children.Add(MakeButton("取消更改", () => { DialogResult = false; }));
        footerButtons.Children.Add(MakeButton("保存并应用", Save, true));
        DockPanel.SetDock(footerButtons, Dock.Right); footer.Children.Add(footerButtons);
        Grid.SetRow(footer, 3); main.Children.Add(footer);
    }

    private void RefreshModeUI()
    {
        _rebuilding = true;
        _modeSelect.SelectedIndex = _dark ? 1 : 0;
        _presetSelect.SelectedItem = _preset[_dark];
        _rebuilding = false;
        RebuildRoles(); Preview();
    }

    private void RebuildRoles()
    {
        if (_roleList == null || _dark != (_modeSelect?.SelectedIndex == 1)) return;
        _roleList.Children.Clear();
        string group = _groupSelect.SelectedItem?.ToString() ?? "全部区域";
        string search = _search.Text.Trim();
        var roles = ThemeCatalog.Roles.Where(r => (group == "全部区域" || group == r.Group) &&
            (search.Length == 0 || r.Key.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             r.Caption.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             r.Group.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        var colors = Colors(_dark);
        _currentSummary.Text = $"{roles.Length} / {ThemeCatalog.Roles.Count} 个颜色角色 · 当前模式已自定义 {_overrides[_dark].Count} 项";
        foreach (var grouping in roles.GroupBy(x => x.Group))
        {
            var groupHeader = Txt(grouping.Key, 14, FontWeights.SemiBold);
            groupHeader.Margin = new Thickness(0, 14, 0, 6);
            _roleList.Children.Add(groupHeader);
            foreach (var role in grouping)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 8, 5), MinHeight = 40 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(43) });
                var name = Txt(role.Caption, 12); name.ToolTip = role.Group + " / " + role.Key;
                Grid.SetColumn(name, 0); row.Children.Add(name);
                var swatch = new Border { Width = 25, Height = 25, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                    BorderBrush = Brushes.Gray, Background = Brush(colors[role.Key]), ToolTip = "点击选择颜色" };
                swatch.MouseLeftButtonUp += (_, _) => PickColor(role.Key);
                Grid.SetColumn(swatch, 1); row.Children.Add(swatch);
                var hex = new TextBox { Text = colors[role.Key], MinHeight = 32, Width = 92, FontFamily = new FontFamily("Consolas"), FontSize = 12,
                    Padding = new Thickness(5, 4, 5, 4), ToolTip = role.Key + "：仅支持 #RRGGBB" };
                hex.TextChanged += (_, _) =>
                {
                    var value = hex.Text.Trim();
                    if (!ThemeCatalog.IsHexColor(value)) { hex.BorderBrush = Brushes.OrangeRed; return; }
                    var presetColor = ThemeCatalog.Resolve(_preset[_dark], _dark, null)[role.Key];
                    if (string.Equals(value, presetColor, StringComparison.OrdinalIgnoreCase)) _overrides[_dark].Remove(role.Key);
                    else _overrides[_dark][role.Key] = value.ToUpperInvariant();
                    swatch.Background = Brush(value);
                    hex.ClearValue(TextBox.BorderBrushProperty);
                    Preview();
                };
                Grid.SetColumn(hex, 2); row.Children.Add(hex);
                var reset = MakeButton("↺", () =>
                {
                    _overrides[_dark].Remove(role.Key);
                    hex.Text = Colors(_dark)[role.Key];
                    Preview();
                });
                reset.ToolTip = "恢复此项为当前预设的颜色";
                reset.Padding = new Thickness(4, 1, 4, 1);
                reset.Margin = new Thickness(6, 0, 0, 0);
                reset.MinWidth = 33;
                Grid.SetColumn(reset, 3); row.Children.Add(reset);
                _roleList.Children.Add(row);
            }
        }
    }

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex)!);

    private void PickColor(string key)
    {
        string current = Colors(_dark)[key];
        using var picker = new Forms.ColorDialog
        {
            Color = System.Drawing.ColorTranslator.FromHtml(current),
            FullOpen = true, AnyColor = true, SolidColorOnly = true
        };
        if (picker.ShowDialog() != Forms.DialogResult.OK) return;
        string value = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        string original = ThemeCatalog.Resolve(_preset[_dark], _dark, null)[key];
        if (value.Equals(original, StringComparison.OrdinalIgnoreCase)) _overrides[_dark].Remove(key);
        else _overrides[_dark][key] = value;
        RebuildRoles(); Preview();
    }

    private void Preview()
    {
        var palette = Colors(_dark);
        ThemeCatalog.Apply(palette);
        _contrast.Text = ThemeCatalog.ForegroundContrastTip(palette);
        _currentSummary.Text = $"{ThemeCatalog.Roles.Count} 个可编辑角色 · 已自定义 {_overrides[_dark].Count} 项 · {(_dark ? "深色" : "浅色")}";
        Resource(_contrast, TextBlock.ForegroundProperty, "MainText");
    }

    private void Save()
    {
        _settings.LightThemePreset = _preset[false];
        _settings.DarkThemePreset = _preset[true];
        _settings.LightThemeColors = CopyValid(_overrides[false]);
        _settings.DarkThemeColors = CopyValid(_overrides[true]);
        _settings.DarkTheme = _dark;
        SavedDarkMode = _dark;
        _saved = true;
        DialogResult = true;
    }

    public sealed class ThemePack
    {
        public int Version { get; set; } = 1;
        public string LightPreset { get; set; } = ThemeCatalog.DefaultPreset;
        public string DarkPreset { get; set; } = ThemeCatalog.DefaultPreset;
        public Dictionary<string, string> LightColors { get; set; } = new();
        public Dictionary<string, string> DarkColors { get; set; } = new();
    }

    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "BiliSnagTide 主题 (*.bstheme.json)|*.bstheme.json|JSON (*.json)|*.json", FileName = "BiliSnagTide-theme.bstheme.json" };
        if (dialog.ShowDialog(this) != true) return;
        var pack = new ThemePack { LightPreset = _preset[false], DarkPreset = _preset[true],
            LightColors = CopyValid(_overrides[false]), DarkColors = CopyValid(_overrides[true]) };
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(pack, new JsonSerializerOptions { WriteIndented = true }));
        System.Windows.MessageBox.Show(this, "配色方案已导出。文件仅包含颜色设置，不包含账号、Cookie、收藏或播放地址。", "导出成功");
    }

    private void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "BiliSnagTide 主题 (*.bstheme.json;*.json)|*.bstheme.json;*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 128_000) throw new InvalidDataException("主题文件超过 128 KB 限制。");
            var pack = JsonSerializer.Deserialize<ThemePack>(File.ReadAllText(dialog.FileName)) ?? throw new InvalidDataException("主题文件为空。");
            if (pack.Version != 1) throw new InvalidDataException("不支持此主题文件版本。");
            // No dynamic resources, file paths, URLs, markup or arbitrary keys can enter settings.
            _preset[false] = ThemeCatalog.SafePreset(pack.LightPreset);
            _preset[true] = ThemeCatalog.SafePreset(pack.DarkPreset);
            _overrides[false] = CopyValid(pack.LightColors);
            _overrides[true] = CopyValid(pack.DarkColors);
            RefreshModeUI();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "导入失败：" + ex.Message, "无法读取主题", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
