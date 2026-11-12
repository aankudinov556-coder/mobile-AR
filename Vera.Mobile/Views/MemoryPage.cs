using Vera.Core;

namespace Vera.Mobile.Views;

/// <summary>Долгая память: факты о пользователе — список, правка, удаление, очистка.</summary>
public sealed class MemoryPage : ContentPage
{
    private VeraCore Core => App.Core;
    private readonly Entry _text;
    private readonly VerticalStackLayout _list = new() { Spacing = 8 };
    private readonly Label _title = Ui.Label("Что Вера знает", 15, Ui.Text, bold: true);

    public MemoryPage()
    {
        Ui.StylePage(this, "Память");
        _text = Ui.Entry("Например: Меня зовут Артём, я учусь на программиста");
        _text.MaxLength = 400;
        _text.ReturnType = ReturnType.Done;
        _text.Completed += (_, _) => Add();

        var addRow = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
            ColumnSpacing = 8,
        };
        addRow.Add(Ui.Box(_text), 0);
        var addBtn = new ImageButton { Source = "ic_plus.png", WidthRequest = 44, HeightRequest = 44, CornerRadius = 12, Padding = new Thickness(11), Background = Ui.AccentGradient };
        addBtn.Clicked += (_, _) => Add();
        addRow.Add(addBtn, 1);

        var clear = Ui.Ghost("Забыть всё", async (_, _) =>
        {
            if (Core.Memory.List().Count == 0) return;
            if (!await DisplayAlertAsync("Забыть всё?", "Вера забудет всё, что знает о тебе. Точно?", "Забыть", "Отмена")) return;
            Core.Memory.Clear();
        }, Ui.Danger);
        var titleRow = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        _title.VerticalOptions = LayoutOptions.Center;
        titleRow.Add(_title, 0);
        titleRow.Add(clear, 1);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 14,
                Padding = new Thickness(14, 12, 14, 28),
                Children =
                {
                    Ui.Card(
                        Ui.H3("Долгая память"),
                        Ui.MutedLabel("Факты, которые Вера помнит о тебе между разговорами. Она добавляет их сама, но можно и вручную."),
                        addRow),
                    titleRow,
                    _list,
                },
            },
        };
    }

    private void OnChanged() => MainThread.BeginInvokeOnMainThread(Render);

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Core.Memory.Changed += OnChanged;
        Render();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Core.Memory.Changed -= OnChanged;
    }

    private void Add()
    {
        var t = (_text.Text ?? "").Trim();
        if (t.Length == 0) return;
        try
        {
            Core.Memory.Add(t, "user");
            _text.Text = "";
            Ui.Toast("Запомнила~");
        }
        catch (Exception e) { Ui.Toast(e.Message, true); }
    }

    private void Render()
    {
        var facts = Core.Memory.List();
        _title.Text = facts.Count > 0 ? $"Что Вера знает ({facts.Count})" : "Что Вера знает";
        _list.Children.Clear();
        if (facts.Count == 0) _list.Children.Add(Ui.MutedLabel("Вера пока ничего о тебе не знает. Расскажи ей о себе!", 14));
        foreach (var m in facts.Reverse()) _list.Children.Add(Item(m));
    }

    private View Item(MemoryFact m)
    {
        var sub = new HorizontalStackLayout { Spacing = 6 };
        sub.Children.Add(Ui.Tag(m.Created.ToLocalTime().ToString("d MMM yyyy", new System.Globalization.CultureInfo("ru-RU"))));
        if (m.Source == "ai") sub.Children.Add(Ui.Tag("запомнила сама", Ui.AccentSoft, Color.FromArgb("#f6c4e0")));
        var actions = new HorizontalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                Ui.Icon("ic_edit.png", async (_, _) =>
                {
                    var t = await DisplayPromptAsync("Изменить факт", "", "Сохранить", "Отмена", initialValue: m.Text, maxLength: 400);
                    if (string.IsNullOrWhiteSpace(t) || t.Trim() == m.Text) return;
                    Core.Memory.Update(m.Id, t);
                }, 40, 9),
                Ui.Icon("ic_trash.png", (_, _) => Core.Memory.Remove(m.Id), 40, 9),
            },
        };
        var g = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 6 };
        g.Add(new VerticalStackLayout { Spacing = 6, Children = { Ui.Label(m.Text, 15), sub } }, 0);
        g.Add(actions, 1);
        return new Border
        {
            BackgroundColor = Ui.Surface,
            Stroke = Ui.Stroke,
            StrokeThickness = 1,
            StrokeShape = Ui.Round(14),
            Padding = new Thickness(14, 10, 6, 10),
            Content = g,
        };
    }
}
