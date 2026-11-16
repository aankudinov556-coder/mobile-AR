using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vera.Core;
using Vera.Core.Commands;
using Vera.Desktop.UI;

namespace Vera.Desktop.Chat;

/// <summary>Раздел «Команды»: список своих команд и редактор схемы.</summary>
public sealed class CommandsPanel : Grid
{
    private readonly AppHost _host;
    private readonly ChatWindow _window;
    private readonly WrapPanel _cards = new();
    private readonly FrameworkElement _list;
    private readonly CommandEditor _editor;

    public CommandsPanel(AppHost host, ChatWindow window)
    {
        _host = host;
        _window = window;

        var title = Ui.Heading("Мои команды", 22);
        var add = Ui.Button("Новая команда", "Btn.Primary", "plus", NewCommand);
        add.HorizontalAlignment = HorizontalAlignment.Right;
        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(title);
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);
        var about = Ui.Muted("Собери команду из блоков: «Вера, рабочий режим» — и она сделает всё по порядку. Можно запускать клавишами или по расписанию.", 13);
        about.Margin = new Thickness(0, 0, 0, 16);
        var col = new StackPanel { Margin = new Thickness(24, 20, 24, 24), Children = { head, about, _cards } };
        _list = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = col };
        Children.Add(_list);

        _editor = new CommandEditor(host, window, ShowList) { Visibility = Visibility.Collapsed };
        Children.Add(_editor);

        _host.Core.Commands.Changed += () => Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    /// <summary>Сейчас открыт редактор (для узкого окна — подсказать развернуть).</summary>
    public bool Editing => _editor.Visibility == Visibility.Visible;

    public void ShowList()
    {
        _editor.SaveNow();
        _editor.Visibility = Visibility.Collapsed;
        _list.Visibility = Visibility.Visible;
        Refresh();
    }

    public void Edit(UserCommand c)
    {
        _list.Visibility = Visibility.Collapsed;
        _editor.Visibility = Visibility.Visible;
        _editor.Open(c);
        Anim.Appear(_editor, 10, 200);
    }

    /// <summary>Уход из раздела — сохранить черновик.</summary>
    public void Leave() => _editor.SaveNow();

    private void NewCommand()
    {
        var c = Blocks.Sample();
        c.Name = L10n.T("Новая команда");
        _host.Core.Commands.Save(c);
        Edit(c);
    }

    public void Refresh()
    {
        _cards.Children.Clear();
        var list = _host.Core.Commands.List();
        if (list.Count == 0)
        {
            var vera = SkinArt.Full(_host.Core.Settings.Current.Skin, "wave", 120);
            var text = Ui.Text("Пока ни одной команды. Начни с простой: «Вера, работаем» → громкость 30% → открыть почту.", 14, "Fg");
            text.MaxWidth = 380;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(16, 0, 0, 0);
            _cards.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { vera, text }, Margin = new Thickness(0, 10, 0, 0) });
            return;
        }
        foreach (var c in list.OrderByDescending(c => c.Updated)) _cards.Children.Add(Card(c));
    }

    private Border Card(UserCommand c)
    {
        var name = Ui.Title(c.Name);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var lines = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var t in c.Triggers.Take(3))
        {
            var def = Blocks.Get(t.Type)!;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(CommandEditor.IconSquare(def, 24, 13));
            var txt = Ui.Text(def.Summary(t), 13, "Fg", wrap: false);
            txt.TextTrimming = TextTrimming.CharacterEllipsis;
            txt.MaxWidth = 210;
            txt.VerticalAlignment = VerticalAlignment.Center;
            txt.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(txt);
            lines.Children.Add(row);
        }
        if (!c.Triggers.Any()) lines.Children.Add(Ui.Muted("Нет блока запуска — только вручную", 12.5));
        var steps = c.Nodes.Count(n => Blocks.Get(n.Type)?.Category != BlockCategory.Start);
        var meta = Ui.Muted(L10n.F("Шагов: {0}", steps) + (c.AllowAi ? L10n.T(" · Вера может запускать сама") : "") + (c.Enabled ? "" : L10n.T(" · выключена")), 12);
        meta.Margin = new Thickness(0, 4, 0, 10);

        var run = Ui.Button("Запустить", "Btn.Ghost", "play", async () =>
        {
            var r = await _host.RunCommandAsync(c, null, "manual");
            _window.Toast(r.Ok ? L10n.F("«{0}»: {1}", c.Name, r.Message) : r.Message, !r.Ok);
        });
        var edit = Ui.Button("Изменить", "Btn.Primary", "edit", () => Edit(c));
        var del = Ui.IconButton("trash", "Удалить", () =>
        {
            if (!ConfirmDialog.Ask(_window, "Удалить команду?", L10n.F("«{0}» будет удалена.", c.Name), okText: "Удалить", danger: true)) return;
            _host.Core.Commands.Remove(c.Id);
        }, 36, 16);
        run.Margin = new Thickness(0, 0, 8, 0);
        edit.Margin = new Thickness(0, 0, 8, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { edit, run, del } };

        var card = new Border
        {
            Width = 300,
            Margin = new Thickness(0, 0, 14, 14),
            Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(2.5),
            BorderBrush = Brushes.Black,
            Effect = Anim.HardShadow(3),
            Opacity = c.Enabled ? 1 : 0.6,
            Child = new StackPanel { Children = { name, lines, meta, buttons } },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg2");
        return card;
    }
}
