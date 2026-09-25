using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Interop;
using System.Windows.Shapes;
using WinForms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using StackPanel = System.Windows.Controls.StackPanel;
using MessageBox = System.Windows.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QuickAccess;

public partial class MainWindow : Window
{
    private List<GroupEntry> _groups = new();
    private HotKeyManager? _hotkeys;
    private WinForms.NotifyIcon? _tray;
    private int _hoverGroup = -1;
    private AppSettings _settings = new();
    private readonly Dictionary<int, Button> _addButtons = new();
    private bool _dragging;
    private bool _suppressClick;
    private bool _isEditMode = false;
    private readonly Dictionary<(int, int), Button> _shortcutButtons = new();
    private readonly Dictionary<(int, int), (Line glow, Line core)> _shortcutLines = new();
    private readonly Dictionary<int, (Line glow, Line core)> _groupLines = new();
    private int _dragGi = -1;
    private int _dragSi = -1;
    private Point _dragPress;
    private double _dragStartL, _dragStartT, _dragExL, _dragExT;
    private Button? _dragExBtn;
    private Action<double, double>? _dragCommit;
    private Func<Button?>? _dragExtraFn;
    private Action<Button>? _dragOnStart;
    private Action<double, double>? _dragOnDelta;
    private readonly List<SpringNode> _springs = new();
    private Button? _dragBtn;
    private double _dragStartCX, _dragStartCY;
    private bool _springRunning;
    private DateTime _lastFrame;
    private TextBlock? _clockTime;
    private TextBlock? _clockDate;
    private System.Windows.Threading.DispatcherTimer? _clockTimer;
    private int _bakeSeq;
    private readonly bool _startInTray;

    private sealed class SpringNode
    {
        public int Gi;
        public int Si;
        public Button Btn = null!;
        public Line Glow = null!;
        public Line Core = null!;
        public double X, Y, VX, VY, SX, SY, TX, TY;
    }

    private Color LineColor => ThemeColors.Parse(_settings.LineColorHex, Colors.White);
    private Color AccentColor => ThemeColors.Parse(_settings.AccentColorHex, Color.FromRgb(0xFF, 0x8C, 0x1A));
    private Color BgColor => ThemeColors.Parse(_settings.BackgroundColorHex, Color.FromArgb(0xDC, 0x12, 0x12, 0x15));

    public MainWindow()
    {
        RenderOptions.ProcessRenderMode = RenderMode.Default;
        InitializeComponent();
        RenderOptions.SetCachingHint(BottomBar, CachingHint.Cache);
        WebCanvas.PreviewMouseMove += CanvasDragMove;
        WebCanvas.PreviewMouseLeftButtonUp += CanvasDragUp;
        _startInTray = Environment.GetCommandLineArgs().Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        _groups = ConfigService.Load();
        _settings = ConfigService.LoadSettings();
        SourceInitialized += (_, _) => BlurHelper.EnableBlur(this);
        _clockTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        Loaded += (_, _) =>
        {
            DebugLog.Write($"Loaded, args={string.Join(" ", Environment.GetCommandLineArgs())}");
            Loc.SetLanguage(_settings.Language);
            RegisterHotkeys();
            try { InitTray(); } catch (Exception ex) { DebugLog.Write("Tray ex: " + ex); }
            ApplyLanguage();
            DebugLog.Write("StartInTray=" + _startInTray);
            if (_startInTray) HideWeb();
            else ShowWeb();
        };
        SizeChanged += (_, _) => Render(false);
        Closed += (_, _) => { _hotkeys?.Dispose(); _tray?.Dispose(); };
    }

    private void InitTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Visible = true,
            Text = $"QuickAccess — {_settings.HotKeyMain}",
            Icon = System.Drawing.SystemIcons.Application
        };
        _tray.DoubleClick += (_, _) => ShowWeb();
        _tray.ContextMenuStrip = BuildTrayMenu();
        _tray.BalloonTipTitle = Loc.T("tray.balloonT");
        _tray.BalloonTipText = Loc.T("tray.balloon", ComboText());
        _tray.ShowBalloonTip(2000);
    }

    private WinForms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(Loc.T("tray.show", ComboText()), null, (_, _) => ShowWeb());
        menu.Items.Add(Loc.T("tray.diag"), null, (_, _) => MessageBox.Show(DebugLog.ReadTail() + $"\n\n{Loc.T("set.hotkeys")}: {_hotkeys?.LastError}", Loc.T("tray.diagT")));
        var auto = new WinForms.ToolStripMenuItem(Loc.T("tray.auto")) { Checked = AutostartHelper.IsEnabled() };
        auto.Click += (_, _) =>
        {
            AutostartHelper.SetEnabled(!AutostartHelper.IsEnabled());
            auto.Checked = AutostartHelper.IsEnabled();
            UpdateAutostartBtn();
        };
        menu.Items.Add(auto);
        menu.Items.Add(Loc.T("tray.exit"), null, (_, _) => { if (_tray != null) _tray.Visible = false; System.Windows.Application.Current.Shutdown(); });
        return menu;
    }

    private void RebuildTray()
    {
        if (_tray == null) return;
        var old = _tray.ContextMenuStrip;
        _tray.Text = $"QuickAccess — {_settings.HotKeyMain}";
        _tray.ContextMenuStrip = BuildTrayMenu();
        old?.Dispose();
    }

    private System.Globalization.CultureInfo _lc = System.Globalization.CultureInfo.GetCultureInfo("en-US");

    private void RegisterHotkeys()
    {
        try
        {
            _hotkeys?.Dispose();
            _hotkeys = new HotKeyManager(this);
            _hotkeys.HotKeyPressed += OnHotKey;
            var slots = new List<HotKeyManager.HotkeySlot>();
            if (HotKeyManager.TryParse(_settings.HotKeyMain, out var m1, out var v1))
                slots.Add(new HotKeyManager.HotkeySlot(HotKeyManager.ID_MAIN, m1, v1));
            if (HotKeyManager.TryParse(_settings.HotKeySecondary, out var m2, out var v2) &&
                (m2 != m1 || v2 != v1))
                slots.Add(new HotKeyManager.HotkeySlot(HotKeyManager.ID_SECONDARY, m2, v2));
            bool ok = _hotkeys.Init(slots);
            if (!ok)
            {
                MessageBox.Show(Loc.T("msg.hotkeyFail", _hotkeys.LastError),
                    Loc.T("msg.hotkeyT"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex) { DebugLog.Write("HotKey init ex: " + ex); }
    }

    private string ComboText()
    {
        if (string.IsNullOrWhiteSpace(_settings.HotKeySecondary)) return _settings.HotKeyMain;
        if (_settings.HotKeySecondary == _settings.HotKeyMain) return _settings.HotKeyMain;
        return $"{_settings.HotKeyMain} / {_settings.HotKeySecondary}";
    }

    private void ApplyLanguage()
    {
        Loc.SetLanguage(_settings.Language);
        _lc = Loc.Culture;
        if (AddGroupBtn != null) AddGroupBtn.Content = Loc.T("bar.add");
        if (HideBtn != null) HideBtn.Content = Loc.T("bar.hide");
        if (SettingsBtn != null) SettingsBtn.Content = Loc.T("bar.settings");
        if (ModulesBtn != null) ModulesBtn.Content = Loc.T("bar.modules");
        if (EditModeBtn != null) EditModeBtn.Content = Loc.T("bar.edit");
        if (FinishEditBtn != null) FinishEditBtn.Content = Loc.T("bar.finish");
        UpdateAutostartBtn();
        UpdateHint();
        RebuildTray();
        UpdateClock();
    }

    private void UpdateHint()
    {
        if (HintText != null) HintText.Text = _isEditMode ? Loc.T("bar.hintEdit") : Loc.T("bar.hint", ComboText());
    }

    private void UpdateAutostartBtn()
    {
        if (AutostartBtn != null)
            AutostartBtn.Content = AutostartHelper.IsEnabled() ? Loc.T("bar.autoOn") : Loc.T("bar.autoOff");
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        AutostartHelper.SetEnabled(!AutostartHelper.IsEnabled());
        UpdateAutostartBtn();
    }

    private void OnHotKey(int id)
    {
        // Хук HwndSource уже работает в UI-потоке — Invoke здесь вызывал deadlock, поэтому напрямую.
        DebugLog.Write($"Toggle via hotkey id={id}, visible={Visibility}");
        try
        {
            if (Visibility == Visibility.Visible) HideWeb();
            else ShowWeb();
        }
        catch (Exception ex) { DebugLog.Write("Toggle ex: " + ex); }
    }

    private void ShowWeb()
    {
        DebugLog.Write("ShowWeb");
        Show();
        Activate();
        Render(true);
        ScheduleBake();
    }

    private void UpdateClock()
    {
        if (_clockTime == null || _clockDate == null) return;
        if (Visibility != Visibility.Visible) return;
        var now = DateTime.Now;
        _clockTime.Text = now.ToString("HH:mm");
        _clockDate.Text = now.ToString("d MMM", _lc);
    }

    private void HideWeb()
    {
        DebugLog.Write("HideWeb");
        StopSpring();
        Hide();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) HideWeb();
        // Локальный запасной вариант: Alt+E скрывает/показывает, работает когда окно в фокусе
        if (e.Key == Key.E && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            HideWeb();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) HideWeb();
    }

    private void WebCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.RightButton == MouseButtonState.Pressed) AddGroup();
        else if (e.ChangedButton == MouseButton.Left)
        {
            // клик по пустому месту — скрыть (панель быстрого доступа)
            if (e.OriginalSource == WebCanvas) HideWeb();
        }
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => HideWeb();
    private void AddGroup_Click(object sender, RoutedEventArgs e) => AddGroup();

    private void EditMode_Click(object sender, RoutedEventArgs e) => SetEditMode(true);
    private void FinishEdit_Click(object sender, RoutedEventArgs e) => SetEditMode(false);

    private void SetEditMode(bool on)
    {
        ResetDrag();
        _isEditMode = on;
        if (EditModeBtn != null) EditModeBtn.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (FinishEditBtn != null) FinishEditBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (HintText != null) UpdateHint();
        if (!on)
        {
            SaveAllPositions();
            ConfigService.Save(_groups);
        }
        Render(false);
    }

    private void SaveAllPositions()
    {
        foreach (var child in WebCanvas.Children)
        {
            if (child is not Button b) continue;
            if (double.IsNaN(Canvas.GetLeft(b)) || double.IsNaN(Canvas.GetTop(b))) continue;
            double cx = Canvas.GetLeft(b) + b.Width / 2;
            double cy = Canvas.GetTop(b) + b.Height / 2;
            if (b.Tag is int gi && gi >= 0 && gi < _groups.Count)
            {
                _groups[gi].X = Snap(cx);
                _groups[gi].Y = Snap(cy);
            }
            else if (b.Tag is ValueTuple<int, int> t &&
                     t.Item1 >= 0 && t.Item1 < _groups.Count &&
                     t.Item2 >= 0 && t.Item2 < _groups[t.Item1].Shortcuts.Count)
            {
                _groups[t.Item1].Shortcuts[t.Item2].X = Snap(cx);
                _groups[t.Item1].Shortcuts[t.Item2].Y = Snap(cy);
            }
        }
    }

    private void AddGroup()
    {
        var name = Prompt(Loc.T("prompt.newGroupT"), Loc.T("prompt.newGroupL"));
        if (string.IsNullOrWhiteSpace(name)) return;
        _groups.Add(new GroupEntry { Name = name.Trim() });
        ConfigService.Save(_groups);
        Render(false);
    }

    private void Render(bool animateOpening)
    {
        if (WebCanvas == null) return;
        if (!IsLoaded) return;
        WebCanvas.Children.Clear();
        _addButtons.Clear();
        _shortcutButtons.Clear();
        _shortcutLines.Clear();
        _groupLines.Clear();
        if (_dragging) { _dragging = false; _suppressClick = false; StopSpring(); }
        double cx = ActualWidth / 2;
        double cy = ActualHeight / 2 - 20;
        if (cx < 10) { cx = 500; cy = 330; }
        ++_bakeSeq;
        var bgBrush = Frozen(Dense(BgColor));
        var lineDimBrush = Frozen(ThemeColors.WithAlpha(LineColor, 0x66));
        var accentBrush = Frozen(AccentColor);
        BottomBar.Background = bgBrush;
        BottomBar.BorderBrush = lineDimBrush;

        var groupStyle = TryFindResource("WebGroupStyle") as Style;
        var shortcutStyle = TryFindResource("WebShortcutStyle") as Style;
        var deleteStyle = TryFindResource("DeleteStyle") as Style;

        var backdrop = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(ActualWidth, 1),
            Height = Math.Max(ActualHeight, 1),
            Fill = Frozen(Color.FromArgb(0x40, 0x00, 0x00, 0x00)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(backdrop, 0);
        Canvas.SetTop(backdrop, 0);
        Canvas.SetZIndex(backdrop, -10);
        WebCanvas.Children.Add(backdrop);

        // Гало: строгое бело-серое свечение, без цветных акцентов
        var halo = new Ellipse
        {
            Width = 170, Height = 170,
            Fill = Frozen(ThemeColors.WithAlpha(LineColor, 36)),
            Effect = FrozenFx(new BlurEffect { Radius = 36 }),
            IsHitTestVisible = false,
            Opacity = animateOpening ? 0 : 1
        };
        Canvas.SetLeft(halo, cx - 85);
        Canvas.SetTop(halo, cy - 85);
        Canvas.SetZIndex(halo, 20);
        WebCanvas.Children.Add(halo);

        var center = new Ellipse
        {
            Width = 96, Height = 96,
            Fill = bgBrush,
            Stroke = Frozen(ThemeColors.WithAlpha(AccentColor, 0xB0)),
            StrokeThickness = 1.5,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Effect = FrozenFx(new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 0, Opacity = 0.65 }),
            Opacity = animateOpening ? 0 : 1
        };
        Canvas.SetLeft(center, cx - 48);
        Canvas.SetTop(center, cy - 48);
        Canvas.SetZIndex(center, 20);
        WebCanvas.Children.Add(center);

        FrameworkElement centerContent;
        if (ModuleRegistry.IsEnabled(_settings, "clock"))
        {
            var clockPanel = new StackPanel
            {
                Width = 96, Height = 96,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                IsHitTestVisible = false,
                Opacity = animateOpening ? 0 : 1
            };
            _clockTime = new TextBlock
            {
                Text = DateTime.Now.ToString("HH:mm"), FontSize = 25, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 8, ShadowDepth = 1, Opacity = 0.7 }
            };
            _clockDate = new TextBlock
            {
                Text = DateTime.Now.ToString("d MMM", _lc), FontSize = 11,
                Foreground = Frozen(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                TextAlignment = TextAlignment.Center
            };
            clockPanel.Children.Add(_clockTime);
            clockPanel.Children.Add(_clockDate);
            Canvas.SetLeft(clockPanel, cx - 48);
            Canvas.SetTop(clockPanel, cy - 48);
            Canvas.SetZIndex(clockPanel, 20);
            WebCanvas.Children.Add(clockPanel);
            centerContent = clockPanel;
        }
        else
        {
            _clockTime = null;
            _clockDate = null;
            var logo = new TextBlock
            {
                Text = "⊞", FontSize = 44, Foreground = Brushes.White,
                Width = 96, Height = 96, TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 8, ShadowDepth = 1, Opacity = 0.7 },
                Opacity = animateOpening ? 0 : 1
            };
            Canvas.SetLeft(logo, cx - 48);
            Canvas.SetTop(logo, cy - 38);
            Canvas.SetZIndex(logo, 20);
            WebCanvas.Children.Add(logo);
            centerContent = logo;
        }

        if (animateOpening)
        {
            AnimateScale(center, 0.5, 1.0, 300, 0);
            center.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0, 1, 240, 0));
            halo.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0, 1, 320, 60));
            centerContent.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0, 1, 260, 120));
        }

        int n = _groups.Count;
        double r1 = 230;
        for (int i = 0; i < n; i++)
        {
            double ang = n == 1 ? -Math.PI / 2 : 2 * Math.PI * i / n - Math.PI / 2;
            bool hasPos = _groups[i].X != 0 || _groups[i].Y != 0;
            double gx = hasPos ? _groups[i].X : cx + r1 * Math.Cos(ang);
            double gy = hasPos ? _groups[i].Y : cy + r1 * Math.Sin(ang);
            bool active = i == _hoverGroup;
            int delay = animateOpening ? 70 + i * 55 : 0;

            var groupLines = AddWebLine(cx, cy, gx, gy, active, animateOpening, delay);
            _groupLines[i] = groupLines;

            var btn = new Button
            {
                Content = BuildButtonContent(_groups[i].IconGlyph, _groups[i].Name, 16),
                Width = 140, Height = 54,
                Tag = i,
                Cursor = _isEditMode ? Cursors.SizeAll : Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Opacity = animateOpening ? 0 : 1,
                Background = bgBrush,
                BorderBrush = active ? accentBrush : lineDimBrush
            };
            if (groupStyle != null) btn.Style = groupStyle;
            btn.MouseEnter += Group_HoverEnter;
            btn.MouseLeave += HoverLeave;
            btn.Click += Group_Click;
            btn.ContextMenu = BuildGroupMenu(i);
            if (animateOpening)
                AnimateFlyOut(btn, cx - 70, cy - 27, gx - 70, gy - 27, 0.6, delay);
            else
            {
                Canvas.SetLeft(btn, gx - 70);
                Canvas.SetTop(btn, gy - 27);
                if (active) btn.RenderTransform = new ScaleTransform(1.07, 1.07);
            }
            Canvas.SetZIndex(btn, 10);
            WebCanvas.Children.Add(btn);
            if (active && deleteStyle != null)
            {
                var dg = new Button { Content = "✕", Width = 26, Height = 26, Tag = ("del", i), Cursor = Cursors.Hand, ToolTip = Loc.T("tip.delGroup") };
                dg.Style = deleteStyle;
                int dgi = i;
                dg.Click += (s, e) => AskDeleteGroup(dgi);
                Canvas.SetLeft(dg, gx + 70 + 6);
                Canvas.SetTop(dg, gy - 27 - 32);
                Canvas.SetZIndex(dg, 10);
                WebCanvas.Children.Add(dg);
            }
            int cgi = i;
            AttachDrag(btn, groupLines, cgi, -1,
                (nx, ny) =>
                {
                    double snx = Snap(nx), sny = Snap(ny);
                    CommitSpringFollow(cgi, snx, sny);
                    _groups[cgi].X = snx;
                    _groups[cgi].Y = sny;
                    ConfigService.Save(_groups);
                    Render(false);
                },
                () => _addButtons.TryGetValue(cgi, out var ab) ? ab : null,
                (b) => { if (ModuleRegistry.IsEnabled(_settings, "spring")) StartSpringFollow(cgi, b); },
                (dx, dy) => UpdateSpringTargets(dx, dy));

            if (active)
            {
                var sc = _groups[i].Shortcuts;
                double r2 = 175;
                for (int j = 0; j < sc.Count; j++)
                {
                    double a2 = sc.Count == 1 ? ang : ang - 0.75 + 1.5 * j / Math.Max(1, sc.Count - 1);
                    bool scHasPos = sc[j].X != 0 || sc[j].Y != 0;
                    double sx = scHasPos ? sc[j].X : gx + r2 * Math.Cos(a2);
                    double sy = scHasPos ? sc[j].Y : gy + r2 * Math.Sin(a2);
                    int sdelay = animateOpening ? delay + 120 + j * 45 : 0;
                    var shortcutLines = AddWebLine(gx, gy, sx, sy, true, animateOpening, sdelay);
                    var sb = new Button
                    {
                        Content = BuildButtonContent(sc[j].IconGlyph, sc[j].Name, 14),
                        Width = 140, Height = 48,
                        Tag = (i, j), Cursor = _isEditMode ? Cursors.SizeAll : Cursors.Hand,
                        RenderTransformOrigin = new Point(0.5, 0.5),
                        ContextMenu = BuildShortcutMenu(i, j),
                        Opacity = animateOpening ? 0 : 1,
                        Background = bgBrush,
                        BorderBrush = lineDimBrush
                    };
                    if (shortcutStyle != null) sb.Style = shortcutStyle;
                    sb.MouseEnter += HoverEnter;
                    sb.MouseLeave += HoverLeave;
                    sb.Click += Shortcut_Click;
                    if (animateOpening)
                        AnimateFlyOut(sb, gx - 70, gy - 24, sx - 70, sy - 24, 0.6, sdelay);
                    else
                    {
                        Canvas.SetLeft(sb, sx - 70);
                        Canvas.SetTop(sb, sy - 24);
                    }
                    Canvas.SetZIndex(sb, 10);
                    WebCanvas.Children.Add(sb);
                    if (deleteStyle != null)
                    {
                        var ds = new Button { Content = "✕", Width = 24, Height = 24, Tag = ("delsc", i, j), Cursor = Cursors.Hand, ToolTip = Loc.T("tip.delShortcut") };
                        ds.Style = deleteStyle;
                        int dGi = i, dSi = j;
                        ds.Click += (s, e) => AskDeleteShortcut(dGi, dSi);
                        Canvas.SetLeft(ds, sx + 70 + 4);
                        Canvas.SetTop(ds, sy - 24 - 30);
                        Canvas.SetZIndex(ds, 10);
                        WebCanvas.Children.Add(ds);
                    }
                    _shortcutButtons[(i, j)] = sb;
                    _shortcutLines[(i, j)] = shortcutLines;
                    int sGi = i, sSi = j;
                    AttachDrag(sb, shortcutLines, sGi, sSi,
                        (nx, ny) =>
                        {
                            _groups[sGi].Shortcuts[sSi].X = Snap(nx);
                            _groups[sGi].Shortcuts[sSi].Y = Snap(ny);
                            ConfigService.Save(_groups);
                            Render(false);
                        });
                }
                var addBtn = new Button { Content = "+ ярлык", Width = 100, Height = 32, Tag = ("add", i), Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5), Background = bgBrush, BorderBrush = lineDimBrush };
                if (shortcutStyle != null) addBtn.Style = shortcutStyle;
                addBtn.Click += (s, e) => AddShortcut(((ValueTuple<string, int>)((Button)s).Tag).Item2);
                Canvas.SetLeft(addBtn, gx - 50);
                Canvas.SetTop(addBtn, gy + 48);
                Canvas.SetZIndex(addBtn, 10);
                WebCanvas.Children.Add(addBtn);
                _addButtons[i] = addBtn;
            }
        }
    }

    private (Line glow, Line core) AddWebLine(double x1, double y1, double x2, double y2, bool active, bool animate, int delayMs)
    {
        double targetOpacity = active ? 1.0 : 0.6;
        double thickness = _settings.LineThickness;

        var gradient = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };
        gradient.GradientStops.Add(new System.Windows.Media.GradientStop(ThemeColors.WithAlpha(LineColor, active ? (byte)0xFF : (byte)0xD9), 0));
        gradient.GradientStops.Add(new System.Windows.Media.GradientStop(ThemeColors.WithAlpha(LineColor, active ? (byte)0xE6 : (byte)0x66), 1));
        gradient.Freeze();

        var glow = new Line
        {
            X1 = x1, Y1 = y1, X2 = animate ? x1 : x2, Y2 = animate ? y1 : y2,
            Stroke = Frozen(ThemeColors.WithAlpha(LineColor, active ? (byte)70 : (byte)36)),
            StrokeThickness = thickness + 5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Effect = FrozenFx(new BlurEffect { Radius = 6 }),
            IsHitTestVisible = false,
            Opacity = animate ? 0 : 1
        };
        var core = new Line
        {
            X1 = x1, Y1 = y1, X2 = animate ? x1 : x2, Y2 = animate ? y1 : y2,
            Stroke = gradient,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Effect = FrozenFx(new DropShadowEffect { Color = LineColor, BlurRadius = 8, ShadowDepth = 0, Opacity = active ? 0.55 : 0.28 }),
            IsHitTestVisible = false,
            Opacity = animate ? 0 : targetOpacity
        };
        Canvas.SetZIndex(glow, 0);
        Canvas.SetZIndex(core, 0);
        WebCanvas.Children.Add(glow);
        WebCanvas.Children.Add(core);

        if (animate)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            foreach (var line in new[] { glow, core })
            {
                line.BeginAnimation(Line.X2Property, MakeAnim(x1, x2, 340, delayMs, ease));
                line.BeginAnimation(Line.Y2Property, MakeAnim(y1, y2, 340, delayMs, ease));
            }
            glow.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0, 1, 260, delayMs));
            core.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0, targetOpacity, 260, delayMs));
        }
        return (glow, core);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Dense(Color c) =>
        c.A < 0xEE ? Color.FromArgb(0xEE, c.R, c.G, c.B) : c;

    private static T FrozenFx<T>(T fx) where T : Freezable
    {
        fx.Freeze();
        return fx;
    }

    private static DoubleAnimation MakeAnim(double from, double to, int ms, int delayMs, IEasingFunction? ease = null)
    {
        var a = new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(ms)))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(a, 60);
        return a;
    }

    private static void BakeProp(UIElement d, DependencyProperty p)
    {
        object cur = d.GetValue(p);
        d.BeginAnimation(p, null);
        d.SetValue(p, cur);
    }

    private void BakeVisuals()
    {
        foreach (var child in WebCanvas.Children)
        {
            switch (child)
            {
                case Button b:
                    BakeProp(b, Canvas.LeftProperty);
                    BakeProp(b, Canvas.TopProperty);
                    BakeProp(b, UIElement.OpacityProperty);
                    break;
                case Line l:
                    BakeProp(l, Line.X2Property);
                    BakeProp(l, Line.Y2Property);
                    BakeProp(l, UIElement.OpacityProperty);
                    break;
                case Ellipse el:
                    BakeProp(el, UIElement.OpacityProperty);
                    break;
                case StackPanel:
                case TextBlock:
                    BakeProp((UIElement)child, UIElement.OpacityProperty);
                    break;
            }
        }
    }

    private async void ScheduleBake()
    {
        int s = _bakeSeq;
        await Task.Delay(2500);
        if (s == _bakeSeq && IsLoaded && Visibility == Visibility.Visible) BakeVisuals();
    }

    private static void AnimateScale(FrameworkElement el, double from, double to, int ms, int delayMs)
    {
        var st = new ScaleTransform(from, from);
        el.RenderTransform = st;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.BeginAnimation(UIElement.OpacityProperty, MakeAnim(el.Opacity, 1, ms, delayMs));
        st.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(from, to, ms, delayMs));
        st.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(from, to, ms, delayMs));
    }

    private static void AnimateFlyOut(FrameworkElement el, double fromX, double fromY, double toX, double toY, double fromScale, int delayMs)
    {
        Canvas.SetLeft(el, fromX);
        Canvas.SetTop(el, fromY);
        el.Opacity = 0;
        var st = new ScaleTransform(fromScale, fromScale);
        el.RenderTransform = st;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        el.BeginAnimation(Canvas.LeftProperty, MakeAnim(fromX, toX, 340, delayMs, ease));
        el.BeginAnimation(Canvas.TopProperty, MakeAnim(fromY, toY, 340, delayMs, ease));
        el.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0, 1, 280, delayMs, ease));
        st.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(fromScale, 1.0, 340, delayMs, ease));
        st.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(fromScale, 1.0, 340, delayMs, ease));
    }

    private void Group_HoverEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragging) return;
        if (sender is not Button b || b.Tag is not int idx) return;
        if (_hoverGroup != idx)
        {
            _hoverGroup = idx;
            Render(false);
            var fresh = FindGroupButton(idx);
            if (fresh != null) AnimateHover(fresh, true);
            AnimateShortcutEntrance(idx);
        }
        else AnimateHover(b, true);
    }

    private void HoverEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement el) AnimateHover(el, true);
    }

    private void HoverLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement el) AnimateHover(el, false);
    }

    private static void AnimateHover(FrameworkElement el, bool enter)
    {
        var st = el.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        el.RenderTransform = st;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        double from = st.ScaleX;
        double to = enter ? 1.07 : 1.0;
        var ease = new SineEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(130));
        var ax = new DoubleAnimation(from, to, dur) { EasingFunction = ease };
        var ay = new DoubleAnimation(from, to, dur) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(ax, 60);
        Timeline.SetDesiredFrameRate(ay, 60);
        st.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, ay);
    }

    private void AttachDrag(Button btn, (Line glow, Line core) lines, int gi, int si, Action<double, double> commit, Func<Button?>? extra = null, Action<Button>? onDragStart = null, Action<double, double>? onDragDelta = null)
    {
        btn.PreviewMouseLeftButtonDown += (s, e) =>
        {
            _suppressClick = false;
            ResetDrag();
            if (!_isEditMode) return;
            _dragGi = gi;
            _dragSi = si;
            _dragPress = e.GetPosition(WebCanvas);
            _dragging = false;
            _dragCommit = commit;
            _dragExtraFn = extra;
            _dragOnStart = onDragStart;
            _dragOnDelta = onDragDelta;
            _dragExBtn = null;
            btn.CaptureMouse();
        };
    }

    private Button? ResolveDragButton()
    {
        if (_dragGi < 0) return null;
        if (_dragSi < 0) return FindGroupButton(_dragGi);
        return _shortcutButtons.TryGetValue((_dragGi, _dragSi), out var sb) ? sb : null;
    }

    private (Line glow, Line core)? ResolveDragLines()
    {
        if (_dragGi < 0) return null;
        if (_dragSi < 0) return _groupLines.TryGetValue(_dragGi, out var gl) ? gl : null;
        return _shortcutLines.TryGetValue((_dragGi, _dragSi), out var sl) ? sl : null;
    }

    private void ResetDrag()
    {
        _dragGi = -1;
        _dragSi = -1;
        _dragging = false;
        _dragCommit = null;
        _dragExtraFn = null;
        _dragOnStart = null;
        _dragOnDelta = null;
        _dragExBtn = null;
    }

    private void CanvasDragMove(object sender, MouseEventArgs e)
    {
        if (_dragGi < 0 || !_isEditMode) return;
        var btn = ResolveDragButton();
        var lines = ResolveDragLines();
        if (btn == null || lines == null) return;
        var p = e.GetPosition(WebCanvas);
        double dx = p.X - _dragPress.X, dy = p.Y - _dragPress.Y;
        if (!_dragging && Math.Abs(dx) < 5 && Math.Abs(dy) < 5) return;
        if (!_dragging)
        {
            _dragging = true;
            _dragStartL = Canvas.GetLeft(btn) - dx;
            _dragStartT = Canvas.GetTop(btn) - dy;
            BakeProp(btn, Canvas.LeftProperty);
            BakeProp(btn, Canvas.TopProperty);
            BakeProp(lines.Value.glow, Line.X2Property);
            BakeProp(lines.Value.glow, Line.Y2Property);
            BakeProp(lines.Value.core, Line.X2Property);
            BakeProp(lines.Value.core, Line.Y2Property);
            _dragExBtn = _dragExtraFn?.Invoke();
            if (_dragExBtn != null)
            {
                _dragExL = Canvas.GetLeft(_dragExBtn) - dx;
                _dragExT = Canvas.GetTop(_dragExBtn) - dy;
            }
            _dragOnStart?.Invoke(btn);
        }
        double nl = _dragStartL + dx, nt = _dragStartT + dy;
        Canvas.SetLeft(btn, nl);
        Canvas.SetTop(btn, nt);
        double ncx = nl + btn.Width / 2, ncy = nt + btn.Height / 2;
        lines.Value.glow.X2 = ncx;
        lines.Value.glow.Y2 = ncy;
        lines.Value.core.X2 = ncx;
        lines.Value.core.Y2 = ncy;
        if (_dragExBtn != null)
        {
            Canvas.SetLeft(_dragExBtn, _dragExL + dx);
            Canvas.SetTop(_dragExBtn, _dragExT + dy);
        }
        _dragOnDelta?.Invoke(dx, dy);
        e.Handled = true;
    }

    private void CanvasDragUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragGi < 0) return;
        var btn = ResolveDragButton();
        var commit = _dragCommit;
        bool wasDragging = _dragging;
        bool edit = _isEditMode;
        ResetDrag();
        if (!wasDragging || !edit || btn == null || commit == null) return;
        _suppressClick = true;
        if (double.IsNaN(Canvas.GetLeft(btn)) || double.IsNaN(Canvas.GetTop(btn))) return;
        double nx = Canvas.GetLeft(btn) + btn.Width / 2;
        double ny = Canvas.GetTop(btn) + btn.Height / 2;
        commit(nx, ny);
    }

    private double Snap(double v) =>
        _settings.SnapToGrid ? Math.Round(v / _settings.GridSize) * _settings.GridSize : v;

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void Modules_Click(object sender, RoutedEventArgs e) => OpenModules();

    private void ArmHotkeyCapture(Window owner, Button target, Action<string> done)
    {
        string previous = target.Content?.ToString() ?? "";
        target.Content = Loc.T("set.press");
        KeyEventHandler? handler = null;
        handler = (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or
                Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
                return;
            owner.PreviewKeyDown -= handler;
            if (key == Key.Escape) { target.Content = previous; return; }
            uint m = 0;
            var mods = Keyboard.Modifiers;
            if (mods.HasFlag(ModifierKeys.Control)) m |= HotKeyManager.MOD_CONTROL;
            if (mods.HasFlag(ModifierKeys.Alt)) m |= HotKeyManager.MOD_ALT;
            if (mods.HasFlag(ModifierKeys.Shift)) m |= HotKeyManager.MOD_SHIFT;
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) { target.Content = previous; return; }
            done(HotKeyManager.Format(m, vk));
        };
        owner.PreviewKeyDown += handler;
    }

    private void OpenModules()
    {
        var win = new Window
        {
            Title = Loc.T("mod.title"), Width = 400, Height = 340,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true, ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x15))
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("mod.header"), FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 4)
        });
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("mod.soon"), Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        foreach (var mod in ModuleRegistry.All)
        {
            var captured = mod;
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var check = new System.Windows.Controls.CheckBox
            {
                Content = Loc.T(captured.NameKey),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                IsChecked = ModuleRegistry.IsEnabled(_settings, captured.Id)
            };
            check.Checked += (_, _) => { ModuleRegistry.SetEnabled(_settings, captured.Id, true); ConfigService.SaveSettings(_settings); Render(false); };
            check.Unchecked += (_, _) => { ModuleRegistry.SetEnabled(_settings, captured.Id, false); ConfigService.SaveSettings(_settings); Render(false); };
            row.Children.Add(check);
            row.Children.Add(new TextBlock
            {
                Text = Loc.T(captured.DescKey),
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 2, 0, 0)
            });
            panel.Children.Add(row);
        }
        var ok = new Button { Content = Loc.T("set.done"), Width = 100, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        if (TryFindResource("PillStyle") is Style pill) ok.Style = pill;
        ok.Click += (_, _) => win.Close();
        panel.Children.Add(ok);
        win.Content = panel;
        win.ShowDialog();
    }

    private void OpenSettings()
    {
        var win = new Window
        {
            Title = Loc.T("set.title"), Width = 400, Height = 700,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true, ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x15))
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("set.header"), FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 12)
        });

        var thickLabel = new TextBlock
        {
            Text = Loc.T("set.thickness", _settings.LineThickness),
            Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 4)
        };
        panel.Children.Add(thickLabel);
        var slider = new System.Windows.Controls.Slider
        {
            Minimum = 1, Maximum = 8, TickFrequency = 0.5,
            IsSnapToTickEnabled = true, Value = _settings.LineThickness,
            Margin = new Thickness(0, 0, 0, 12)
        };
        slider.ValueChanged += (_, _) =>
        {
            _settings.LineThickness = Math.Round(slider.Value * 2) / 2;
            thickLabel.Text = Loc.T("set.thickness", _settings.LineThickness);
            ConfigService.SaveSettings(_settings);
            Render(false);
        };
        panel.Children.Add(slider);

        var tensionLabel = new TextBlock
        {
            Text = Loc.T("set.tension", _settings.Tension),
            Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 4)
        };
        panel.Children.Add(tensionLabel);
        var tension = new System.Windows.Controls.Slider
        {
            Minimum = 0.1, Maximum = 1.0, TickFrequency = 0.05,
            IsSnapToTickEnabled = true, Value = _settings.Tension,
            Margin = new Thickness(0, 0, 0, 12)
        };
        tension.ValueChanged += (_, _) =>
        {
            _settings.Tension = Math.Round(tension.Value, 2);
            tensionLabel.Text = Loc.T("set.tension", _settings.Tension);
            ConfigService.SaveSettings(_settings);
        };
        panel.Children.Add(tension);

        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("set.hotkeys"), FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6)
        });
        var mainHkBtn = new Button { Content = _settings.HotKeyMain, Width = 170, Margin = new Thickness(0, 0, 6, 0) };
        var secHkBtn = new Button { Content = _settings.HotKeySecondary, Width = 170 };
        if (TryFindResource("PillStyle") is Style hkPill) { mainHkBtn.Style = hkPill; secHkBtn.Style = hkPill; }
        var mainRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        mainRow.Children.Add(new TextBlock { Text = Loc.T("set.main"), Foreground = Brushes.White, Width = 90, VerticalAlignment = VerticalAlignment.Center });
        mainRow.Children.Add(mainHkBtn);
        panel.Children.Add(mainRow);
        var secRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        secRow.Children.Add(new TextBlock { Text = Loc.T("set.secondary"), Foreground = Brushes.White, Width = 90, VerticalAlignment = VerticalAlignment.Center });
        secRow.Children.Add(secHkBtn);
        panel.Children.Add(secRow);
        var resetHk = new Button { Content = Loc.T("set.reset"), Width = 110, Margin = new Thickness(0, 0, 0, 12), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        if (TryFindResource("PillStyle") is Style hkPill2) resetHk.Style = hkPill2;
        resetHk.Click += (_, _) =>
        {
            _settings.HotKeyMain = "Alt+E";
            _settings.HotKeySecondary = "Alt+Space";
            ConfigService.SaveSettings(_settings);
            mainHkBtn.Content = _settings.HotKeyMain;
            secHkBtn.Content = _settings.HotKeySecondary;
            RegisterHotkeys();
            RebuildTray();
            UpdateHint();
        };
        panel.Children.Add(resetHk);
        mainHkBtn.Click += (_, _) => ArmHotkeyCapture(win, mainHkBtn, s =>
        {
            _settings.HotKeyMain = s;
            ConfigService.SaveSettings(_settings);
            mainHkBtn.Content = s;
            RegisterHotkeys();
            RebuildTray();
            UpdateHint();
        });
        secHkBtn.Click += (_, _) => ArmHotkeyCapture(win, secHkBtn, s =>
        {
            _settings.HotKeySecondary = s;
            ConfigService.SaveSettings(_settings);
            secHkBtn.Content = s;
            RegisterHotkeys();
            RebuildTray();
            UpdateHint();
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Тема", FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6)
        });
        var presetRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var swatches = new List<(Button btn, string role, string hex)>();
        var previews = new Dictionary<string, Border>();
        foreach (var preset in ThemePresets.All)
        {
            var pb = new Button { Content = preset.Name, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 4, 10, 4) };
            if (TryFindResource("PillStyle") is Style pp) pb.Style = pp;
            var captured = preset;
            pb.Click += (_, _) =>
            {
                _settings.LineColorHex = captured.Line;
                _settings.AccentColorHex = captured.Accent;
                _settings.BackgroundColorHex = captured.Background;
                ConfigService.SaveSettings(_settings);
                RefreshThemeUI();
                Render(false);
            };
            presetRow.Children.Add(pb);
        }
        panel.Children.Add(presetRow);

        panel.Children.Add(BuildPaletteSection(Loc.T("set.palLines"), "line", LinePalette, swatches, previews));
        panel.Children.Add(BuildPaletteSection(Loc.T("set.palAccent"), "accent", AccentPalette, swatches, previews));
        panel.Children.Add(BuildPaletteSection(Loc.T("set.palBg"), "bg", BgPalette, swatches, previews));

        void RefreshThemeUI()
        {
            foreach (var (b, role, hex) in swatches)
            {
                string cur = role == "line" ? _settings.LineColorHex
                    : role == "accent" ? _settings.AccentColorHex : _settings.BackgroundColorHex;
                b.BorderBrush = string.Equals(cur, hex, StringComparison.OrdinalIgnoreCase)
                    ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
                b.BorderThickness = new Thickness(string.Equals(cur, hex, StringComparison.OrdinalIgnoreCase) ? 2 : 1);
            }
            if (previews.TryGetValue("line", out var pl)) pl.Background = new SolidColorBrush(ThemeColors.Parse(_settings.LineColorHex, Colors.White));
            if (previews.TryGetValue("accent", out var pa)) pa.Background = new SolidColorBrush(ThemeColors.Parse(_settings.AccentColorHex, Colors.White));
            if (previews.TryGetValue("bg", out var pb2)) pb2.Background = new SolidColorBrush(ThemeColors.Parse(_settings.BackgroundColorHex, Colors.Gray));
        }

        StackPanel BuildPaletteSection(string title, string role, string[] colors,
            List<(Button btn, string role, string hex)> registry, Dictionary<string, Border> prev)
        {
            var section = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var head = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            head.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var preview = new Border
            {
                Width = 44, Height = 22, CornerRadius = new CornerRadius(5),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            };
            prev[role] = preview;
            head.Children.Add(preview);
            section.Children.Add(head);
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 6 };
            foreach (var hex in colors)
            {
                var sw = new Button
                {
                    Tag = hex,
                    ToolTip = hex,
                    Width = 30, Height = 26, Margin = new Thickness(2), Padding = new Thickness(0),
                    Background = new SolidColorBrush(ThemeColors.Parse(hex, Colors.Magenta)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                    Cursor = Cursors.Hand
                };
                sw.Click += (_, _) =>
                {
                    if (role == "line") _settings.LineColorHex = hex;
                    else if (role == "accent") _settings.AccentColorHex = hex;
                    else _settings.BackgroundColorHex = hex;
                    ConfigService.SaveSettings(_settings);
                    RefreshThemeUI();
                    Render(false);
                };
                registry.Add((sw, role, hex));
                grid.Children.Add(sw);
            }
            section.Children.Add(grid);
            return section;
        }

        RefreshThemeUI();

        var snap = new System.Windows.Controls.CheckBox
        {
            Content = Loc.T("set.snap"),
            Foreground = Brushes.White, IsChecked = _settings.SnapToGrid,
            Margin = new Thickness(0, 0, 0, 8)
        };
        snap.Checked += (_, _) => { _settings.SnapToGrid = true; ConfigService.SaveSettings(_settings); };
        snap.Unchecked += (_, _) => { _settings.SnapToGrid = false; ConfigService.SaveSettings(_settings); };
        panel.Children.Add(snap);

        var auto = new System.Windows.Controls.CheckBox
        {
            Content = Loc.T("set.auto"),
            Foreground = Brushes.White, IsChecked = AutostartHelper.IsEnabled(),
            Margin = new Thickness(0, 0, 0, 16)
        };
        auto.Checked += (_, _) => { AutostartHelper.SetEnabled(true); UpdateAutostartBtn(); };
        auto.Unchecked += (_, _) => { AutostartHelper.SetEnabled(false); UpdateAutostartBtn(); };
        panel.Children.Add(auto);

        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("set.language"), FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6)
        });
        var langBox = new System.Windows.Controls.ComboBox { Margin = new Thickness(0, 0, 0, 12), Width = 200, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        langBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.T("set.langAuto"), Tag = "auto" });
        langBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "English", Tag = "en" });
        langBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "Русский", Tag = "ru" });
        bool langInit = true;
        langBox.SelectionChanged += (_, _) =>
        {
            if (langInit) return;
            if (langBox.SelectedItem is System.Windows.Controls.ComboBoxItem sel && sel.Tag is string tag)
            {
                _settings.Language = tag;
                ConfigService.SaveSettings(_settings);
                ApplyLanguage();
                win.Close();
                OpenSettings();
            }
        };
        foreach (System.Windows.Controls.ComboBoxItem item in langBox.Items)
            if ((string)item.Tag == _settings.Language) { langBox.SelectedItem = item; break; }
        panel.Children.Add(langBox);
        langInit = false;

        var ok = new Button { Content = Loc.T("set.done"), Width = 100, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        if (TryFindResource("PillStyle") is Style pill) ok.Style = pill;
        ok.Click += (_, _) => win.Close();
        panel.Children.Add(ok);

        win.Content = new System.Windows.Controls.ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
        };
        win.ShowDialog();
    }

    private void StartSpringFollow(int gi, Button groupBtn)
    {
        StopSpring();
        _dragBtn = groupBtn;
        _dragStartCX = Canvas.GetLeft(groupBtn) + groupBtn.Width / 2;
        _dragStartCY = Canvas.GetTop(groupBtn) + groupBtn.Height / 2;
        var sc = _groups[gi].Shortcuts;
        for (int j = 0; j < sc.Count; j++)
        {
            if (!_shortcutButtons.TryGetValue((gi, j), out var sb)) continue;
            if (!_shortcutLines.TryGetValue((gi, j), out var ln)) continue;
            double sx = Canvas.GetLeft(sb) + sb.Width / 2;
            double sy = Canvas.GetTop(sb) + sb.Height / 2;
            BakeProp(sb, Canvas.LeftProperty);
            BakeProp(sb, Canvas.TopProperty);
            BakeProp(ln.glow, Line.X2Property);
            BakeProp(ln.glow, Line.Y2Property);
            BakeProp(ln.core, Line.X2Property);
            BakeProp(ln.core, Line.Y2Property);
            _springs.Add(new SpringNode
            {
                Gi = gi, Si = j, Btn = sb, Glow = ln.glow, Core = ln.core,
                X = sx, Y = sy, VX = 0, VY = 0, SX = sx, SY = sy, TX = sx, TY = sy
            });
        }
        if (_springs.Count == 0) return;
        _lastFrame = DateTime.UtcNow;
        _springRunning = true;
        CompositionTarget.Rendering += SpringTick;
    }

    private void UpdateSpringTargets(double dx, double dy)
    {
        foreach (var n in _springs) { n.TX = n.SX + dx; n.TY = n.SY + dy; }
    }

    private void CommitSpringFollow(int gi, double snappedGCX, double snappedGCY)
    {
        try
        {
            double dx = snappedGCX - _dragStartCX, dy = snappedGCY - _dragStartCY;
            var sc = _groups[gi].Shortcuts;
            for (int j = 0; j < sc.Count; j++)
            {
                var node = _springs.FirstOrDefault(n => n.Gi == gi && n.Si == j);
                if (node == null) continue;
                sc[j].X = Snap(node.SX + dx);
                sc[j].Y = Snap(node.SY + dy);
            }
        }
        finally { StopSpring(); }
    }

    private void StopSpring()
    {
        if (_springRunning)
        {
            _springRunning = false;
            CompositionTarget.Rendering -= SpringTick;
        }
        _springs.Clear();
        _dragBtn = null;
    }

    private void SpringTick(object? sender, EventArgs e)
    {
        if (!_springRunning || _dragBtn == null || Visibility != Visibility.Visible) { StopSpring(); return; }
        var now = DateTime.UtcNow;
        double dt = Math.Min(0.033, Math.Max(0.0005, (now - _lastFrame).TotalSeconds));
        _lastFrame = now;
        double tension = Math.Clamp(_settings.Tension, 0.1, 1.0);
        double k = 60 + tension * 320;
        double damp = Math.Exp(-2 * Math.Sqrt(k) * 0.32 * dt);
        double gcx = Canvas.GetLeft(_dragBtn) + _dragBtn.Width / 2;
        double gcy = Canvas.GetTop(_dragBtn) + _dragBtn.Height / 2;
        bool settled = !_dragging;
        foreach (var n in _springs)
        {
            n.VX = (n.VX + (n.TX - n.X) * k * dt) * damp;
            n.VY = (n.VY + (n.TY - n.Y) * k * dt) * damp;
            n.X += n.VX * dt;
            n.Y += n.VY * dt;
            Canvas.SetLeft(n.Btn, n.X - n.Btn.Width / 2);
            Canvas.SetTop(n.Btn, n.Y - n.Btn.Height / 2);
            n.Glow.X1 = gcx; n.Glow.Y1 = gcy; n.Glow.X2 = n.X; n.Glow.Y2 = n.Y;
            n.Core.X1 = gcx; n.Core.Y1 = gcy; n.Core.X2 = n.X; n.Core.Y2 = n.Y;
            if (Math.Abs(n.VX) > 0.5 || Math.Abs(n.VY) > 0.5 || Math.Abs(n.TX - n.X) > 0.5 || Math.Abs(n.TY - n.Y) > 0.5)
                settled = false;
        }
        if (settled) StopSpring();
    }

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static readonly string[] LinePalette =
    {
        "#FFFFFF", "#B0B0B0", "#22D3EE", "#A855F7", "#E8C874", "#FF8C1A",
        "#2FA14F", "#E5484D", "#3B82F6", "#EC4899", "#FFD400", "#00E676"
    };

    private static readonly string[] AccentPalette =
    {
        "#FF8C1A", "#FFFFFF", "#22D3EE", "#A855F7", "#D4AF37", "#E8C874",
        "#2FA14F", "#E5484D", "#3B82F6", "#EC4899", "#FFD400", "#00E676"
    };

    private static readonly string[] BgPalette =
    {
        "#EE18181C", "#DC121215", "#E60B0B18", "#E0141210", "#F0202026", "#CC000000",
        "#EE1E1E24", "#EE0F2B3D", "#EE2B0F3D", "#EE0F3D2B", "#EE2E1A47", "#EE3D2B0F"
    };

    private static object BuildButtonContent(string glyph, string name, double glyphSize)
    {
        if (string.IsNullOrEmpty(glyph)) return name;
        var sp = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        sp.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = IconFont, FontSize = glyphSize,
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        sp.Children.Add(new TextBlock { Text = name, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private void PickIcon(string title, string current, Action<string> apply)
    {
        var win = new Window
        {
            Title = title, Width = 450, Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true, ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x15))
        };
        var dock = new StackPanel { Margin = new Thickness(12) };
        dock.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(current) ? Loc.T("icon.noSel") : Loc.T("icon.cur", current),
            Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8)
        });
        var clear = new Button { Content = Loc.T("icon.none"), Width = 130, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        if (TryFindResource("PillStyle") is Style pill0) clear.Style = pill0;
        clear.Click += (_, _) => { apply(""); win.Close(); };
        dock.Children.Add(clear);
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 6 };
        foreach (var (code, label) in IconGlyphs.All)
        {
            string glyph = IconGlyphs.ToGlyph(code);
            var cell = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            cell.Children.Add(new TextBlock
            {
                Text = glyph, FontFamily = IconFont, FontSize = 24,
                Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            });
            cell.Children.Add(new TextBlock
            {
                Text = label, FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                TextAlignment = TextAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            });
            var b = new Button
            {
                Content = cell, Margin = new Thickness(3), Padding = new Thickness(4),
                ToolTip = label,
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x24)),
                Foreground = Brushes.White
            };
            b.Click += (_, _) => { apply(glyph); win.Close(); };
            grid.Children.Add(b);
        }
        dock.Children.Add(new System.Windows.Controls.ScrollViewer
        {
            Content = grid,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Height = 350
        });
        win.Content = dock;
        win.ShowDialog();
    }

    private void AnimateShortcutEntrance(int gi)
    {
        if (gi < 0 || gi >= _groups.Count) return;
        double gx = _groups[gi].X, gy = _groups[gi].Y;
        var gb = FindGroupButton(gi);
        if (gb != null && !double.IsNaN(Canvas.GetLeft(gb)) && !double.IsNaN(Canvas.GetTop(gb)))
        {
            gx = Canvas.GetLeft(gb) + gb.Width / 2;
            gy = Canvas.GetTop(gb) + gb.Height / 2;
        }
        int k = 0;
        foreach (var (key, sb) in _shortcutButtons)
        {
            if (key.Item1 != gi) continue;
            if (!_shortcutLines.TryGetValue(key, out var ln)) continue;
            double fl = Canvas.GetLeft(sb), ft = Canvas.GetTop(sb);
            if (double.IsNaN(fl) || double.IsNaN(ft)) continue;
            int delay = k * 45;
            k++;
            AnimateFlyOut(sb, gx - 70, gy - 24, fl, ft, 0.5, delay);
            foreach (var line in new[] { ln.glow, ln.core })
            {
                double fx2 = line.X2, fy2 = line.Y2;
                line.X2 = gx; line.Y2 = gy;
                line.BeginAnimation(Line.X2Property, MakeAnim(gx, fx2, 300, delay));
                line.BeginAnimation(Line.Y2Property, MakeAnim(gy, fy2, 300, delay));
            }
        }
    }

    private bool ConfirmDelete(string title, string question)
    {
        var win = new Window
        {
            Title = title, Width = 360, Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true, ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x15))
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = question, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var yes = new Button { Content = Loc.T("dlg.yes"), Width = 110, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var no = new Button { Content = Loc.T("prompt.cancel"), Width = 90, IsCancel = true };
        if (TryFindResource("DeleteStyle") is Style del) yes.Style = del;
        if (TryFindResource("PillStyle") is Style pill) no.Style = pill;
        row.Children.Add(yes);
        row.Children.Add(no);
        panel.Children.Add(row);
        win.Content = panel;
        yes.Click += (_, _) => { win.DialogResult = true; };
        return win.ShowDialog() == true;
    }

    private void AskDeleteGroup(int gi)
    {
        if (gi < 0 || gi >= _groups.Count) return;
        if (!ConfirmDelete(Loc.T("del.groupT"), Loc.T("del.groupQ", _groups[gi].Name))) return;
        _groups.RemoveAt(gi);
        _hoverGroup = -1;
        ConfigService.Save(_groups);
        Render(false);
    }

    private void AskDeleteShortcut(int gi, int si)
    {
        if (gi < 0 || gi >= _groups.Count) return;
        var sc = _groups[gi].Shortcuts;
        if (si < 0 || si >= sc.Count) return;
        if (!ConfirmDelete(Loc.T("del.scT"), Loc.T("del.scQ", sc[si].Name))) return;
        sc.RemoveAt(si);
        ConfigService.Save(_groups);
        Render(false);
    }

    private Button? FindGroupButton(int idx)
    {
        foreach (var child in WebCanvas.Children)
            if (child is Button b && b.Tag is int t && t == idx) return b;
        return null;
    }

    private void Group_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressClick) { _suppressClick = false; return; }
        if (_isEditMode) return;
        if (sender is Button b && b.Tag is int idx)
        {
            _hoverGroup = _hoverGroup == idx ? -1 : idx;
            Render(false);
            if (_hoverGroup == idx) AnimateShortcutEntrance(idx);
        }
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressClick) { _suppressClick = false; return; }
        if (_isEditMode) return;
        if (sender is Button b && b.Tag is ValueTuple<int, int> t)
        {
            var entry = _groups[t.Item1].Shortcuts[t.Item2];
            try
            {
                Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
                HideWeb();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("msg.launchFail", entry.Path, ex.Message));
            }
        }
    }

    private ContextMenu BuildGroupMenu(int gi)
    {
        var m = new ContextMenu();
        m.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) HideWeb(); };
        var rename = new MenuItem { Header = Loc.T("ctx.rename") };
        rename.Click += (_, _) =>
        {
            var v = Prompt(Loc.T("prompt.renameT"), Loc.T("prompt.renameL"), _groups[gi].Name);
            if (!string.IsNullOrWhiteSpace(v)) { _groups[gi].Name = v.Trim(); ConfigService.Save(_groups); Render(false); }
        };
        var add = new MenuItem { Header = Loc.T("ctx.addShortcut") };
        add.Click += (_, _) => AddShortcut(gi);
        var icon = new MenuItem { Header = Loc.T("ctx.pickIcon") };
        icon.Click += (_, _) => PickIcon(Loc.T("icon.groupT"), _groups[gi].IconGlyph,
            g => { _groups[gi].IconGlyph = g; ConfigService.Save(_groups); Render(false); });
        var del = new MenuItem { Header = Loc.T("ctx.delGroup") };
        del.Click += (_, _) =>
        {
            if (MessageBox.Show(string.Format(Loc.T("del.groupQ"), _groups[gi].Name), Loc.T("del.confirmT"),
                MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            { _groups.RemoveAt(gi); _hoverGroup = -1; ConfigService.Save(_groups); Render(false); }
        };
        m.Items.Add(rename); m.Items.Add(add); m.Items.Add(icon); m.Items.Add(del);
        return m;
    }

    private ContextMenu BuildShortcutMenu(int gi, int si)
    {
        var m = new ContextMenu();
        m.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) HideWeb(); };
        var ren = new MenuItem { Header = Loc.T("ctx.rename") };
        ren.Click += (_, _) =>
        {
            var v = Prompt(Loc.T("prompt.scT"), Loc.T("prompt.renameL"), _groups[gi].Shortcuts[si].Name);
            if (!string.IsNullOrWhiteSpace(v)) { _groups[gi].Shortcuts[si].Name = v.Trim(); ConfigService.Save(_groups); Render(false); }
        };
        var del = new MenuItem { Header = Loc.T("ctx.del") };
        del.Click += (_, _) => { _groups[gi].Shortcuts.RemoveAt(si); ConfigService.Save(_groups); Render(false); };
        var icon = new MenuItem { Header = Loc.T("ctx.pickIcon") };
        icon.Click += (_, _) => PickIcon(Loc.T("icon.scT"), _groups[gi].Shortcuts[si].IconGlyph,
            g => { _groups[gi].Shortcuts[si].IconGlyph = g; ConfigService.Save(_groups); Render(false); });
        m.Items.Add(ren); m.Items.Add(icon); m.Items.Add(del);
        return m;
    }

    private void AddShortcut(int gi)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("dlg.openTitle", _groups[gi].Name) };
        if (dlg.ShowDialog() == true)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
            var v = Prompt(Loc.T("prompt.scT"), Loc.T("prompt.scNameL"), name);
            if (string.IsNullOrWhiteSpace(v)) return;
            _groups[gi].Shortcuts.Add(new ShortcutEntry { Name = v.Trim(), Path = dlg.FileName });
            ConfigService.Save(_groups);
            Render(false);
        }
    }

    private static string Prompt(string title, string label, string initial = "")
    {
        var win = new Window
        {
            Title = title, Width = 340, Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true, ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox { Text = initial };
        panel.Children.Add(tb);
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = Loc.T("prompt.ok"), Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = Loc.T("prompt.cancel"), Width = 80, IsCancel = true };
        row.Children.Add(ok); row.Children.Add(cancel);
        panel.Children.Add(row);
        win.Content = panel;
        string result = "";
        ok.Click += (_, _) => { result = tb.Text; win.DialogResult = true; };
        return win.ShowDialog() == true ? result : "";
    }
}
