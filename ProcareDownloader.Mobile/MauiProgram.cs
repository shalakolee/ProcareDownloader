using Microsoft.Extensions.Logging;
using ProcareDownloader.Mobile.Services;
using ProcareDownloader.Mobile.ViewModels;
using ProcareDownloader.Services;

namespace ProcareDownloader.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<MobileProcareApiService>();
        builder.Services.AddSingleton<MobileImageCacheService>();
        builder.Services.AddSingleton<MobilePhotoMetadataCacheService>();
        builder.Services.AddSingleton<MobileMediaSaveService>();
        builder.Services.AddSingleton<DownloadHistoryService>();
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<MobileDownloadService>();
        builder.Services.AddSingleton<MainPageViewModel>();
        builder.Services.AddSingleton<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
