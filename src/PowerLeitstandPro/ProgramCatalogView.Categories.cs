using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace PowerControlProWpf;

internal sealed partial class ProgramCatalogView
{
    private Button? _moreCategories;
    private Border? _additionalCategories;
    internal Button MoreCategoriesButton => _moreCategories!;
    internal Border AdditionalCategoriesFrame => _additionalCategories!;
    private void InitializeAdditionalCategories(Grid tabs)
    {
        tabs.ColumnDefinitions.Add(new() { Width = new GridLength(64) });
        _moreCategories = Action("", 27); _moreCategories.FontSize = 10.5; _moreCategories.Margin = new(4, 0, 0, 0);
        Grid.SetColumn(_moreCategories, tabs.ColumnDefinitions.Count - 1); tabs.Children.Add(_moreCategories);
        _moreCategories.Click += (_, _) =>
        {
            if (_additionalCategories!.Visibility == Visibility.Visible) CloseAdditionalCategories();
            else { CloseInfo(); _additionalCategories.Visibility = Visibility.Visible; PositionAdditionalCategories(); }
        };
        _additionalCategories = new Border { Width = 220, CornerRadius = new(8), BorderThickness = new(1), Padding = new(8),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
        _additionalCategories.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        var list = new StackPanel(); string[] glyphs = ["\uE8A5", "\uE716", "\uE968"];
        for (int n = 0; n < ProgramCatalog.AdditionalCategories.Length; n++)
        {
            var id = ProgramCatalog.AdditionalCategories[n]; var button = Action("", 31);
            button.Margin = new(0, 0, 0, 5); button.HorizontalContentAlignment = HorizontalAlignment.Left; button.Tag = glyphs[n];
            button.Click += (_, _) => SetCategory(id); _categories[id] = button; list.Children.Add(button);
        }
        _packagesButton = Action("", 31); _packagesButton.Margin = new(0, 0, 0, 5);
        _packagesButton.Click += async (_, _) => await OpenDeveloperPackagesAsync(); list.Children.Add(_packagesButton);
        _additionalCategories.Child = list; Grid.SetRowSpan(_additionalCategories, 5); Panel.SetZIndex(_additionalCategories, 2000); _root.Children.Add(_additionalCategories);
        _root.PreviewMouseDown += (_, _) =>
        { if (!_additionalCategories.IsMouseOver && !_moreCategories.IsMouseOver) CloseAdditionalCategories(); };
        _root.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _additionalCategories.Visibility == Visibility.Visible) { CloseAdditionalCategories(); e.Handled = true; _moreCategories.Focus(); } };
        Unloaded += (_, _) => CloseAdditionalCategories();
    }
    private void UpdateAdditionalCategories()
    {
        if (_moreCategories is null || _additionalCategories is null) return;
        ProgramButtonIcons.Set(_moreCategories, "more", L("Mehr", "More"), true);
        if (_packagesButton is not null) ProgramButtonIcons.Set(_packagesButton, "development", L("Paketmanager", "Package managers"), true);
        _moreCategories.ToolTip = L("Weitere Kategorien", "More categories") + " · " + ProgramCatalog.Category(_category, _english);
        System.Windows.Automation.AutomationProperties.SetName(_moreCategories, L("Weitere Kategorien", "More categories"));
        _moreCategories.BorderThickness = new(ProgramCatalog.AdditionalCategories.Contains(_category) ? 2 : 1);
        var color = (TryFindResource("InputBrush") as SolidColorBrush)?.Color ?? (Color)ColorConverter.ConvertFromString(Palette.Colors[2]);
        _additionalCategories.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
    }
    private void CloseAdditionalCategories() { if (_additionalCategories is not null) _additionalCategories.Visibility = Visibility.Collapsed; }
    private void PositionAdditionalCategories()
    {
        if (_additionalCategories is null || _moreCategories is null || _additionalCategories.Visibility != Visibility.Visible || !_moreCategories.IsLoaded) return;
        var point = _moreCategories.TranslatePoint(new Point(0, _moreCategories.ActualHeight + 5), _root);
        _additionalCategories.Measure(new Size(220, double.PositiveInfinity));
        _additionalCategories.Margin = new(0, Math.Max(0, Math.Min(point.Y, _root.ActualHeight - _additionalCategories.DesiredSize.Height)), 0, 0);
    }
}
