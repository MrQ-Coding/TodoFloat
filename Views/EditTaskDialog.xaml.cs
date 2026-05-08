using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TodoFloat.Data;
using TodoFloat.Models;
using TodoFloat.ViewModels;

namespace TodoFloat.Views;

public partial class EditTaskDialog : Window
{
    private readonly TaskItemViewModel _vm;
    private readonly TaskRepository _tasks;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public EditTaskDialog(
        TaskItemViewModel vm,
        IList<Category> categories,
        TaskRepository tasks,
        CategoryRepository _)
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        };
        _vm = vm;
        _tasks = tasks;

        TitleBox.Text = vm.Model.Title;
        NotesBox.Text = vm.Model.Notes ?? string.Empty;
        PriorityCombo.SelectedIndex = (int)vm.Priority;

        var cats = new List<Category> { new() { Id = 0, Name = "(无)" } };
        cats.AddRange(categories);
        CategoryCombo.ItemsSource = cats;
        CategoryCombo.SelectedValue = vm.Model.CategoryId ?? 0L;

        if (vm.Model.DueAt is { } d)
        {
            var local = d.ToLocalTime();
            DueDate.SelectedDate = local.Date;
            DueTime.Text = local.ToString("HH:mm");
        }
        if (vm.Model.RemindAt is { } r)
        {
            var local = r.ToLocalTime();
            RemindDate.SelectedDate = local.Date;
            RemindTime.Text = local.ToString("HH:mm");
        }

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var t = _vm.Model;
        t.Title = TitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(t.Title))
        {
            MessageBox.Show(this, "标题不能为空", "TodoFloat");
            return;
        }
        t.Notes = string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text;
        t.Priority = (TaskPriority)int.Parse(((ComboBoxItem)PriorityCombo.SelectedItem).Tag.ToString()!);
        var catId = (long?)CategoryCombo.SelectedValue ?? 0;
        t.CategoryId = catId == 0 ? null : catId;
        t.DueAt = ParseDateTime(DueDate.SelectedDate, DueTime.Text);
        t.RemindAt = ParseDateTime(RemindDate.SelectedDate, RemindTime.Text);
        _tasks.Update(t);
        DialogResult = true;
        Close();
    }

    private void ClearDue_Click(object sender, RoutedEventArgs e)
    {
        DueDate.SelectedDate = null; DueTime.Text = "";
    }
    private void ClearRemind_Click(object sender, RoutedEventArgs e)
    {
        RemindDate.SelectedDate = null; RemindTime.Text = "";
    }

    private static DateTime? ParseDateTime(DateTime? date, string time)
    {
        if (date is null) return null;
        var t = TimeSpan.Zero;
        if (!string.IsNullOrWhiteSpace(time))
        {
            if (TimeSpan.TryParseExact(time.Trim(), new[] { @"h\:m", @"hh\:mm" }, CultureInfo.InvariantCulture, out var parsed))
                t = parsed;
            else if (TimeSpan.TryParse(time.Trim(), out parsed))
                t = parsed;
        }
        var local = date.Value.Date.Add(t);
        return local.ToUniversalTime();
    }
}
