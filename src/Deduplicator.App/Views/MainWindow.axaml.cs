using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Deduplicator.App.ViewModels;

namespace Deduplicator.App.Views;

public partial class MainWindow : Window
{
    private const double Pad = 8;     // отступ рамки от элемента
    private const double Edge = 16;   // минимальный отступ от края окна
    private const double Gap = 18;    // зазор между элементом и карточкой
    private const double Radius = 12; // скругление «окошка»
    private const double LargeTargetMaxHeight = 220;

    private MainWindowViewModel? _vm;
    private bool _queued;
    private Rect _lastRect = new(-1, -1, 0, 0);
    private readonly WindowNotificationManager _notifications;

    public MainWindow()
    {
        InitializeComponent();
        // Ненавязчивые тосты поверх окна (сторож дубликатов, подсказка «носитель с фотографиями»).
        _notifications = new WindowNotificationManager(this)
        {
            Position = NotificationPosition.TopRight,
            MaxItems = 3
        };
        DataContextChanged += (_, _) => Attach();
        SizeChanged += (_, _) => { _lastRect = new(-1, -1, 0, 0); Schedule(); };
        LayoutUpdated += (_, _) => Schedule();
#if DEBUG
        this.AttachDevTools();
#endif
    }

    /// <summary>
    /// Информационный тост в правом верхнем углу окна. <paramref name="onClick"/> — необязательное
    /// действие по клику (например, перейти на экран сверки). Вызывать на UI-потоке.
    /// </summary>
    public void ShowToast(string title, string message, Action? onClick = null)
    {
        try
        {
            _notifications.Show(new Notification(title, message, NotificationType.Information,
                TimeSpan.FromSeconds(8), onClick));
        }
        catch (Exception ex) { Core.Util.AppLog.Error("ShowToast", ex); }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Attach();
        Schedule();
        // Самопроверка: при DEDUP_SMOKE=1 прогоняем навигацию по всем экранам и закрываемся.
        if (Environment.GetEnvironmentVariable("DEDUP_SMOKE") == "1")
            _ = RunSmokeAsync();
    }

    private void Attach()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        _lastRect = new(-1, -1, 0, 0);
        Schedule();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsOnboardingVisible)
            or nameof(MainWindowViewModel.OnboardingIndex)
            or nameof(MainWindowViewModel.SelectedNav)
            or nameof(MainWindowViewModel.CurrentPage))
        {
            _lastRect = new(-1, -1, 0, 0); // новый шаг — форсируем пересчёт
            Schedule();
        }
    }

    private void Schedule()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() => { _queued = false; Place(); }, DispatcherPriority.Background);
    }

    /// <summary>Замеряет реальный подсвечиваемый контрол и точно расставляет затемнение, рамку и карточку.</summary>
    private void Place()
    {
        if (_vm is null || !_vm.IsOnboardingVisible) return;
        if (OnboardingMask is null || OnboardingBlocker is null || OnboardingHighlight is null || OnboardingCard is null) return;

        double w = Math.Max(Bounds.Width, MinWidth);
        double h = Math.Max(Bounds.Height, MinHeight);
        OnboardingBlocker.Width = w;
        OnboardingBlocker.Height = h;

        var rect = MeasureTarget(_vm.OnboardingTargetName, w, h);

        // Без мерцания: если ничего не изменилось (частые LayoutUpdated) — выходим.
        var key = rect ?? new Rect(-2, -2, 0, 0);
        if (key == _lastRect) return;
        _lastRect = key;

        var full = new RectangleGeometry(new Rect(0, 0, w, h));
        var cardSize = new Size(
            OnboardingCard.Bounds.Width > 1 ? OnboardingCard.Bounds.Width : 400,
            OnboardingCard.Bounds.Height > 1 ? OnboardingCard.Bounds.Height : 280);

        if (rect is { } r)
        {
            OnboardingMask.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, RoundedRect(r, Radius));
            OnboardingHighlight.IsVisible = true;
            Canvas.SetLeft(OnboardingHighlight, r.X);
            Canvas.SetTop(OnboardingHighlight, r.Y);
            OnboardingHighlight.Width = r.Width;
            OnboardingHighlight.Height = r.Height;

            var cp = PlaceCard(r, cardSize, w, h);
            Canvas.SetLeft(OnboardingCard, cp.X);
            Canvas.SetTop(OnboardingCard, cp.Y);
        }
        else
        {
            // Цель ещё не отрисована (или её нет) — сплошное затемнение, карточка по центру.
            OnboardingMask.Data = full;
            OnboardingHighlight.IsVisible = false;
            Canvas.SetLeft(OnboardingCard, Math.Round((w - cardSize.Width) / 2));
            Canvas.SetTop(OnboardingCard, Math.Round(Math.Max(Edge, (h - cardSize.Height) / 2)));
        }
    }

    private Rect? MeasureTarget(string name, double w, double h)
    {
        if (string.IsNullOrEmpty(name)) return null;

        var target = this.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c.Name == name && c.IsVisible && c.Bounds.Width > 1 && c.Bounds.Height > 1);
        if (target is null) return null;

        var topLeft = target.TranslatePoint(new Point(0, 0), this);
        if (topLeft is not { } p) return null;

        double x = Math.Max(Edge, p.X - Pad);
        double y = Math.Max(Edge, p.Y - Pad);
        double right = Math.Min(w - Edge, p.X + target.Bounds.Width + Pad);
        double bottom = Math.Min(h - Edge, p.Y + target.Bounds.Height + Pad);
        if (right - x < 8 || bottom - y < 8) return null;

        var rect = new Rect(Math.Round(x), Math.Round(y), Math.Round(right - x), Math.Round(bottom - y));
        return NormalizeTarget(name, rect);
    }

    private static Point PlaceCard(Rect target, Size card, double w, double h)
    {
        double cw = Math.Min(card.Width, w - Edge * 2);
        double ch = Math.Min(card.Height, h - Edge * 2);

        bool wideTarget = target.Width > cw + Gap * 2;

        if (!wideTarget && target.Right + Gap + cw <= w - Edge) // справа от кнопки
            return Clamp(target.Right + Gap, target.Y, cw, ch, w, h);
        if (!wideTarget && target.X - Gap - cw >= Edge)         // слева от кнопки
            return Clamp(target.X - Gap - cw, target.Y, cw, ch, w, h);
        if (target.Bottom + Gap + ch <= h - Edge)               // под целью
            return Clamp(target.X, target.Bottom + Gap, cw, ch, w, h);
        if (target.Y - Gap - ch >= Edge)                        // над целью
            return Clamp(target.X, target.Y - Gap - ch, cw, ch, w, h);
        if (target.Right + Gap + cw <= w - Edge)                // справа
            return Clamp(target.Right + Gap, target.Y, cw, ch, w, h);
        if (target.X - Gap - cw >= Edge)                        // слева
            return Clamp(target.X - Gap - cw, target.Y, cw, ch, w, h);

        return Clamp(w - Edge - cw, target.Center.Y - ch / 2, cw, ch, w, h); // прижать к краю
    }

    private static Rect NormalizeTarget(string name, Rect rect)
    {
        if (name == "OnboardingMainNav" || rect.Height <= LargeTargetMaxHeight)
            return rect;

        return new Rect(rect.X, rect.Y, rect.Width, LargeTargetMaxHeight);
    }

    private static Point Clamp(double x, double y, double cw, double ch, double w, double h)
        => new(Math.Round(Math.Clamp(x, Edge, Math.Max(Edge, w - Edge - cw))),
               Math.Round(Math.Clamp(y, Edge, Math.Max(Edge, h - Edge - ch))));

    private static Geometry RoundedRect(Rect rect, double radius)
    {
        double r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(new Point(rect.X + r, rect.Y), true);
        c.LineTo(new Point(rect.Right - r, rect.Y));
        c.ArcTo(new Point(rect.Right, rect.Y + r), new Size(r, r), 0, false, SweepDirection.Clockwise);
        c.LineTo(new Point(rect.Right, rect.Bottom - r));
        c.ArcTo(new Point(rect.Right - r, rect.Bottom), new Size(r, r), 0, false, SweepDirection.Clockwise);
        c.LineTo(new Point(rect.X + r, rect.Bottom));
        c.ArcTo(new Point(rect.X, rect.Bottom - r), new Size(r, r), 0, false, SweepDirection.Clockwise);
        c.LineTo(new Point(rect.X, rect.Y + r));
        c.ArcTo(new Point(rect.X + r, rect.Y), new Size(r, r), 0, false, SweepDirection.Clockwise);
        c.EndFigure(true);
        return g;
    }

    private async Task RunSmokeAsync()
    {
        try
        {
            if (DataContext is MainWindowViewModel vm)
            {
                foreach (var item in vm.NavItems)
                {
                    vm.SelectedNav = item;
                    await Task.Delay(300);
                }
            }
        }
        finally
        {
            Close();
        }
    }
}
