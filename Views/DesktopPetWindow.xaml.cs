using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TodoFloat.Views;

public partial class DesktopPetWindow : Window
{
    // ── Bloom layout constants (mirror prototype) ───────────────
    private const double Radius = 210;
    private const double StartAngleDeg = -90;
    private const int StaggerMs = 55;
    private const int OpenDurMs = 550;
    private const int CloseDurMs = 380;
    private const int IdleMs = 5000;
    private const double DragThreshold = 5;
    private const int TodoPreviewUnits = 18;

    private record PanelEntry(
        Border Element,
        TranslateTransform Translate,
        ScaleTransform Scale,
        double TargetX,
        double TargetY);

    private readonly List<PanelEntry> _panels = new();
    private bool _bloomed;
    private bool _sticky;
    private bool _sleeping;
    private bool _chatOpen;
    private string _mood = "happy";
    private DispatcherTimer? _idleTimer;
    private DispatcherTimer? _hideTimer;

    // Drag-vs-click disambiguation
    private bool _dragArmed;
    private Point _dragStart;

    // Chat refs
    private Border _chatCard = null!;
    private FrameworkElement _chatCollapsed = null!;
    private FrameworkElement _chatExpanded = null!;
    private StackPanel _chatLog = null!;
    private ScrollViewer _chatLogScroll = null!;
    private TextBox _chatInput = null!;
    private Button _chatSend = null!;
    private StackPanel? _todoItemsHost;
    private readonly List<(string role, string content)> _history = new();
    private readonly NibbleResponder _responder = new();

    public event EventHandler? ActivateRequested;
    public event EventHandler? PositionChangedByDrag;
    public Func<IReadOnlyList<DesktopPetTodoItem>>? TodoItemsProvider { get; set; }

    public DesktopPetWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        PreviewKeyDown += OnGlobalKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildPanels();
        StartIdleAnimations();
        WirePetInteractions();
        ResetIdleTimer();
        KeepOnTop();
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

    private void OnGlobalKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_chatOpen) CloseChat();
            Bloom(false, force: true);
            _sticky = false;
        }
    }

    // ── Wire interactions ───────────────────────────────────────
    private void WirePetInteractions()
    {
        Pet.MouseEnter += (_, _) => { CancelHide(); Bloom(true); ResetIdleTimer(); };
        Scene.MouseLeave += (_, _) => ScheduleHide();
        Ring.MouseEnter  += (_, _) => { CancelHide(); Bloom(true); ResetIdleTimer(); };

        // Activity reset — only counts for events over Pet / Scene / Ring
        Scene.MouseMove += (_, _) => ResetIdleTimer();
        Pet.MouseMove   += (_, _) => ResetIdleTimer();
        Ring.MouseMove  += (_, _) => ResetIdleTimer();

        // Drag-or-click on the pet
        Pet.MouseLeftButtonDown += Pet_LeftDown;
        Pet.MouseMove           += Pet_MouseMove;
        Pet.MouseLeftButtonUp   += Pet_LeftUp;
    }

    private void Pet_LeftDown(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = true;
        _dragStart = e.GetPosition(this);
        Pet.CaptureMouse();
        e.Handled = true;
    }
    private void Pet_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) > DragThreshold ||
            Math.Abs(p.Y - _dragStart.Y) > DragThreshold)
        {
            _dragArmed = false;
            Pet.ReleaseMouseCapture();
            try { DragMove(); } catch { /* clicked outside title bar territory */ }
            KeepOnTop();
            PositionChangedByDrag?.Invoke(this, EventArgs.Empty);
        }
    }
    private void Pet_LeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragArmed) return;
        _dragArmed = false;
        Pet.ReleaseMouseCapture();
        // True click → toggle sticky bloom
        _sticky = !_sticky;
        Bloom(_sticky);
        ActivateRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void ScheduleHide()
    {
        CancelHide();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _hideTimer.Tick += (_, _) =>
        {
            CancelHide();
            if (!_sticky && !_chatOpen) Bloom(false);
        };
        _hideTimer.Start();
    }
    private void CancelHide()
    {
        _hideTimer?.Stop();
        _hideTimer = null;
    }

    // ── Bloom mechanic ──────────────────────────────────────────
    private void Bloom(bool open, bool force = false)
    {
        if (!force && open == _bloomed) return;
        if (!open && _chatOpen && !force) return;
        _bloomed = open;

        if (open)
        {
            RefreshTodoCard();
        }

        for (int i = 0; i < _panels.Count; i++)
        {
            var p = _panels[i];
            int delay = open ? i * StaggerMs : (_panels.Count - 1 - i) * 28;
            int dur   = open ? OpenDurMs : CloseDurMs;
            IEasingFunction ease = open
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
                : new CubicEase { EasingMode = EasingMode.EaseIn };

            double tx = open ? p.TargetX : 0;
            double ty = open ? p.TargetY : 0;
            double sc = open ? 1.0 : 0.2;
            double op = open ? 1.0 : 0.0;

            AnimDouble(p.Translate, TranslateTransform.XProperty, tx, dur, delay, ease);
            AnimDouble(p.Translate, TranslateTransform.YProperty, ty, dur, delay, ease);
            AnimDouble(p.Scale, ScaleTransform.ScaleXProperty, sc, dur, delay, ease);
            AnimDouble(p.Scale, ScaleTransform.ScaleYProperty, sc, dur, delay, ease);

            var opAnim = new DoubleAnimation(op, TimeSpan.FromMilliseconds(open ? 350 : 250))
            { BeginTime = TimeSpan.FromMilliseconds(delay) };
            p.Element.BeginAnimation(UIElement.OpacityProperty, opAnim);

            p.Element.IsHitTestVisible = open;
        }

        // Pet "called!" squish + aura brighten
        AnimDouble(PetSquish, ScaleTransform.ScaleXProperty, open ? 0.92 : 1.0, 350, 0, new CubicEase { EasingMode = EasingMode.EaseOut });
        AnimDouble(PetSquish, ScaleTransform.ScaleYProperty, open ? 0.92 : 1.0, 350, 0, new CubicEase { EasingMode = EasingMode.EaseOut });
        AnimDouble(AuraScale, ScaleTransform.ScaleXProperty, open ? 1.15 : 0.95, 600, 0, new CubicEase { EasingMode = EasingMode.EaseOut });
        AnimDouble(AuraScale, ScaleTransform.ScaleYProperty, open ? 1.15 : 0.95, 600, 0, new CubicEase { EasingMode = EasingMode.EaseOut });

        // Bloomed → blink faster (excited)
        if (open) RestartBlink(durationSec: 2.2);
        else      RestartBlink(durationSec: 4.6);
    }

    // ── Idle / Sleep ────────────────────────────────────────────
    private void ResetIdleTimer()
    {
        if (_sleeping) WakeUp();
        if (_idleTimer == null)
        {
            _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IdleMs) };
            _idleTimer.Tick += (_, _) =>
            {
                _idleTimer!.Stop();
                if (_bloomed || _chatOpen) { ResetIdleTimer(); return; }
                GoToSleep();
            };
        }
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private void GoToSleep()
    {
        if (_sleeping) return;
        _sleeping = true;

        // Eyes shut
        AnimDouble(LeftEyeScale, ScaleTransform.ScaleYProperty, 0.08, 400);
        AnimDouble(RightEyeScale, ScaleTransform.ScaleYProperty, 0.08, 400);
        LeftEyeSpark.Visibility = LeftEyeSpark2.Visibility = Visibility.Collapsed;
        RightEyeSpark.Visibility = RightEyeSpark2.Visibility = Visibility.Collapsed;

        // Mouth → tiny dash
        Mouth.Data = Geometry.Parse("M 0,0 L 8,0");
        Mouth.Fill = Brushes.Transparent;
        Mouth.StrokeThickness = 2;

        // Tilt head & slower bob
        AnimDouble(PetTilt, RotateTransform.AngleProperty, -3, 600);

        // Antenna → purple tint
        AntennaBulb.Fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0xD7,0xCD,0xE3), 0),
                new GradientStop(Color.FromRgb(0xA8,0x9B,0xC4), 1),
            }
        };
        AntennaGlow.Color = Color.FromRgb(0xB4,0xAA,0xC8);
        AntennaGlow.BlurRadius = 8;
        AntennaGlow.Opacity = 0.6;
        AntennaGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null); // stop blink
        AntennaGlow.BeginAnimation(DropShadowEffect.OpacityProperty, null);

        // Aura → cool tint
        Aura.Fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x66,0xA5,0xB4,0xC8), 0),
                new GradientStop(Color.FromArgb(0x00,0xA5,0xB4,0xC8), 0.6),
            }
        };

        // Zzz appear + float
        Zzz.Opacity = 0;
        Zzz.BeginAnimation(OpacityProperty, new DoubleAnimation(0.85, TimeSpan.FromMilliseconds(300)));
        var zzzX = new DoubleAnimation(0, 16, TimeSpan.FromSeconds(3))
        { RepeatBehavior = RepeatBehavior.Forever };
        var zzzY = new DoubleAnimation(0, -20, TimeSpan.FromSeconds(3))
        { RepeatBehavior = RepeatBehavior.Forever };
        var zzzOp = new DoubleAnimationUsingKeyFrames
        { RepeatBehavior = RepeatBehavior.Forever };
        zzzOp.KeyFrames.Add(new LinearDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        zzzOp.KeyFrames.Add(new LinearDoubleKeyFrame(1.0,  KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.5))));
        zzzOp.KeyFrames.Add(new LinearDoubleKeyFrame(0.0,  KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3))));
        ZzzT.BeginAnimation(TranslateTransform.XProperty, zzzX);
        ZzzT.BeginAnimation(TranslateTransform.YProperty, zzzY);
        Zzz.BeginAnimation(OpacityProperty, zzzOp);

        // Stop the eye-blink keyframes
        LeftEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RightEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        LeftEyeScale.ScaleY = 0.08;
        RightEyeScale.ScaleY = 0.08;
    }

    private void WakeUp()
    {
        if (!_sleeping) return;
        _sleeping = false;

        LeftEyeSpark.Visibility = LeftEyeSpark2.Visibility = Visibility.Visible;
        RightEyeSpark.Visibility = RightEyeSpark2.Visibility = Visibility.Visible;

        // Restore mouth
        Mouth.Data = Geometry.Parse("M 0,0 L 14,0 L 14,3 C 14,9 0,9 0,3 Z");
        Mouth.Fill = new SolidColorBrush(Color.FromRgb(0x7A,0x2E,0x1E));
        Mouth.StrokeThickness = 2;

        // Untilt
        AnimDouble(PetTilt, RotateTransform.AngleProperty, 0, 350);

        // Restore antenna
        AntennaBulb.Fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0xFF,0xE3,0x8A), 0),
                new GradientStop(Color.FromRgb(0xF4,0xA5,0x74), 0.6),
                new GradientStop(Color.FromRgb(0xD9,0x62,0x2A), 1),
            }
        };
        AntennaGlow.Color = Color.FromRgb(0xF4,0xA5,0x74);
        AntennaGlow.BlurRadius = 14;
        AntennaGlow.Opacity = 0.7;
        StartAntennaBlink();

        // Restore aura
        Aura.Fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x73,0xF4,0xA5,0x74), 0),
                new GradientStop(Color.FromArgb(0x00,0xF4,0xA5,0x74), 0.6),
            }
        };

        // Hide Zzz
        ZzzT.BeginAnimation(TranslateTransform.XProperty, null);
        ZzzT.BeginAnimation(TranslateTransform.YProperty, null);
        Zzz.BeginAnimation(OpacityProperty, null);
        Zzz.Opacity = 0;
        ZzzT.X = 0; ZzzT.Y = 0;

        // Resume blink
        RestartBlink(4.6);
    }

    // ── Idle animations setup ───────────────────────────────────
    private void StartIdleAnimations()
    {
        // Pet bob
        var bob = new DoubleAnimation(0, -8, TimeSpan.FromSeconds(1.7))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        PetBob.BeginAnimation(TranslateTransform.YProperty, bob);

        // Antenna wiggle
        var ant = new DoubleAnimation(-8, 8, TimeSpan.FromSeconds(1.7))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        AntennaRot.BeginAnimation(RotateTransform.AngleProperty, ant);

        // Ground breathe
        var gx = new DoubleAnimation(1, 0.84, TimeSpan.FromSeconds(1.7))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        GroundScale.BeginAnimation(ScaleTransform.ScaleXProperty, gx);
        GroundScale.BeginAnimation(ScaleTransform.ScaleYProperty, gx.Clone());

        // Aura pulse
        var auraSc = new DoubleAnimation(0.95, 1.05, TimeSpan.FromSeconds(1.3))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        AuraScale.BeginAnimation(ScaleTransform.ScaleXProperty, auraSc);
        AuraScale.BeginAnimation(ScaleTransform.ScaleYProperty, auraSc.Clone());
        var auraOp = new DoubleAnimation(0.55, 0.85, TimeSpan.FromSeconds(1.3))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Aura.BeginAnimation(OpacityProperty, auraOp);

        StartAntennaBlink();
        RestartBlink(4.6);
        StartPixelFloats();
    }

    private void StartAntennaBlink()
    {
        var blur = new DoubleAnimation(14, 22, TimeSpan.FromSeconds(0.8))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        var opa = new DoubleAnimation(0.7, 1.0, TimeSpan.FromSeconds(0.8))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        AntennaGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blur);
        AntennaGlow.BeginAnimation(DropShadowEffect.OpacityProperty, opa);
    }

    private void RestartBlink(double durationSec)
    {
        var k = (Action<DoubleAnimationUsingKeyFrames>)((kf) =>
        {
            kf.Duration = TimeSpan.FromSeconds(durationSec);
            kf.RepeatBehavior = RepeatBehavior.Forever;
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(durationSec * 0.46))));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(durationSec * 0.48))));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(durationSec * 0.5))));
            kf.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(durationSec))));
        });
        var a = new DoubleAnimationUsingKeyFrames(); k(a);
        var b = new DoubleAnimationUsingKeyFrames(); k(b);
        LeftEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        RightEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, b);
    }

    private void StartPixelFloats()
    {
        // Each sparkle floats slightly + spins. Periods/amplitudes from prototype.
        FloatSparkle(Pix1T, Pix1R, dx: -6, dy: -10, deg:  45, periodSec: 2.1);
        FloatSparkle(Pix2T, Pix2R, dx:  8, dy:  -8, deg: -30, periodSec: 1.8);
        FloatSparkle(Pix3T, Pix3R, dx:-10, dy:   6, deg:  20, periodSec: 2.4);
        FloatSparkle(Pix4T, Pix4R, dx:  6, dy:  10, deg: -45, periodSec: 1.6);
    }

    private static void FloatSparkle(TranslateTransform t, RotateTransform r,
        double dx, double dy, double deg, double periodSec)
    {
        var xa = new DoubleAnimation(0, dx, TimeSpan.FromSeconds(periodSec))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
          EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        var ya = new DoubleAnimation(0, dy, TimeSpan.FromSeconds(periodSec))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
          EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        var ra = new DoubleAnimation(0, deg, TimeSpan.FromSeconds(periodSec))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
          EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        t.BeginAnimation(TranslateTransform.XProperty, xa);
        t.BeginAnimation(TranslateTransform.YProperty, ya);
        r.BeginAnimation(RotateTransform.AngleProperty, ra);
    }

    // ── Build the 6 floating cards ──────────────────────────────
    private void BuildPanels()
    {
        var cards = new (Border card, double tilt)[]
        {
            (BuildChatCard(),     2.5),
            (BuildCalendarCard(),-2.5),
            (BuildTimerCard(),    2.5),
            (BuildTodoCard(),    -2.5),
            (BuildMemoCard(),     2.5),
            (BuildReminderCard(),-2.5),
        };

        int n = cards.Length;
        double startRad = StartAngleDeg * Math.PI / 180.0;
        double step = 2 * Math.PI / n;

        for (int i = 0; i < n; i++)
        {
            var (card, tilt) = cards[i];
            double angle = startRad + step * i;
            double tx = Math.Cos(angle) * Radius;
            double ty = Math.Sin(angle) * Radius;

            var tt = new TranslateTransform(0, 0);
            var st = new ScaleTransform(0.2, 0.2);
            var rt = new RotateTransform(tilt);
            card.RenderTransformOrigin = new Point(0.5, 0.5);
            card.RenderTransform = new TransformGroup { Children = { tt, st, rt } };
            card.Opacity = 0;
            card.IsHitTestVisible = false;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;

            // Hover bookkeeping → keep open + reset idle
            card.MouseEnter += (_, _) => { CancelHide(); Bloom(true); ResetIdleTimer(); };
            card.MouseMove  += (_, _) => ResetIdleTimer();

            Ring.Children.Add(card);
            _panels.Add(new PanelEntry(card, tt, st, tx, ty));
        }
    }

    private Border MakeCardShell(Color? bgOverride = null, double maxWidth = 220)
    {
        var card = new Border
        {
            Style = (Style)FindResource("CardStyle"),
            MaxWidth = maxWidth,
            MinWidth = 140,
        };
        if (bgOverride.HasValue)
            card.Background = new SolidColorBrush(bgOverride.Value);
        return card;
    }

    private StackPanel MakeHeader(string ico, Color icoBg, Color icoFg, string hdText)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,6) };
        var icoBorder = new Border
        {
            Style = (Style)FindResource("IcoStyle"),
            Background = new SolidColorBrush(icoBg),
            Child = new TextBlock
            {
                Text = ico,
                Foreground = new SolidColorBrush(icoFg),
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                LineHeight = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };
        sp.Children.Add(icoBorder);
        sp.Children.Add(new TextBlock { Text = hdText, Style = (Style)FindResource("HdLabelStyle") });
        return sp;
    }

    private Border BuildChatCard()
    {
        var card = MakeCardShell(bgOverride: Color.FromArgb(0xF2, 0xFF, 0xF2, 0xDD), maxWidth: 220);
        var stack = new StackPanel();
        stack.Children.Add(MakeHeader("AI",
            Color.FromRgb(0xF0,0x87,0x70),
            Color.FromRgb(0xFF,0xF6,0xE6),
            "chat"));
        AddPlaceholderContent(stack, "功能预留");
        card.Child = stack;
        return card;
    }

    private Border BuildCalendarCard()
    {
        var card = MakeCardShell();
        var stack = new StackPanel();
        stack.Children.Add(MakeHeader("日",
            Color.FromRgb(0xFF,0xD8,0x9A),
            Color.FromRgb(0x4A,0x2E,0x18),
            "calendar"));
        AddPlaceholderContent(stack, "功能预留");
        card.Child = stack;
        return card;
    }

    private Border BuildTimerCard()
    {
        var card = MakeCardShell(bgOverride: Color.FromArgb(0xF0, 0xFF, 0xEC, 0xD2));
        var stack = new StackPanel();
        stack.Children.Add(MakeHeader("⏱",
            Color.FromRgb(0xF4,0xA5,0x74),
            Color.FromRgb(0x4A,0x2E,0x18),
            "focus"));
        AddPlaceholderContent(stack, "功能预留");
        card.Child = stack;
        return card;
    }

    private Border BuildTodoCard()
    {
        var card = MakeCardShell();
        var stack = new StackPanel();
        stack.Children.Add(MakeHeader("✓",
            Color.FromRgb(0xF4,0xA5,0x74),
            Color.FromRgb(0x4A,0x2E,0x18),
            "todo"));
        _todoItemsHost = new StackPanel();
        stack.Children.Add(_todoItemsHost);
        card.Child = stack;
        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (!_bloomed) return;
            ActivateRequested?.Invoke(this, EventArgs.Empty);
            ResetIdleTimer();
            e.Handled = true;
        };
        RefreshTodoCard();
        return card;
    }

    public void RefreshTodoCard()
    {
        if (_todoItemsHost is null) return;

        _todoItemsHost.Children.Clear();

        IReadOnlyList<DesktopPetTodoItem> items;
        try
        {
            items = TodoItemsProvider?.Invoke() ?? [];
        }
        catch
        {
            items = [];
        }

        if (items.Count == 0)
        {
            _todoItemsHost.Children.Add(new TextBlock
            {
                Text = "暂无待办",
                Style = (Style)FindResource("SubStyle"),
                Margin = new Thickness(0, 2, 0, 0)
            });
            return;
        }

        foreach (var item in items.Take(4))
        {
            _todoItemsHost.Children.Add(TodoItem(PreviewTodoTitle(item.Title), item.Completed));
        }
    }

    private static string PreviewTodoTitle(string title)
    {
        var text = (title ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text)) return "未命名待办";

        var units = 0;
        var end = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var nextUnits = units + GetPreviewUnits(text[i]);
            if (nextUnits > TodoPreviewUnits) break;

            units = nextUnits;
            end = i + 1;
        }

        if (end >= text.Length) return text;

        return text[..end].TrimEnd() + "...";
    }

    private static int GetPreviewUnits(char c) =>
        c <= 0x007F ? 1 : 2;

    private StackPanel TodoItem(string text, bool done)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,2,0,0) };
        var chk = new Border
        {
            Width = 12, Height = 12,
            BorderBrush = (Brush)FindResource("OutlineBrush"),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(3),
            Background = done
                ? (Brush)FindResource("Pet1Brush")
                : (Brush)FindResource("CreamBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0,0,7,0),
        };
        if (done) chk.Child = new TextBlock {
            Text = "✓", FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("OutlineBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(chk);
        var tb = new TextBlock {
            Text = text, FontSize = 12.5,
            Foreground = (Brush)FindResource("InkBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 175,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (done)
        {
            tb.TextDecorations = TextDecorations.Strikethrough;
            tb.Foreground = (Brush)FindResource("Ink3Brush");
        }
        row.Children.Add(tb);
        return row;
    }

    private Border BuildMemoCard()
    {
        var card = MakeCardShell();
        var stack = new StackPanel();
        stack.Children.Add(MakeHeader("✎",
            Color.FromRgb(0xFF,0xD8,0x9A),
            Color.FromRgb(0x4A,0x2E,0x18),
            "memo"));
        AddPlaceholderContent(stack, "功能预留");
        card.Child = stack;
        return card;
    }

    private Border MemoLine(string text, bool last = false)
    {
        var b = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x4D,0x4A,0x2E,0x18)),
            BorderThickness = last ? new Thickness(0) : new Thickness(0,0,0,1),
            Padding = new Thickness(0,3,0,3),
            Child = new TextBlock {
                Text = text,
                FontFamily = new FontFamily("ZCOOL KuaiLe, Microsoft YaHei UI, Segoe UI"),
                FontSize = 14,
                Foreground = (Brush)FindResource("InkBrush"),
            }
        };
        return b;
    }

    private Border BuildReminderCard()
    {
        var card = MakeCardShell(bgOverride: Color.FromArgb(0xF2, 0xFF, 0xE4, 0xCD));
        var stack = new StackPanel();
        stack.Children.Add(MakeHeader("!",
            Color.FromRgb(0xD9,0x62,0x2A),
            Color.FromRgb(0xFF,0xF6,0xE6),
            "reminder"));
        AddPlaceholderContent(stack, "功能预留");
        card.Child = stack;
        return card;
    }

    private void AddPlaceholderContent(Panel host, string title)
    {
        host.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("TtlStyle")
        });
    }

    private StackPanel ReminderRow(Color pipColor, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,3,0,0) };
        row.Children.Add(new Ellipse {
            Width = 6, Height = 6,
            Fill = new SolidColorBrush(pipColor),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0,0,6,0)
        });
        row.Children.Add(new TextBlock {
            Text = text, FontSize = 12,
            Foreground = (Brush)FindResource("Ink2Brush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    // ── Chat handling ───────────────────────────────────────────
    private void OpenChat()
    {
        if (_chatOpen) return;
        _chatOpen = true;
        _chatCard.MaxWidth = 320;
        _chatCard.MinWidth = 260;
        _chatCollapsed.Visibility = Visibility.Collapsed;
        _chatExpanded.Visibility = Visibility.Visible;
        if (_history.Count == 0)
            AddMsg("pet", "嗨～我在哦，想聊点什么？");
        SetMood("excited");
        Dispatcher.BeginInvoke(new Action(() => _chatInput.Focus()), DispatcherPriority.Input);
    }

    private void CloseChat()
    {
        _chatOpen = false;
        _chatCard.MaxWidth = 220;
        _chatCard.MinWidth = 140;
        _chatExpanded.Visibility = Visibility.Collapsed;
        _chatCollapsed.Visibility = Visibility.Visible;
        SetMood("happy");
    }

    private void AddMsg(string role, string text)
    {
        var msg = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10,6,10,6),
            MaxWidth = 240,
            Margin = new Thickness(0,3,0,0),
            HorizontalAlignment = role == "me" ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = role == "me"
                ? (Brush)FindResource("InkBrush")
                : new SolidColorBrush(Color.FromRgb(0xFF,0xE9,0xCC)),
            BorderBrush = role == "me"
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromArgb(0x2E,0x4A,0x2E,0x18)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text,
                Foreground = role == "me"
                    ? (Brush)FindResource("CreamBrush")
                    : (Brush)FindResource("InkBrush"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
            }
        };
        // asymmetric tail corner
        msg.CornerRadius = role == "me"
            ? new CornerRadius(12, 12, 4, 12)
            : new CornerRadius(12, 12, 12, 4);
        _chatLog.Children.Add(msg);
        Dispatcher.BeginInvoke(new Action(() => _chatLogScroll?.ScrollToEnd()),
            DispatcherPriority.Background);
    }

    private async System.Threading.Tasks.Task SendChatAsync()
    {
        var text = _chatInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        _chatInput.Text = "";
        _chatSend.IsEnabled = false;

        AddMsg("me", text);
        _history.Add(("user", text));

        var thinking = new TextBlock
        {
            Text = "想想…",
            FontStyle = FontStyles.Italic,
            Foreground = (Brush)FindResource("Ink3Brush"),
            FontSize = 12,
            Margin = new Thickness(4,3,0,0),
        };
        _chatLog.Children.Add(thinking);

        SetMood("excited");

        try
        {
            var reply = await _responder.RespondAsync(_history);
            _chatLog.Children.Remove(thinking);
            AddMsg("pet", reply);
            _history.Add(("assistant", reply));
        }
        catch
        {
            _chatLog.Children.Remove(thinking);
            AddMsg("pet", "呜… 我的电路打结了，再试一次？");
        }
        finally
        {
            _chatSend.IsEnabled = true;
            SetMood(_chatOpen ? "happy" : _mood);
            _chatInput.Focus();
        }
    }

    // ── Mood / expression ───────────────────────────────────────
    private void SetMood(string mood)
    {
        if (_sleeping) return;
        _mood = mood;
        switch (mood)
        {
            case "sleepy":
                AnimDouble(LeftEyeScale, ScaleTransform.ScaleYProperty, 0.18, 200);
                AnimDouble(RightEyeScale, ScaleTransform.ScaleYProperty, 0.18, 200);
                LeftBlush.Opacity = RightBlush.Opacity = 0.4;
                break;
            case "excited":
                RestartBlink(2.2);
                LeftBlush.Opacity = RightBlush.Opacity = 1;
                break;
            default: // happy
                RestartBlink(4.6);
                LeftBlush.Opacity = RightBlush.Opacity = 0.6;
                break;
        }
    }

    // ── Context menu actions ────────────────────────────────────
    private void Wake_Click(object sender, RoutedEventArgs e)   { WakeUp(); ResetIdleTimer(); }
    private void Sleep_Click(object sender, RoutedEventArgs e)  { GoToSleep(); }
    private void Expr_Happy(object sender, RoutedEventArgs e)   { WakeUp(); SetMood("happy"); ResetIdleTimer(); }
    private void Expr_Sleepy(object sender, RoutedEventArgs e)  { WakeUp(); SetMood("sleepy"); ResetIdleTimer(); }
    private void Expr_Excited(object sender, RoutedEventArgs e) { WakeUp(); SetMood("excited"); ResetIdleTimer(); }
    private void ToggleTodoFloat_Click(object sender, RoutedEventArgs e)
    {
        ActivateRequested?.Invoke(this, EventArgs.Empty);
        ResetIdleTimer();
    }

    // ── Tiny helpers ────────────────────────────────────────────
    private static void AnimDouble(DependencyObject target, DependencyProperty prop,
        double to, double durMs, double delayMs = 0, IEasingFunction? ease = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(durMs))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease,
        };
        // Both UIElement and Animatable have BeginAnimation(prop, anim)
        switch (target)
        {
            case UIElement ui: ui.BeginAnimation(prop, a); break;
            case Animatable an: an.BeginAnimation(prop, a); break;
        }
    }

    private static string ZhWeekday(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday    => "周一",
        DayOfWeek.Tuesday   => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday  => "周四",
        DayOfWeek.Friday    => "周五",
        DayOfWeek.Saturday  => "周六",
        _                   => "周日",
    };

    private static string ZhMonth(int m) => m switch
    {
        1 => "一月", 2 => "二月", 3 => "三月", 4 => "四月",
        5 => "五月", 6 => "六月", 7 => "七月", 8 => "八月",
        9 => "九月", 10 => "十月", 11 => "十一月", _ => "十二月",
    };

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

public sealed record DesktopPetTodoItem(string Title, bool Completed);
