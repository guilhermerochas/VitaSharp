using MauiReactor;
using VitaSharp.Android.Pages;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Util;

namespace VitaSharp.Android;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiReactorApp<RootPage>(app => app.UseTheme<VitaTheme>(),
                unhandled => Log.Error($"Unhandled UI exception: {unhandled.ExceptionObject}"))
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("Inter-Regular.ttf", Fonts.Inter);
                fonts.AddFont("Inter-Medium.ttf", Fonts.InterMedium);
                fonts.AddFont("Inter-SemiBold.ttf", Fonts.InterSemiBold);
                fonts.AddFont("Sora-SemiBold.ttf", Fonts.SoraSemiBold);
                fonts.AddFont("Sora-Bold.ttf", Fonts.SoraBold);
            });
        return builder.Build();
    }
}
