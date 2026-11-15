using Android.Content.Res;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using SkiaSharp.Views.Maui.Controls.Hosting;

namespace Vera.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>().UseSkiaSharp()
            .ConfigureFonts(f =>
            {
                f.AddFont("Onest-Regular.ttf", "Onest");
                f.AddFont("Onest-SemiBold.ttf", "OnestSemiBold");
                f.AddFont("Onest-Bold.ttf", "OnestBold");
                f.AddFont("Unbounded-Bold.ttf", "UnboundedBold");
                f.AddFont("Unbounded-Black.ttf", "UnboundedBlack");
            });

        // Тёмные поля ввода без «материального» подчёркивания Android
        var clear = ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
        EntryHandler.Mapper.AppendToMapping("VeraNoUnderline", (h, _) => h.PlatformView.BackgroundTintList = clear);
        EditorHandler.Mapper.AppendToMapping("VeraNoUnderline", (h, _) =>
        {
            h.PlatformView.BackgroundTintList = clear;
            h.PlatformView.SetPadding(h.PlatformView.PaddingLeft, h.PlatformView.PaddingTop / 2, h.PlatformView.PaddingRight, h.PlatformView.PaddingBottom / 2);
        });
        PickerHandler.Mapper.AppendToMapping("VeraNoUnderline", (h, _) => h.PlatformView.BackgroundTintList = clear);
        DatePickerHandler.Mapper.AppendToMapping("VeraNoUnderline", (h, _) => h.PlatformView.BackgroundTintList = clear);
        TimePickerHandler.Mapper.AppendToMapping("VeraNoUnderline", (h, _) => h.PlatformView.BackgroundTintList = clear);
        SearchBarHandler.Mapper.AppendToMapping("VeraDark", (h, _) =>
        {
            h.PlatformView.SetBackgroundColor(Android.Graphics.Color.Transparent);
        });

        return builder.Build();
    }
}
