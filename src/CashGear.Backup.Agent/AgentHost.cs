using System.Globalization;
using System.IO.Pipes;
using CashGear.Backup.Agent.Api;
using CashGear.Backup.Agent.Destinations;
using CashGear.Backup.Agent.Engine;
using CashGear.Backup.Agent.Hosting;
using CashGear.Backup.Agent.Jobs;
using CashGear.Backup.Agent.Notifications;
using CashGear.Backup.Agent.Runs;
using CashGear.Backup.Agent.Scheduling;
using CashGear.Backup.Agent.Security;
using CashGear.Backup.Core;
using CashGear.Backup.Data;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting.WindowsServices;
using Quartz;
using Serilog;
using Serilog.Events;

namespace CashGear.Backup.Agent;

/// <summary>Builds the agent: gRPC on a named pipe only (no TCP port), state.db, scheduler, engine, logging.</summary>
public static class AgentHost
{
    public static WebApplication Build(string[] args, Action<AgentOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
        });

        var options = builder.Configuration.GetSection(AgentOptions.Section).Get<AgentOptions>() ?? new AgentOptions();
        configure?.Invoke(options);
        BackupEngineException.ThrowIfInvalid(options.Validate(), "Agent configuration");

        builder.Services.AddWindowsService(o => o.ServiceName = AgentInfo.ServiceName);
        ConfigureLogging(builder, options);
        ConfigureNamedPipe(builder, options);

        builder.Services.AddGrpc(o =>
        {
            o.Interceptors.Add<ErrorInterceptor>();
            o.EnableDetailedErrors = false;
            o.MaxReceiveMessageSize = 1024 * 1024;
        });

        builder.Services.AddDbContextFactory<StateDbContext>(o => o.UseStateDb(options.StateDbPath));

        // In-memory store: state.db holds the schedules, and the scheduler is rebuilt from it at every start.
        // The scheduler name is per pipe so several agents can share a process (tests).
        builder.Services.AddQuartz(q =>
        {
            q.SchedulerName = "CashGearBackup-" + options.PipeName;
            q.SchedulerId = "CashGearBackup-" + options.PipeName;
            q.UseInMemoryStore();
            q.UseDefaultThreadPool(t => t.MaxConcurrency = 4);
        });

        builder.Services.AddBundledBackupEngine();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        builder.Services.AddSingleton<CallerAuthorization>();
        builder.Services.AddSingleton<AgentState>();
        builder.Services.AddSingleton<EngineHost>();
        builder.Services.AddSingleton<RunCoordinator>();
        builder.Services.AddSingleton<JobScheduler>();
        builder.Services.AddSingleton<JobService>();
        builder.Services.AddSingleton<DestinationService>();
        builder.Services.AddSingleton<Licensing.ILicenceServer, Licensing.HttpLicenceServer>();
        builder.Services.AddSingleton<Licensing.LicenceService>();
        builder.Services.AddSingleton<SettingsStore>();
        builder.Services.AddSingleton<AlertService>();
        builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
        builder.Services.AddSingleton<Housekeeping>();
        builder.Services.AddHostedService<AgentLifecycle>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Housekeeping>());

        var app = builder.Build();
        app.MapGrpcService<BackupAgentService>();
        return app;
    }

    private static void ConfigureNamedPipe(WebApplicationBuilder builder, AgentOptions options)
    {
        builder.WebHost.UseNamedPipes(o =>
        {
            // Kestrel defaults to CurrentUserOnly, which would replace our ACL (and cannot be combined with it).
            o.CurrentUserOnly = false;
            o.PipeSecurity = PipeAccess.CreateSecurity();
            var first = 1;
            o.CreateNamedPipeServerStream = context =>
            {
                // The first instance must be ours: if another process already holds the name (pipe squatting),
                // creating it fails and the agent does not start rather than share the name.
                var pipeOptions = context.PipeOptions & ~PipeOptions.CurrentUserOnly;
                if (Interlocked.Exchange(ref first, 0) == 1)
                {
                    pipeOptions |= PipeOptions.FirstPipeInstance;
                }

                return NamedPipeServerStreamAcl.Create(context.NamedPipeEndPoint.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, pipeOptions, 0, 0, context.PipeSecurity);
            };
        });

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.ListenNamedPipe(options.PipeName, l => l.Protocols = HttpProtocols.Http2);
        });
    }

    private static void ConfigureLogging(WebApplicationBuilder builder, AgentOptions options)
    {
        builder.Services.AddSerilog((_, logger) =>
        {
            logger.MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                .MinimumLevel.Override("Grpc", LogEventLevel.Warning)
                .MinimumLevel.Override("Quartz", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Destructure.With<SecretMaskingPolicy>()
                .WriteTo.File(
                    Path.Combine(options.LogDirectory, "agent-.log"),
                    formatProvider: CultureInfo.InvariantCulture,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: 20 * 1024 * 1024,
                    rollOnFileSizeLimit: true);

            if (WindowsServiceHelpers.IsWindowsService())
            {
                logger.WriteTo.EventLog(AgentInfo.EventLogSource, manageEventSource: false, restrictedToMinimumLevel: LogEventLevel.Warning, formatProvider: CultureInfo.InvariantCulture);
            }
            else if (options.WriteToConsole)
            {
                logger.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);
            }
        }, preserveStaticLogger: true);
    }
}
