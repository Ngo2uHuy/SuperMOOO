using SuperMOOO.App.Shell;
using SuperMOOO.Data.Sqlite;
using SuperMOOO.Infrastructure.Conventions;
using SuperMOOO.Infrastructure.Health;
using SuperMOOO.Infrastructure.Jobs;
using SuperMOOO.Infrastructure.Logging;
using SuperMOOO.Infrastructure.Scheduling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Wpf.Ui;

namespace SuperMOOO.App;

public static class AppHost
{
    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        var paths = AppPaths.Create(builder.Configuration["Paths:DataRoot"]);
        // Trước mọi thứ chạm DB/voices: áp bản khôi phục đã dựng ở lần chạy trước (nếu có).
        var restoreNote = SuperMOOO.Infrastructure.Operations.PendingRestore.TryApply(paths);
        paths.EnsureCreated();

        var (logger, ring) = SerilogSetup.Create(paths);
        if (restoreNote is not null) logger.Warning("{Restore}", restoreNote);
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(logger, dispose: true);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(ring);

        var assemblies = AssemblyCatalog.LoadAppAssemblies();
        builder.Services.AddSingleton(new AppAssemblies(assemblies));
        builder.Services.AddConventionServices([.. assemblies]);
        builder.Services.AddScheduledJobs([.. assemblies]);
        builder.Services.AddHostedService(sp => sp.GetRequiredService<JobQueue>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CacheJanitor>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<JobHistoryJanitor>());
        // Sau JobQueue: cần lane Publish đã khởi tạo để đổi số upload đồng thời.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Features.S16.PublishingRuntimeSettings>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DiskAlertNotifier>());
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<ISnackbarService, SnackbarService>();
        builder.Services.AddSingleton<IContentDialogService, ContentDialogService>();

        var host = builder.Build();
        host.Services.GetRequiredService<MigrationRunner>().Migrate();
        return host;
    }
}
