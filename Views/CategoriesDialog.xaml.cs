using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using TodoFloat.Data;
using TodoFloat.Models;
using TodoFloat.ViewModels;

namespace TodoFloat.Views;

public partial class CategoriesDialog : Window
{
    private static readonly string[] Palette =
    [
        "#E8915E",
        "#7B98E8",
        "#5FB58A",
        "#C57BB8",
        "#D9A235",
        "#6CA6A6"
    ];

    private readonly MainViewModel _main;
    private readonly CategoryRepository _repo = new();
    private string _selectedColor = Palette[0];

    public CategoriesDialog(MainViewModel main)
    {
        InitializeComponent();
        _main = main;
        BuildColorPalette();
        Refresh();
        SourceInitialized += (_, _) => ApplyDwmRoundedCorners();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed
                && e.OriginalSource is FrameworkElement fe
                && !IsInteractive(fe))
            {
                DragMove();
            }
        };
    }

    // —— Win11 DWM 圆角：和主窗口一致，避免 WindowStyle=None 时角落露出方形外框 ——
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private void ApplyDwmRoundedCorners()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    private static bool IsInteractive(FrameworkElement fe)
    {
        DependencyObject? d = fe;
        while (d is not null)
        {
            if (d is ButtonBase or TextBoxBase) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Refresh()
    {
        CatList.ItemsSource = null;
        // 倒序展示：最新加的（id 最大）排在最上面
        var all = _repo.GetAll().OrderByDescending(c => c.Id).ToList();
        CatList.ItemsSource = all;
        var idx = all.Count % Palette.Length;
        SetSelectedColor(Palette[idx]);
    }

    private void BuildColorPalette()
    {
        ColorGrid.Children.Clear();
        foreach (var hex in Palette)
        {
            var swatchBtn = new Button
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(3),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Tag = hex
            };
            swatchBtn.Template = BuildSwatchTemplate(hex);
            swatchBtn.Click += (_, _) =>
            {
                SetSelectedColor(hex);
                ColorPopup.IsOpen = false;
            };
            ColorGrid.Children.Add(swatchBtn);
        }
    }

    private static ControlTemplate BuildSwatchTemplate(string hex)
    {
        var tpl = new ControlTemplate(typeof(Button));
        var outer = new FrameworkElementFactory(typeof(Border));
        outer.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        outer.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        outer.SetValue(Border.PaddingProperty, new Thickness(3));
        outer.Name = "Bd";

        var dot = new FrameworkElementFactory(typeof(Ellipse));
        dot.SetValue(Shape.FillProperty, BrushFromHex(hex));
        dot.SetValue(FrameworkElement.WidthProperty, 18.0);
        dot.SetValue(FrameworkElement.HeightProperty, 18.0);
        outer.AppendChild(dot);

        tpl.VisualTree = outer;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, BrushFromHex("#14000000"), "Bd"));
        tpl.Triggers.Add(hoverTrigger);

        return tpl;
    }

    private static Brush BrushFromHex(string hex)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch
        {
            return Brushes.MediumPurple;
        }
    }

    private void SetSelectedColor(string hex)
    {
        _selectedColor = hex;
        if (ColorSwatchButton is not null)
        {
            ColorSwatchButton.Tag = BrushFromHex(hex);
        }
    }

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        ColorPopup.IsOpen = !ColorPopup.IsOpen;
    }

    private void NewNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DoAdd();
            e.Handled = true;
        }
    }

    private void DoAdd()
    {
        var name = NewNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        _repo.Insert(new Category { Name = name, Color = _selectedColor, SortOrder = 99 });
        NewNameBox.Text = string.Empty;
        Refresh();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is Category c)
        {
            _repo.Delete(c.Id);
            Refresh();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
