using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TodoFloat.Data;
using TodoFloat.Models;
using TodoFloat.ViewModels;

namespace TodoFloat.Views;

public partial class CategoriesDialog : Window
{
    private readonly MainViewModel _main;
    private readonly CategoryRepository _repo = new();

    public CategoriesDialog(MainViewModel main)
    {
        InitializeComponent();
        _main = main;
        Refresh();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    private void Refresh()
    {
        CatList.ItemsSource = null;
        CatList.ItemsSource = _repo.GetAll();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var name = NewNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var color = NewColorBox.Text.Trim();
        if (string.IsNullOrEmpty(color) || !color.StartsWith("#")) color = "#7E57C2";
        _repo.Insert(new Category { Name = name, Color = color, SortOrder = 99 });
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
