using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace Vera.Desktop.Platform;

/// <summary>Пункт меню трея (null-Label — разделитель).</summary>
public sealed record TrayMenuItem(string? Label, Action? Click = null, bool? Checked = null)
{
    public static TrayMenuItem Separator => new(null);
}

/// <summary>Иконка в трее (WinForms NotifyIcon) с тёмным/светлым меню.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Func<IEnumerable<TrayMenuItem>> _build;

    public event Action? Click;
    public event Action? DoubleClick;

    public TrayIcon(Func<IEnumerable<TrayMenuItem>> buildMenu)
    {
        _build = buildMenu;
        _menu = new WinForms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        _menu.Opening += (_, _) => Rebuild();
        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Вера — ИИ ассистент",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) Click?.Invoke(); };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) DoubleClick?.Invoke(); };
        ApplyTheme(true);
    }

    private static Icon LoadIcon()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/assets/icon.ico"));
            if (res is not null)
            {
                using var s = res.Stream;
                return new Icon(s, WinForms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception) { }
        return SystemIcons.Application;
    }

    public void ApplyTheme(bool dark)
    {
        _menu.Renderer = new MenuRenderer(dark);
        _menu.BackColor = dark ? Color.FromArgb(0x1d, 0x1a, 0x27) : Color.White;
        _menu.ForeColor = dark ? Color.FromArgb(0xec, 0xe9, 0xf5) : Color.FromArgb(0x22, 0x1d, 0x2e);
        _menu.Font = new Font("Segoe UI", 9.5f);
    }

    private void Rebuild()
    {
        _menu.Items.Clear();
        foreach (var it in _build())
        {
            if (it.Label is null)
            {
                _menu.Items.Add(new WinForms.ToolStripSeparator());
                continue;
            }
            var mi = new WinForms.ToolStripMenuItem(it.Label) { Padding = new WinForms.Padding(4, 3, 4, 3) };
            if (it.Checked is { } c) mi.Checked = c;
            var action = it.Click;
            mi.Click += (_, _) => action?.Invoke();
            _menu.Items.Add(mi);
        }
    }

    /// <summary>Запасной способ уведомить, если toast недоступен.</summary>
    public void Balloon(string title, string text)
    {
        try { _icon.ShowBalloonTip(8000, title, text, WinForms.ToolTipIcon.Info); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    private sealed class MenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        private readonly bool _dark;

        public MenuRenderer(bool dark) : base(new Colors(dark))
        {
            _dark = dark;
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = _dark ? Color.FromArgb(0xec, 0xe9, 0xf5) : Color.FromArgb(0x22, 0x1d, 0x2e);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(0xe0, 0x47, 0x9a), 2f);
            var cx = r.Left + r.Width / 2f;
            var cy = r.Top + r.Height / 2f;
            g.DrawLines(pen, new[] { new PointF(cx - 5, cy), new PointF(cx - 1.5f, cy + 3.5f), new PointF(cx + 5, cy - 4) });
        }
    }

    private sealed class Colors : WinForms.ProfessionalColorTable
    {
        private readonly bool _dark;
        public Colors(bool dark) { _dark = dark; UseSystemColors = false; }

        private Color Bg => _dark ? Color.FromArgb(0x1d, 0x1a, 0x27) : Color.White;
        private Color Hover => _dark ? Color.FromArgb(0x3a, 0x22, 0x36) : Color.FromArgb(0xfb, 0xe6, 0xf1);
        private Color Line => _dark ? Color.FromArgb(0x2f, 0x2a, 0x3d) : Color.FromArgb(0xe1, 0xdc, 0xea);

        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
        public override Color CheckBackground => Bg;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
