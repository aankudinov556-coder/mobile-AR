using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vera.Core;
using Vera.Core.Coding;
using Vera.Desktop.UI;

namespace Vera.Desktop.Chat;

/// <summary>
/// Раздел «Проекты» (вайбкодинг): проекты из папки «Проекты Веры», их файлы и журнал шагов,
/// «Открыть», «Папка», «Откатить шаг». Живые шаги работы Веры и агента появляются в журнале сразу.
/// </summary>
public sealed class ProjectsPanel : Grid
{
    private readonly AppHost _host;
    private readonly ChatWindow _window;
    private readonly StackPanel _list = new();
    private readonly StackPanel _files = new();
    private readonly StackPanel _journal = new();
    private readonly TextBlock _title;
    private readonly TextBlock _path;
    private readonly FrameworkElement _detail;
    private readonly FrameworkElement _empty;
    private readonly Button _undo;
    private readonly Button _openMain;
    private readonly Dictionary<string, List<(DateTimeOffset At, string Text)>> _live = new();
    private string? _current;

    public ProjectsPanel(AppHost host, ChatWindow window)
    {
        _host = host;
        _window = window;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ------------------------------------------------------------ список проектов
        var head = Ui.Heading("Проекты", 20);
        var root = Ui.Button("Папка проектов", "Btn.Ghost", "folder", () => OpenSafe(Workspace?.Root));
        root.HorizontalAlignment = HorizontalAlignment.Left;
        root.Margin = new Thickness(0, 10, 0, 12);
        var left = new DockPanel { Margin = new Thickness(16, 18, 10, 12) };
        var top = new StackPanel { Children = { head, root } };
        DockPanel.SetDock(top, Dock.Top);
        left.Children.Add(top);
        left.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list });
        var leftHost = new Border { BorderThickness = new Thickness(0, 0, 2, 0), Child = left };
        leftHost.SetResourceReference(Border.BackgroundProperty, "Side");
        leftHost.SetResourceReference(Border.BorderBrushProperty, "Bg3");
        Children.Add(leftHost);

        // ------------------------------------------------------------ проект
        _title = Ui.Heading("", 22);
        _path = Ui.Muted("", 12);
        _openMain = Ui.Button("Открыть", "Btn.Primary", "play", OpenMain);
        var folder = Ui.Button("Папка", "Btn.Ghost", "folder", () => { if (_current is { } p) OpenSafe(Workspace?.ProjectDir(p)); });
        _undo = Ui.Button("Откатить шаг", "Btn.Ghost", "repeat", Undo);
        foreach (var b in new[] { _openMain, folder }) b.Margin = new Thickness(0, 0, 8, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 16), Children = { _openMain, folder, _undo } };

        var filesCard = Ui.Card(Ui.Title("Файлы"), _files);
        var journalCard = Ui.Card(Ui.Title("Журнал"), Ui.Muted("Что делали Вера и агент. «Откатить шаг» возвращает файлы как было до последнего шага.", 12), _journal);
        var cols = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(14) }, new ColumnDefinition() } };
        Grid.SetColumn(journalCard, 2);
        cols.Children.Add(filesCard);
        cols.Children.Add(journalCard);

        _detail = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Margin = new Thickness(24, 20, 24, 24), Children = { _title, _path, buttons, cols } },
        };
        SetColumn(_detail, 1);
        Children.Add(_detail);

        var veraImg = SkinArt.Full(host.Core.Settings.Current.Skin, "wave", 130);
        var emptyText = Ui.Text("Скажи или напиши: «Вера, сделай страничку с таймером на 25 минут» — проект появится здесь. Каждый шаг можно откатить.", 14.5, "Fg");
        emptyText.MaxWidth = 420;
        emptyText.Margin = new Thickness(18, 0, 0, 0);
        emptyText.VerticalAlignment = VerticalAlignment.Center;
        _empty = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Children = { veraImg, emptyText } };
        SetColumn(_empty, 1);
        Children.Add(_empty);

        Refresh();
    }

    private ProjectWorkspace? Workspace => _host.Core.Tools.Coding?.Workspace;

    /// <summary>Шаг работы в проекте (из инструментов) — сразу в журнал.</summary>
    public void OnActivity(string project, string text)
    {
        if (!_live.TryGetValue(project, out var l)) _live[project] = l = new();
        l.Add((DateTimeOffset.Now, text));
        if (l.Count > 100) l.RemoveAt(0);
        if (Visibility != Visibility.Visible) return;
        if (_current is null || _current == project) { _current = project; Refresh(); }
        else FillList();
    }

    public void Refresh()
    {
        FillList();
        var ws = Workspace;
        if (ws is null || _current is null || !ws.Exists(_current))
        {
            _current = ws?.Projects().FirstOrDefault();
            if (_current is null)
            {
                _detail.Visibility = Visibility.Collapsed;
                _empty.Visibility = Visibility.Visible;
                return;
            }
            FillList();
        }
        _detail.Visibility = Visibility.Visible;
        _empty.Visibility = Visibility.Collapsed;
        _title.Text = _current;
        _path.Text = ws!.ProjectDir(_current);

        _files.Children.Clear();
        var files = ws.Files(_current, 200);
        if (files.Count == 0) _files.Children.Add(Ui.Muted("Пока пусто", 12.5));
        foreach (var (path, size) in files)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var sz = Ui.Muted(size < 1024 ? $"{size} B" : $"{size / 1024.0:0.#} KB", 12);
            DockPanel.SetDock(sz, Dock.Right);
            row.Children.Add(sz);
            var name = Ui.Text(path, 13.5, "Fg", wrap: false);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(name);
            var rel = path;
            row.ToolTip = L10n.T("Открыть");
            row.MouseLeftButtonUp += (_, _) => { if (_current is { } p) OpenSafe(ws.Resolve(p, rel)); };
            _files.Children.Add(row);
        }

        _journal.Children.Clear();
        var history = ws.History(_current);
        _undo.IsEnabled = history.Count > 0;
        var entries = history.Select(h => (h.At, h.Title, Saved: true))
            .Concat((_live.TryGetValue(_current, out var l) ? l : new()).Select(x => (x.At, Title: x.Text, Saved: false)))
            .OrderByDescending(x => x.At).Take(60).ToList();
        if (entries.Count == 0) _journal.Children.Add(Ui.Muted("Шагов пока не было", 12.5));
        foreach (var (at, text, saved) in entries)
        {
            var time = Ui.Muted(at.ToLocalTime().ToString("HH:mm"), 12);
            time.Width = 46;
            var t = Ui.Text(text, 13, saved ? "Fg" : "Muted");
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(time);
            row.Children.Add(t);
            _journal.Children.Add(row);
        }
        _openMain.IsEnabled = MainFile(files.Select(f => f.Path)) is not null || files.Count > 0;
    }

    private void FillList()
    {
        _list.Children.Clear();
        var ws = Workspace;
        if (ws is null)
        {
            _list.Children.Add(Ui.Note("Вайбкодинг выключен — включи в Настройки → Что ей можно.", false));
            return;
        }
        var projects = ws.Projects();
        if (projects.Count == 0) _list.Children.Add(Ui.Muted("Проектов пока нет", 12.5));
        foreach (var p in projects)
        {
            var rb = Ui.Radio(p, "projects", "NavItem");
            rb.IsChecked = p == _current;
            var name = p;
            rb.Checked += (_, _) => { if (_current == name) return; _current = name; Refresh(); };
            _list.Children.Add(rb);
        }
    }

    /// <summary>Что открыть кнопкой «Открыть»: index.html, иначе первая страница, иначе папка.</summary>
    private static string? MainFile(IEnumerable<string> files)
    {
        var list = files.ToList();
        return list.FirstOrDefault(f => f.Equals("index.html", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
    }

    private void OpenMain()
    {
        if (_current is not { } p || Workspace is not { } ws) return;
        var main = MainFile(ws.Files(p).Select(f => f.Path));
        OpenSafe(main is null ? ws.ProjectDir(p) : ws.Resolve(p, main));
    }

    private void Undo()
    {
        if (_current is not { } p || Workspace is not { } ws) return;
        var last = ws.History(p).LastOrDefault();
        if (last is null) return;
        if (!ConfirmDialog.Ask(_window, "Откатить шаг?", L10n.F("Вернуть файлы как было до шага «{0}»?", last.Title), okText: "Откатить")) return;
        try
        {
            var t = ws.Undo(p);
            _window.Toast(L10n.F("Откатила: {0}", t ?? ""));
        }
        catch (Exception e)
        {
            _window.Toast(e.Message, true);
        }
        Refresh();
    }

    private void OpenSafe(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (!File.Exists(path)) Directory.CreateDirectory(path);
            _host.CodingHost?.Open(path);
        }
        catch (Exception e)
        {
            _window.Toast(e.Message, true);
        }
    }
}
