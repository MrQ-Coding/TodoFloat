using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace TodoFloat.Views;

public partial class DesktopPetWindow : Window
{
    public event EventHandler? ActivateRequested;
    public event EventHandler? PositionChangedByDrag;

    public DesktopPetWindow()
    {
        InitializeComponent();
    }

    public void KeepOnTop()
    {
        Topmost = true;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        SetWindowPos(
            hwnd,
            HWND_TOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        KeepOnTop();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        KeepOnTop();
    }

    private void PetRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var startLeft = Left;
        var startTop = Top;

        try
        {
            DragMove();
        }
        catch
        {
            // DragMove can throw if the mouse capture state changes during click.
        }

        var moved = Math.Abs(Left - startLeft) > 3 || Math.Abs(Top - startTop) > 3;

        if (moved)
        {
            KeepOnTop();
            PositionChangedByDrag?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            ActivateRequested?.Invoke(this, EventArgs.Empty);
        }

        e.Handled = true;
    }

    private void PetRoot_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateScale(1.08);
    }

    private void PetRoot_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateScale(1.0);
    }

    private void AnimateScale(double scale)
    {
        var animation = new DoubleAnimation(scale, TimeSpan.FromMilliseconds(90))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        PetScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, animation);
        PetScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, animation);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
}
