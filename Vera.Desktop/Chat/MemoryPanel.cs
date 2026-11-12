using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vera.Core;
using Vera.Desktop.UI;

namespace Vera.Desktop.Chat;

/// <summary>Вкладка «Память»: факты о пользователе — добавить, исправить, забыть.</summary>
public sealed class MemoryPanel : Grid
{
    private readonly AppHost _host;
    private readonly ChatWindow _window;
    private readonly TextBox _new;
    private readonly StackPanel _list = new();
    private readonly TextBlock _count;
    private string? _editing;

    public MemoryPanel(AppHost host, ChatWindow window)
    {
        _host = host;
        _window = window;

        _new = new TextBox { Tag = "Например: Меня зовут Артём, я учусь на программиста", MaxLength = 400 };
        _new.KeyDown += (_, e) => { if (e.Key == Key.Enter) Add(); };
        var addBtn = Ui.Button(null, "Btn.Primary", "plus", Add, "Добавить");
        addBtn.Padding = new Thickness(10, 8, 10, 8);
        var desc = Ui.Muted("Факты, которые Вера помнит о тебе между разговорами. Она добавляет их сама, но можно и вручную.");
        desc.Margin = new Thickness(0, 4, 0, 10);
        var card = Ui.Card(Ui.Title("Долгая память"), desc, Ui.Row(new UIElement[] { _new, addBtn }, 8, 0));

        _count = Ui.Muted("", 14);
        var titleText = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleText.Children.Add(Ui.Text("Что Вера знает ", 14, "Fg", FontWeights.Bold));
        titleText.Children.Add(_count);
        var clear = Ui.Button("Забыть всё", "Btn.GhostSmall", null, ClearAll);
        clear.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Err");
        var title = Ui.Row(new UIElement[] { titleText, clear }, 8, 0);
        title.Margin = new Thickness(2, 6, 2, 8);

        var content = new StackPanel();
        content.Children.Add(card);
        content.Children.Add(title);
        content.Children.Add(_list);
        Children.Add(Ui.Scroll(content));
        Refresh();
    }

    private void Add()
    {
        var t = _new.Text.Trim();
        if (t.Length == 0) return;
        try
        {
            _host.Core.Memory.Add(t, "user");
            _new.Clear();
        }
        catch (Exception e)
        {
            _window.Toast(e.Message, true);
        }
        Refresh();
    }

    private void ClearAll()
    {
        if (!ConfirmDialog.Ask(_window, "Забыть всё?", "Вера забудет всё, что знает о тебе. Точно?", okText: "Забыть всё", danger: true)) return;
        _host.Core.Memory.Clear();
        Refresh();
    }

    public void Refresh()
    {
        var facts = _host.Core.Memory.List();
        _count.Text = facts.Count > 0 ? $"({facts.Count})" : "";
        _list.Children.Clear();
        foreach (var m in facts.Reverse()) _list.Children.Add(Item(m));
        if (facts.Count == 0) _list.Children.Add(RemindersPanel.EmptyNote("Вера пока ничего о тебе не знает. Расскажи ей о себе!"));
    }

    private UIElement Item(MemoryFact m)
    {
        var id = m.Id;
        var editing = _editing == id;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        TextBox? editor = null;
        if (editing)
        {
            editor = new TextBox { Text = m.Text, MaxLength = 400, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
            editor.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) Save(id, editor.Text);
                else if (e.Key == Key.Escape) { _editing = null; Refresh(); }
            };
            editor.Loaded += (_, _) => { editor.Focus(); editor.SelectAll(); };
            text.Children.Add(editor);
        }
        else
        {
            var t = Ui.Text(m.Text, 14, "Fg", FontWeights.SemiBold);
            t.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { _editing = id; Refresh(); } };
            text.Children.Add(t);
        }
        var sub = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        var date = Ui.Muted(m.Created.ToLocalTime().ToString("dd.MM.yyyy"));
        date.Margin = new Thickness(0, 0, 8, 0);
        sub.Children.Add(date);
        if (m.Source == "ai") sub.Children.Add(RemindersPanel.MakeTag("запомнила сама"));
        text.Children.Add(sub);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Ui.IconButton(editing ? "check" : "edit", editing ? "Сохранить" : "Изменить", () =>
        {
            if (editing && editor is not null) Save(id, editor.Text);
            else { _editing = id; Refresh(); }
        }, 30, 16));
        actions.Children.Add(Ui.IconButton("trash", "Забыть", () =>
        {
            _host.Core.Memory.Remove(id);
            Refresh();
        }, 30, 16));
        return RemindersPanel.ItemFrame(Ui.Row(new UIElement[] { text, actions }, 10, 0));
    }

    private void Save(string id, string text)
    {
        _editing = null;
        try { if (!string.IsNullOrWhiteSpace(text)) _host.Core.Memory.Update(id, text); }
        catch (Exception e) { _window.Toast(e.Message, true); }
        Refresh();
    }
}
