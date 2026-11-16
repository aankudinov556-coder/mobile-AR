using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Vera.Core;
using Vera.Core.Commands;
using Vera.Desktop.Platform;
using Vera.Desktop.UI;

namespace Vera.Desktop.Chat;

/// <summary>
/// Редактор своей команды: меню блоков с поиском слева, поле со схемой (блоки перетаскиваются, соединяются линией
/// от выхода к входу), свойства выбранного блока справа. Ctrl+Z — отменить, Delete — удалить, Ctrl+колесо — масштаб.
/// </summary>
public sealed class CommandEditor : Grid
{
    private const double NodeW = 220, NodeH = 84, Port = 14;
    private const string DragFormat = "vera-block";

    private readonly AppHost _host;
    private readonly ChatWindow _window;
    private readonly Action _back;
    private readonly Canvas _board;
    private readonly Border _boardHost;
    private readonly ScaleTransform _zoom = new(1, 1);
    private readonly TranslateTransform _pan = new();
    private readonly TextBlock _zoomText;
    private readonly TextBox _name;
    private readonly CheckBox _enabled;
    private readonly TextBlock _saved;
    private readonly Button _runBtn;
    private readonly StackPanel _palette = new();
    private readonly TextBox _search;
    private readonly StackPanel _inspector = new();
    private readonly Border _hint;
    private readonly DispatcherTimer _saveTimer;
    private readonly Dictionary<string, NodeView> _nodes = new();
    private readonly List<LinkView> _links = new();
    private readonly Stack<string> _undo = new();

    private UserCommand _cmd = new();
    private NodeView? _selNode;
    private LinkView? _selLink;
    private NodeView? _dragNode;
    private Point _dragOffset;
    private bool _dragMoved;
    private (NodeView From, string Port)? _wireFrom;
    private Path? _wire;
    private Point? _panStart;
    private Point _panOrigin;
    private CancellationTokenSource? _runCts;
    private bool _loading;

    public CommandEditor(AppHost host, ChatWindow window, Action back)
    {
        _host = host;
        _window = window;
        _back = back;
        Focusable = true;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });

        // ------------------------------------------------------------ верхняя панель
        var backBtn = Ui.Button("Мои команды", "Btn.Ghost", null, () => { SaveNow(); _back(); });
        _name = new TextBox { FontFamily = Ui.Display, FontSize = 17, FontWeight = FontWeights.Bold, MinWidth = 220, MaxWidth = 360, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        _name.TextChanged += (_, _) => { if (_loading) return; _cmd.Name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : L10n.T("Новая команда"); Changed(false); };
        _enabled = Ui.Toggle("Включена");
        _enabled.VerticalAlignment = VerticalAlignment.Center;
        _enabled.Click += (_, _) => { Remember(); _cmd.Enabled = _enabled.IsChecked == true; Changed(); };
        _saved = Ui.Muted("", 12.5);
        _saved.VerticalAlignment = VerticalAlignment.Center;
        _saved.Margin = new Thickness(0, 0, 12, 0);
        _runBtn = Ui.Button("Проверить", "Btn.Ghost", "play", ToggleRun);
        var save = Ui.Button("Сохранить", "Btn.Primary", null, () => { SaveNow(); _window.Toast(L10n.T("Команда сохранена")); });
        save.Margin = new Thickness(8, 0, 0, 0);
        var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(14, 10, 14, 10) };
        bar.Children.Add(backBtn);
        bar.Children.Add(_name);
        bar.Children.Add(_enabled);
        foreach (var el in new UIElement[] { save, _runBtn, _saved })
        {
            DockPanel.SetDock(el, Dock.Right);
            bar.Children.Add(el);
        }
        var barHost = new Border { BorderThickness = new Thickness(0, 0, 0, 2.5), BorderBrush = Brushes.Black, Child = bar };
        barHost.SetResourceReference(Border.BackgroundProperty, "Bg2");
        SetColumnSpan(barHost, 3);
        Children.Add(barHost);

        // ------------------------------------------------------------ меню блоков
        _search = new TextBox { Tag = L10n.T("Найти блок: сайт, звук…"), Margin = new Thickness(0, 0, 0, 10) };
        _search.TextChanged += (_, _) => FillPalette();
        var paletteCol = new DockPanel { Margin = new Thickness(12, 12, 8, 0) };
        DockPanel.SetDock(_search, Dock.Top);
        paletteCol.Children.Add(_search);
        paletteCol.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _palette });
        var paletteHost = new Border { BorderThickness = new Thickness(0, 0, 2, 0), Child = paletteCol };
        paletteHost.SetResourceReference(Border.BackgroundProperty, "Side");
        paletteHost.SetResourceReference(Border.BorderBrushProperty, "Bg3");
        SetRow(paletteHost, 1);
        Children.Add(paletteHost);
        FillPalette();

        // ------------------------------------------------------------ поле
        _board = new Canvas { Width = 3200, Height = 2400, AllowDrop = true, RenderTransform = new TransformGroup { Children = { _zoom, _pan } } };
        _board.SetResourceReference(Panel.BackgroundProperty, "Dots");
        _board.MouseLeftButtonDown += BoardDown;
        _board.MouseMove += BoardMove;
        _board.MouseLeftButtonUp += BoardUp;
        _board.MouseRightButtonDown += (_, e) => StartPan(e);
        _board.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        _board.Drop += BoardDrop;
        var boardClip = new Grid { ClipToBounds = true, Children = { _board } };
        boardClip.MouseWheel += Wheel;
        boardClip.PreviewMouseMove += (_, e) => PanMove(e);
        boardClip.PreviewMouseRightButtonUp += (_, _) => EndPan();
        boardClip.PreviewMouseLeftButtonUp += (_, _) => EndPan();
        boardClip.PreviewMouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) EndPan(); };
        boardClip.PreviewMouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) StartPan(e); };

        _zoomText = Ui.Text("100%", 12.5, "Fg", FontWeights.SemiBold, wrap: false);
        _zoomText.VerticalAlignment = VerticalAlignment.Center;
        _zoomText.Width = 44;
        _zoomText.TextAlignment = TextAlignment.Center;
        var zoomBox = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 12, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    Ui.IconButton("minus", "Уменьшить", () => Zoom(-0.1, null), 30, 14), _zoomText,
                    Ui.IconButton("plus", "Увеличить", () => Zoom(0.1, null), 30, 14),
                },
            },
        };
        zoomBox.SetResourceReference(Border.BackgroundProperty, "Bg2");
        zoomBox.SetResourceReference(Border.BorderBrushProperty, "Line");

        var hintText = Ui.Text("Перетащи блок из меню слева. Потом тяни от золотого кружка к белому — так блоки соединяются.", 13, "VeraBubbleFg");
        hintText.MaxWidth = 300;
        var hintBubble = new Border
        {
            CornerRadius = new CornerRadius(18, 18, 6, 18),
            BorderThickness = new Thickness(2.5),
            BorderBrush = Brushes.Black,
            Padding = new Thickness(12, 9, 12, 9),
            Effect = Anim.HardShadow(3),
            Child = hintText,
            Margin = new Thickness(0, 0, 0, 26),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        hintBubble.SetResourceReference(Border.BackgroundProperty, "Paper");
        var hintVera = SkinArt.Full(_host.Core.Settings.Current.Skin, "surprised", 100);
        _hint = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 14, 10),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { hintBubble, hintVera } },
            Cursor = Cursors.Hand,
            ToolTip = L10n.T("Скрыть подсказку"),
        };
        _hint.MouseLeftButtonUp += (_, _) => _hint.Visibility = Visibility.Collapsed;

        _boardHost = new Border { Child = new Grid { Children = { boardClip, zoomBox, _hint } } };
        _boardHost.SetResourceReference(Border.BackgroundProperty, "Bg");
        SetRow(_boardHost, 1);
        SetColumn(_boardHost, 1);
        Children.Add(_boardHost);

        // ------------------------------------------------------------ свойства
        var inspectorHost = new Border
        {
            BorderThickness = new Thickness(2, 0, 0, 0),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _inspector, Padding = new Thickness(16) },
        };
        inspectorHost.SetResourceReference(Border.BackgroundProperty, "Side");
        inspectorHost.SetResourceReference(Border.BorderBrushProperty, "Bg3");
        SetRow(inspectorHost, 1);
        SetColumn(inspectorHost, 2);
        Children.Add(inspectorHost);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _saveTimer.Tick += (_, _) => SaveNow();
        PreviewKeyDown += OnKeys;
    }

    // ================================================================== открыть / сохранить

    public void Open(UserCommand cmd)
    {
        _runCts?.Cancel();
        _cmd = cmd.Clone();
        _undo.Clear();
        _loading = true;
        _name.Text = _cmd.Name;
        _enabled.IsChecked = _cmd.Enabled;
        _loading = false;
        _zoom.ScaleX = _zoom.ScaleY = 1;
        _pan.X = _pan.Y = 0;
        _zoomText.Text = "100%";
        Rebuild();
        Select(null, null);
        _saved.Text = "";
        _hint.Visibility = _cmd.Links.Count < 3 ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(() => Focus(), DispatcherPriority.Input);
    }

    /// <summary>Сохранить, если есть изменения (вызывается и при уходе со страницы).</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        _dirty = false;
        _host.Core.Commands.Save(_cmd.Clone());
        _saved.Text = L10n.T("Сохранено");
    }

    private bool _dirty;

    private void Changed(bool redraw = true)
    {
        _dirty = true;
        _saved.Text = L10n.T("Есть изменения…");
        _saveTimer.Stop();
        _saveTimer.Start();
        if (redraw) Rebuild();
    }

    /// <summary>Запомнить состояние до изменения (для Ctrl+Z).</summary>
    private void Remember()
    {
        _undo.Push(JsonSerializer.Serialize(_cmd, VeraJson.Options));
        if (_undo.Count > 60)
        {
            var keep = _undo.Take(60).Reverse().ToList();
            _undo.Clear();
            foreach (var k in keep) _undo.Push(k);
        }
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var sel = _selNode?.Node.Id;
        _cmd = JsonSerializer.Deserialize<UserCommand>(_undo.Pop(), VeraJson.Options)!;
        _loading = true;
        _name.Text = _cmd.Name;
        _enabled.IsChecked = _cmd.Enabled;
        _loading = false;
        Changed();
        Select(sel is not null && _nodes.TryGetValue(sel, out var nv) ? nv : null, null);
    }

    // ================================================================== меню блоков

    private void FillPalette()
    {
        _palette.Children.Clear();
        var found = Blocks.Search(_search?.Text).ToList();
        foreach (var cat in new[] { BlockCategory.Start, BlockCategory.Action, BlockCategory.Logic, BlockCategory.Vera })
        {
            var items = found.Where(b => b.Category == cat).ToList();
            if (items.Count == 0) continue;
            var head = Ui.Text(L10n.T(Blocks.CategoryTitle(cat)).ToUpperInvariant(), 11, "Muted", FontWeights.Bold, wrap: false);
            head.Foreground = CatBrush(cat);
            head.Margin = new Thickness(4, _palette.Children.Count == 0 ? 0 : 12, 0, 4);
            _palette.Children.Add(head);
            foreach (var b in items) _palette.Children.Add(PaletteItem(b));
        }
        if (_palette.Children.Count == 0) _palette.Children.Add(Ui.Muted("Ничего не нашлось", 12.5));
    }

    private FrameworkElement PaletteItem(BlockDef b)
    {
        var icon = IconSquare(b, 28, 15);
        var title = Ui.Text(b.Title, 13.5, "Fg", wrap: false);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(10, 0, 0, 0);
        var row = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(0, 0, 0, 2),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, title } },
            ToolTip = L10n.T("Перетащи на поле или дважды щёлкни"),
        };
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "Bg3");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        Point? down = null;
        row.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { AddAtCenter(b.Type); e.Handled = true; return; }
            down = e.GetPosition(row);
        };
        row.PreviewMouseMove += (_, e) =>
        {
            if (down is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
            if ((e.GetPosition(row) - d).Length < 5) return;
            down = null;
            DragDrop.DoDragDrop(row, new DataObject(DragFormat, b.Type), DragDropEffects.Copy);
        };
        row.PreviewMouseLeftButtonUp += (_, _) => down = null;
        return row;
    }

    private void AddAtCenter(string type)
    {
        var host = _board.Parent as FrameworkElement;
        var c = host is null ? new Point(400, 300) : host.TranslatePoint(new Point(host.ActualWidth / 2, host.ActualHeight / 2), _board);
        AddNode(type, c.X - NodeW / 2, c.Y - NodeH / 2);
    }

    private void BoardDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not string type) return;
        var p = e.GetPosition(_board);
        AddNode(type, p.X - NodeW / 2, p.Y - 20);
        e.Handled = true;
    }

    private void AddNode(string type, double x, double y)
    {
        Remember();
        var n = Blocks.Create(type, Snap(Math.Max(0, x)), Snap(Math.Max(0, y)));
        // новый блок сразу подключаем к выбранному, если у того свободен выход
        if (_selNode is { } prev && Blocks.Get(n.Type)!.HasInput)
        {
            var free = Blocks.Get(prev.Node.Type)!.Ports.Select(p => p.Port).FirstOrDefault(p => _cmd.Links.All(l => !(l.From == prev.Node.Id && l.Port == p)));
            if (free is not null) _cmd.Links.Add(new CommandLink { From = prev.Node.Id, Port = free, To = n.Id });
        }
        _cmd.Nodes.Add(n);
        Changed();
        Select(_nodes[n.Id], null);
        if (_cmd.Links.Count >= 3) _hint.Visibility = Visibility.Collapsed;
    }

    private static double Snap(double v) => Math.Round(v / 10) * 10;

    // ================================================================== отрисовка

    private sealed class NodeView
    {
        public required CommandNode Node;
        public required BlockDef Def;
        public required Border Root;
        public required TextBlock Summary;
        public Ellipse? In;
        public readonly Dictionary<string, Ellipse> Outs = new();
        public readonly List<UIElement> Extras = new();
        public required Ellipse Halo;
    }

    private sealed class LinkView
    {
        public required CommandLink Link;
        public required Path Outline;
        public required Path Line;
    }

    private void Rebuild()
    {
        var sel = _selNode?.Node.Id;
        var selLink = _selLink?.Link;
        _board.Children.Clear();
        _nodes.Clear();
        _links.Clear();
        // висячие линии (блок удалён) — убираем
        _cmd.Links.RemoveAll(l => _cmd.Node(l.From) is null || _cmd.Node(l.To) is null);
        foreach (var n in _cmd.Nodes)
        {
            if (Blocks.Get(n.Type) is not { } def) continue;
            _nodes[n.Id] = MakeNode(n, def);
        }
        foreach (var l in _cmd.Links) _links.Add(MakeLink(l));
        foreach (var lv in _links) { _board.Children.Insert(0, lv.Line); _board.Children.Insert(0, lv.Outline); }
        foreach (var nv in _nodes.Values) Place(nv);
        _selNode = sel is not null && _nodes.TryGetValue(sel, out var s) ? s : null;
        _selLink = selLink is null ? null : _links.FirstOrDefault(x => x.Link == selLink);
        Highlight();
    }

    private NodeView MakeNode(CommandNode n, BlockDef def)
    {
        var caps = Ui.Text(L10n.T(def.Title).ToUpperInvariant(), 11, "Fg", FontWeights.Bold, wrap: false);
        caps.Foreground = CatBrush(def.Category);
        caps.Margin = new Thickness(7, 0, 0, 0);
        caps.VerticalAlignment = VerticalAlignment.Center;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Children = { Icons.Make(def.Icon, 14), caps } };
        ((FrameworkElement)head.Children[0]).SetValue(TextElement.ForegroundProperty, CatBrush(def.Category));
        var summary = Ui.Text(def.Summary(n), 13.5, "Fg", FontWeights.SemiBold);
        summary.TextTrimming = TextTrimming.CharacterEllipsis;
        summary.MaxHeight = 40;
        summary.Margin = new Thickness(0, 6, 0, 0);
        var root = new Border
        {
            Width = NodeW,
            Height = NodeH,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(2.5),
            BorderBrush = Brushes.Black,
            Padding = new Thickness(12, 9, 12, 9),
            Effect = Anim.HardShadow(3),
            Child = new StackPanel { Children = { head, summary } },
            Cursor = Cursors.SizeAll,
        };
        root.SetResourceReference(Border.BackgroundProperty, "Bg3");
        var halo = new Ellipse { Width = 130, Height = 13, StrokeThickness = 3, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        halo.SetResourceReference(Shape.StrokeProperty, "Accent");
        var nv = new NodeView { Node = n, Def = def, Root = root, Summary = summary, Halo = halo };
        root.MouseLeftButtonDown += (_, e) => NodeDown(nv, e);
        _board.Children.Add(halo);
        _board.Children.Add(root);
        if (def.HasInput)
        {
            nv.In = PortDot(false);
            _board.Children.Add(nv.In);
        }
        foreach (var (port, label) in def.Ports)
        {
            var dot = PortDot(true);
            var p = port;
            dot.MouseLeftButtonDown += (_, e) => { StartWire(nv, p); e.Handled = true; };
            nv.Outs[port] = dot;
            _board.Children.Add(dot);
            if (label.Length > 0)
            {
                var t = Ui.Text(label, 11.5, port is "no" ? "Muted" : "Accent", FontWeights.Bold, wrap: false);
                t.IsHitTestVisible = false;
                t.Tag = port;
                nv.Extras.Add(t);
                _board.Children.Add(t);
            }
        }
        return nv;
    }

    private static Ellipse PortDot(bool output)
    {
        var e = new Ellipse { Width = Port, Height = Port, StrokeThickness = 2.5, Stroke = Brushes.Black, Cursor = output ? Cursors.Cross : Cursors.Arrow };
        e.SetResourceReference(Shape.FillProperty, output ? "Accent" : "Paper");
        return e;
    }

    private static Point OutPos(NodeView nv, string port)
    {
        var ports = nv.Def.Ports;
        var i = Math.Max(0, ports.ToList().FindIndex(p => p.Port == port));
        var x = nv.Node.X + NodeW * (i + 1) / (ports.Count + 1);
        return new Point(x, nv.Node.Y + NodeH);
    }

    private static Point InPos(NodeView nv) => new(nv.Node.X + NodeW / 2, nv.Node.Y);

    private void Place(NodeView nv)
    {
        Canvas.SetLeft(nv.Root, nv.Node.X);
        Canvas.SetTop(nv.Root, nv.Node.Y);
        Canvas.SetLeft(nv.Halo, nv.Node.X + NodeW / 2 - 65);
        Canvas.SetTop(nv.Halo, nv.Node.Y - 15);
        if (nv.In is not null)
        {
            var p = InPos(nv);
            Canvas.SetLeft(nv.In, p.X - Port / 2);
            Canvas.SetTop(nv.In, p.Y - Port / 2);
        }
        foreach (var (port, dot) in nv.Outs)
        {
            var p = OutPos(nv, port);
            Canvas.SetLeft(dot, p.X - Port / 2);
            Canvas.SetTop(dot, p.Y - Port / 2);
        }
        foreach (var t in nv.Extras.OfType<TextBlock>())
        {
            var p = OutPos(nv, (string)t.Tag);
            Canvas.SetLeft(t, p.X + 10);
            Canvas.SetTop(t, p.Y + 2);
        }
        foreach (var lv in _links.Where(l => l.Link.From == nv.Node.Id || l.Link.To == nv.Node.Id)) Route(lv);
    }

    private LinkView MakeLink(CommandLink l)
    {
        var outline = new Path { StrokeThickness = 7, Stroke = Brushes.Black, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Cursor = Cursors.Hand };
        var line = new Path { StrokeThickness = 3.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
        line.SetResourceReference(Shape.StrokeProperty, l.Port == "no" ? "Muted" : "Accent");
        var lv = new LinkView { Link = l, Outline = outline, Line = line };
        outline.MouseLeftButtonDown += (_, e) => { Select(null, lv); e.Handled = true; };
        return lv;
    }

    private void Route(LinkView lv)
    {
        if (!_nodes.TryGetValue(lv.Link.From, out var a) || !_nodes.TryGetValue(lv.Link.To, out var b)) return;
        var g = Curve(OutPos(a, lv.Link.Port), InPos(b));
        lv.Outline.Data = g;
        lv.Line.Data = g;
    }

    private static Geometry Curve(Point p1, Point p2)
    {
        var dy = Math.Max(40, Math.Abs(p2.Y - p1.Y) / 2);
        var fig = new PathFigure { StartPoint = p1, Segments = { new BezierSegment(new Point(p1.X, p1.Y + dy), new Point(p2.X, p2.Y - dy), p2, true) } };
        return new PathGeometry { Figures = { fig } };
    }

    // ================================================================== выделение и свойства

    private void Select(NodeView? node, LinkView? link)
    {
        _selNode = node;
        _selLink = link;
        Highlight();
        FillInspector();
    }

    private void Highlight()
    {
        foreach (var nv in _nodes.Values)
        {
            var on = nv == _selNode;
            if (on) nv.Root.SetResourceReference(Border.BorderBrushProperty, "Accent"); else nv.Root.BorderBrush = Brushes.Black;
            nv.Halo.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var lv in _links)
        {
            var on = lv == _selLink;
            lv.Line.StrokeDashArray = on ? new DoubleCollection { 2, 1.5 } : null;
            lv.Line.StrokeThickness = on ? 4.5 : 3.5;
        }
    }

    private void FillInspector()
    {
        _inspector.Children.Clear();
        if (_selLink is { } lv)
        {
            _inspector.Children.Add(Ui.Title("Линия"));
            _inspector.Children.Add(Ui.Muted("Соединяет два блока. Delete — убрать.", 12.5));
            var del = Ui.Button("Убрать линию", "Btn.Ghost", "trash", () => DeleteLink(lv));
            del.Margin = new Thickness(0, 12, 0, 0);
            del.HorizontalAlignment = HorizontalAlignment.Left;
            _inspector.Children.Add(del);
            return;
        }
        if (_selNode is not { } nv)
        {
            CommandSettings();
            return;
        }
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(IconSquare(nv.Def, 40, 20));
        var titles = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Ui.Title(nv.Def.Title));
        titles.Children.Add(Ui.Muted(Blocks.CategoryTitle(nv.Def.Category), 12));
        head.Children.Add(titles);
        _inspector.Children.Add(head);
        foreach (var p in nv.Def.Props) _inspector.Children.Add(PropEditor(nv, p));
        if (nv.Def.Type == "powershell")
            _inspector.Children.Add(Ui.Note("Команда выполнится с твоими правами. Перед запуском Вера спросит подтверждение.", false));
        if (nv.Def.Type == "hotkey")
            _inspector.Children.Add(Ui.Muted("Клавиши работают везде, даже когда окно Веры закрыто.", 12));
        var del2 = Ui.Button("Удалить блок", "Btn.Ghost", "trash", () => DeleteNode(nv));
        del2.Margin = new Thickness(0, 16, 0, 0);
        del2.HorizontalAlignment = HorizontalAlignment.Left;
        _inspector.Children.Add(del2);
        _inspector.Children.Add(new Border { Height = 2, Margin = new Thickness(0, 18, 0, 14) }.Res(Border.BackgroundProperty, "Bg3"));
        CommandSettings();
    }

    /// <summary>Настройки всей команды: разрешение Вере и как её запускать.</summary>
    private void CommandSettings()
    {
        _inspector.Children.Add(Ui.Text(L10n.T("Как запускать команду").ToUpperInvariant(), 11, "Muted", FontWeights.Bold));
        var triggers = _cmd.Triggers.ToList();
        if (triggers.Count == 0)
            _inspector.Children.Add(Ui.Note("Добавь блок из раздела «Запуск» — например, «Когда скажу».", false));
        foreach (var t in triggers)
        {
            var def = Blocks.Get(t.Type)!;
            var line = Ui.Text(L10n.T(def.Title) + ": " + def.Summary(t), 13, "Fg");
            line.Margin = new Thickness(0, 6, 0, 0);
            _inspector.Children.Add(line);
        }
        var ai = Ui.Toggle("Вера может запустить её сама, если поймёт просьбу своими словами");
        ai.IsChecked = _cmd.AllowAi;
        ai.Margin = new Thickness(0, 12, 0, 0);
        ai.Click += (_, _) => { Remember(); _cmd.AllowAi = ai.IsChecked == true; Changed(false); };
        _inspector.Children.Add(ai);
        _inspector.Children.Add(Ui.Muted("Выдели блок, чтобы изменить его. Ctrl+Z — отменить, Delete — удалить, Ctrl+колесо — масштаб.", 12));
    }

    private UIElement PropEditor(NodeView nv, BlockProp p)
    {
        var node = nv.Node;
        void Set(string v)
        {
            node.Props[p.Key] = v;
            nv.Summary.Text = nv.Def.Summary(node);
            Changed(false);
        }
        UIElement control;
        switch (p.Kind)
        {
            case PropKind.Choice:
            {
                var c = Ui.Combo((p.Options ?? Array.Empty<(string, string)>()).Select(o => (o.Value, o.Title)));
                Ui.Select(c, node.Get(p.Key));
                c.SelectionChanged += (_, _) => { if (Ui.Selected(c) is { } v && v != node.Get(p.Key)) { Remember(); Set(v); } };
                control = c;
                break;
            }
            case PropKind.Hotkey:
            {
                var tb = new TextBox { Text = Accelerator.Pretty(node.Get(p.Key)), IsReadOnly = true, IsReadOnlyCaretVisible = false, Tag = L10n.T("Нажми сочетание…") };
                tb.GotKeyboardFocus += (_, _) => _host.PauseHotkeys(true);
                tb.LostKeyboardFocus += (_, _) => _host.PauseHotkeys(false);
                tb.PreviewKeyDown += (_, e) =>
                {
                    e.Handled = true;
                    var key = e.Key == Key.System ? e.SystemKey : e.Key;
                    if (key is Key.Back or Key.Delete) { Remember(); Set(""); tb.Text = ""; return; }
                    var accel = Accelerator.FromKeyEvent(key, Keyboard.Modifiers, out var err);
                    if (accel is null) { if (err is not null) _window.Toast(err, true); return; }
                    Remember();
                    Set(accel);
                    tb.Text = Accelerator.Pretty(accel);
                };
                control = tb;
                break;
            }
            default:
            {
                var tb = new TextBox { Text = node.Get(p.Key), Tag = p.Kind == PropKind.Time ? "чч:мм" : "" };
                if (p.Kind == PropKind.Multiline)
                {
                    tb.AcceptsReturn = true;
                    tb.TextWrapping = TextWrapping.Wrap;
                    tb.MinHeight = 70;
                    tb.VerticalContentAlignment = VerticalAlignment.Top;
                }
                tb.GotKeyboardFocus += (_, _) => Remember();
                tb.TextChanged += (_, _) => Set(tb.Text);
                control = tb;
                break;
            }
        }
        var field = Ui.Field(p.Label, control);
        if (p.Hint is not null) field.Children.Add(Ui.Muted(p.Hint, 12));
        return field;
    }

    // ================================================================== мышь: блоки, линии, панорама

    private void NodeDown(NodeView nv, MouseButtonEventArgs e)
    {
        Select(nv, null);
        _dragNode = nv;
        _dragMoved = false;
        var p = e.GetPosition(_board);
        _dragOffset = new Point(p.X - nv.Node.X, p.Y - nv.Node.Y);
        _board.CaptureMouse();
        Focus();
        e.Handled = true;
    }

    private void StartWire((NodeView, string) from)
    {
        _wireFrom = from;
        _wire = new Path { StrokeThickness = 3, StrokeDashArray = new DoubleCollection { 2.2, 2.2 }, IsHitTestVisible = false };
        _wire.SetResourceReference(Shape.StrokeProperty, "Accent");
        _board.Children.Add(_wire);
        _board.CaptureMouse();
    }

    private void StartWire(NodeView nv, string port) => StartWire((nv, port));

    private void BoardDown(object sender, MouseButtonEventArgs e)
    {
        // пустое место поля: снять выделение и тащить поле
        Select(null, null);
        StartPan(e);
        Focus();
    }

    private void BoardMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_board);
        if (_dragNode is { } nv && e.LeftButton == MouseButtonState.Pressed)
        {
            var x = Snap(Math.Max(0, p.X - _dragOffset.X));
            var y = Snap(Math.Max(0, p.Y - _dragOffset.Y));
            if (x == nv.Node.X && y == nv.Node.Y) return;
            if (!_dragMoved) { Remember(); _dragMoved = true; }
            nv.Node.X = x;
            nv.Node.Y = y;
            Place(nv);
            return;
        }
        if (_wireFrom is { } w && _wire is not null)
            _wire.Data = Curve(OutPos(w.From, w.Port), p);
    }

    private void BoardUp(object sender, MouseButtonEventArgs e)
    {
        _board.ReleaseMouseCapture();
        if (_dragNode is not null)
        {
            if (_dragMoved) Changed(false);
            _dragNode = null;
            return;
        }
        if (_wireFrom is { } w)
        {
            var p = e.GetPosition(_board);
            _board.Children.Remove(_wire);
            _wire = null;
            _wireFrom = null;
            var target = _nodes.Values.FirstOrDefault(n => n != w.From && n.Def.HasInput &&
                ((InPos(n) - p).Length < 22 || new Rect(n.Node.X, n.Node.Y, NodeW, NodeH).Contains(p)));
            if (target is null) return;
            Remember();
            _cmd.Links.RemoveAll(l => l.From == w.From.Node.Id && l.Port == w.Port); // из выхода — одна линия
            _cmd.Links.Add(new CommandLink { From = w.From.Node.Id, Port = w.Port, To = target.Node.Id });
            Changed();
            if (_cmd.Links.Count >= 3) _hint.Visibility = Visibility.Collapsed;
        }
    }

    private void StartPan(MouseButtonEventArgs e)
    {
        var host = (FrameworkElement)_board.Parent;
        _panStart = e.GetPosition(host);
        _panOrigin = new Point(_pan.X, _pan.Y);
        host.CaptureMouse();
    }

    private void PanMove(MouseEventArgs e)
    {
        if (_panStart is not { } s) return;
        if (e.LeftButton != MouseButtonState.Pressed && e.RightButton != MouseButtonState.Pressed && e.MiddleButton != MouseButtonState.Pressed)
        {
            EndPan();
            return;
        }
        var host = (FrameworkElement)_board.Parent;
        var d = e.GetPosition(host) - s;
        _pan.X = Math.Min(0, _panOrigin.X + d.X);
        _pan.Y = Math.Min(0, _panOrigin.Y + d.Y);
    }

    private void EndPan()
    {
        if (_panStart is null) return;
        _panStart = null;
        ((FrameworkElement)_board.Parent).ReleaseMouseCapture();
    }

    private void Wheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Zoom(e.Delta > 0 ? 0.1 : -0.1, e.GetPosition((IInputElement)sender));
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _pan.X = Math.Min(0, _pan.X + e.Delta / 2.0);
        else _pan.Y = Math.Min(0, _pan.Y + e.Delta / 2.0);
        e.Handled = true;
    }

    /// <summary>Масштаб 50–160% вокруг точки (по умолчанию — центр видимой области).</summary>
    private void Zoom(double step, Point? around)
    {
        var host = (FrameworkElement)_board.Parent;
        var c = around ?? new Point(host.ActualWidth / 2, host.ActualHeight / 2);
        var old = _zoom.ScaleX;
        var next = Math.Clamp(Math.Round((old + step) * 10) / 10, 0.5, 1.6);
        if (Math.Abs(next - old) < 0.001) return;
        // точка под курсором остаётся на месте
        var bx = (c.X - _pan.X) / old;
        var by = (c.Y - _pan.Y) / old;
        _zoom.ScaleX = _zoom.ScaleY = next;
        _pan.X = Math.Min(0, c.X - bx * next);
        _pan.Y = Math.Min(0, c.Y - by * next);
        _zoomText.Text = $"{next * 100:0}%";
    }

    // ================================================================== клавиши и удаление

    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return; // в полях свойств клавиши — для текста
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo(); e.Handled = true; }
        else if (e.Key is Key.Delete or Key.Back)
        {
            if (_selNode is { } n) DeleteNode(n);
            else if (_selLink is { } l) DeleteLink(l);
            e.Handled = true;
        }
    }

    private void DeleteNode(NodeView nv)
    {
        Remember();
        _cmd.Nodes.Remove(nv.Node);
        _cmd.Links.RemoveAll(l => l.From == nv.Node.Id || l.To == nv.Node.Id);
        _selNode = null;
        Changed();
        FillInspector();
    }

    private void DeleteLink(LinkView lv)
    {
        Remember();
        _cmd.Links.Remove(lv.Link);
        _selLink = null;
        Changed();
        FillInspector();
    }

    // ================================================================== проверка

    private async void ToggleRun()
    {
        if (_runCts is not null)
        {
            _runCts.Cancel();
            return;
        }
        SaveNow();
        _runCts = new CancellationTokenSource();
        _runBtn.Content = Ui.Content(L10n.T("Стоп"), "stop", 16);
        foreach (var nv in _nodes.Values) nv.Root.Opacity = 1;
        var runner = _host.CreateCommandRunner();
        runner.StepStarted += id => Dispatcher.BeginInvoke(() =>
        {
            if (!_nodes.TryGetValue(id, out var nv)) return;
            nv.Root.SetResourceReference(Border.BorderBrushProperty, "Accent");
            nv.Root.BorderThickness = new Thickness(4);
            Anim.Pop(nv.Root, 220, 0.94);
        });
        runner.StepFinished += (id, ok, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!_nodes.TryGetValue(id, out var nv)) return;
            nv.Root.BorderThickness = new Thickness(2.5);
            nv.Root.SetResourceReference(Border.BorderBrushProperty, ok ? "Ok" : "Err");
        });
        try
        {
            var res = await runner.RunAsync(_cmd.Clone(), null, _runCts.Token);
            _window.Toast(res.Ok ? L10n.T("Проверка прошла: ") + res.Message : res.Message, !res.Ok);
            if (!res.Ok && res.FailedNode is { } bad && _nodes.TryGetValue(bad, out var badView)) Select(badView, null);
        }
        finally
        {
            _runCts = null;
            _runBtn.Content = Ui.Content(L10n.T("Проверить"), "play", 16);
            // через 2 с вернуть обычные рамки
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (_, _) => { t.Stop(); Highlight(); };
            t.Start();
        }
    }

    // ================================================================== оформление

    public static Brush CatBrush(BlockCategory c) => c switch
    {
        BlockCategory.Start => (Brush)(Application.Current.TryFindResource("Accent") ?? Brushes.Goldenrod),
        BlockCategory.Action => Frozen("#5E8FE0"),
        BlockCategory.Logic => Frozen("#D86BAE"),
        _ => Frozen("#3FB37A"),
    };

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static Border IconSquare(BlockDef b, double size, double icon)
    {
        var ic = Icons.Make(b.Icon, icon);
        ic.SetValue(TextElement.ForegroundProperty, CatBrush(b.Category));
        var box = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 3.5),
            BorderThickness = new Thickness(2),
            Child = ic,
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.SetResourceReference(Border.BackgroundProperty, "Bg3");
        box.SetResourceReference(Border.BorderBrushProperty, "Line");
        return box;
    }
}
