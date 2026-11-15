using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QRCoder;
using Vera.Core;
using Vera.Core.Sync;
using Vera.Desktop.UI;

namespace Vera.Desktop.Chat;

/// <summary>Окно привязки телефона: QR-код (адрес ПК + одноразовый код), обратный отсчёт, «Готово», когда телефон подключился.</summary>
public static class PairingDialog
{
    public static void Show(Window owner, VeraCore core, PairingOffer offer)
    {
        var before = core.Sync.Peers.Select(p => p.Id).ToHashSet();
        var w = new Window
        {
            Owner = owner,
            Title = "Привязать телефон",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = Ui.Font,
            ShowInTaskbar = false,
        };
        w.SetResourceReference(Window.BackgroundProperty, "Bg");
        w.SetResourceReference(Window.ForegroundProperty, "Fg");

        var qr = new Image { Width = 260, Height = 260, Source = Qr(offer.Uri), Margin = new Thickness(0, 12, 0, 12) };
        RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
        var qrFrame = new Border { Background = Brushes.White, Padding = new Thickness(10), CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center, Child = qr };
        qrFrame.SetResourceReference(Border.BorderBrushProperty, "Accent");
        qrFrame.BorderThickness = new Thickness(3);
        var steps = Ui.Text("1. Открой Веру на телефоне → Настройки → Синхронизация.\n2. Нажми «Привязать ПК» и наведи камеру на этот код.\n3. Телефон и ПК должны быть в одной домашней сети.", 13);
        steps.LineHeight = 20;
        var timer = Ui.Muted("", 12);
        var fp = Ui.Muted("Отпечаток ПК: " + offer.Code, 11);
        var done = Ui.Text("", 14, "Ok", FontWeights.Bold);
        var close = Ui.Button("Закрыть", "Btn.Ghost", null, w.Close);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 12, 0, 0);

        var sp = new StackPanel { Margin = new Thickness(20) };
        sp.Children.Add(Ui.Title("📱 Привязать телефон"));
        sp.Children.Add(qrFrame);
        sp.Children.Add(steps);
        sp.Children.Add(timer);
        sp.Children.Add(fp);
        sp.Children.Add(done);
        sp.Children.Add(close);
        w.Content = sp;

        var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += (_, _) =>
        {
            var left = offer.Expires - DateTimeOffset.Now;
            timer.Text = left > TimeSpan.Zero ? $"Код действует ещё {left:mm\\:ss}" : "Код устарел — закрой окно и нажми «Привязать» снова.";
        };
        tick.Start();
        void OnChanged() => w.Dispatcher.BeginInvoke(() =>
        {
            var added = core.Sync.Peers.FirstOrDefault(p => !before.Contains(p.Id));
            if (added is null) return;
            done.Text = $"✓ «{added.Name}» привязан! Данные синхронизируются.";
            qrFrame.Opacity = 0.3;
            tick.Stop();
            timer.Text = "";
        });
        core.Sync.Changed += OnChanged;
        w.Closed += (_, _) =>
        {
            tick.Stop();
            core.Sync.Changed -= OnChanged;
            core.Sync.CancelPairing();
        };
        w.ShowDialog();
    }

    private static BitmapSource? Qr(string text)
    {
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(8);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(png);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e)
        {
            Log.Error("qr", e);
            return null;
        }
    }
}
