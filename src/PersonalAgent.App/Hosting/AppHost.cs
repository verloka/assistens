using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using PersonalAgent.App.ViewModels;
using PersonalAgent.Agent;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Persistence;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Security;
using PersonalAgent.Core.Channels;
using PersonalAgent.Persistence;
using PersonalAgent.Mcp;
using PersonalAgent.Computer;
using PersonalAgent.Channels.Telegram;
using System.IO;
using System.Security.Principal;

namespace PersonalAgent.App;

public static class AppHost
{
    public static string DefaultHistoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalAgent", "history.db");

    // A path opts into persistent history. Tests/diagnostics can compose an isolated memory-only host.
    public static IHostBuilder CreateBuilder(string[] args, string? historyDatabasePath = null) => Host.CreateDefaultBuilder(args)
        .UseContentRoot(AppContext.BaseDirectory)
        .ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole(options => options.FormatterName = PrivacyConsoleFormatter.FormatterName)
                .AddConsoleFormatter<PrivacyConsoleFormatter, ConsoleFormatterOptions>();
        })
        .ConfigureServices(services =>
        {
            services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
            services.AddSingleton<MainWindow>();
            services.AddSingleton<HealthViewModel>();
            services.AddSingleton<DashboardViewModel>();
            services.AddSingleton<ModelProfilesViewModel>();
            services.AddSingleton<McpConnectionsViewModel>();
            services.AddSingleton<ApprovalsViewModel>();
            services.AddSingleton<SchedulesViewModel>();
            services.AddSingleton<ChannelsViewModel>();
            services.AddSingleton<PrivacyViewModel>();
            services.AddSingleton<RunDiagnosticsViewModel>();
            services.AddSingleton<SetupWizardViewModel>();
            services.AddSingleton(provider => new SettingsViewModel(provider.GetService<SettingsTransfer>(), provider.GetRequiredService<ApprovalOwner>(),
                provider.GetRequiredService<IHostApplicationLifetime>(), provider.GetService<SqliteDatabaseMaintenance>()));
            services.AddSingleton<RunDiagnosticsService>();
            services.AddSingleton(provider => new TelegramConnectionProbe(provider.GetRequiredService<ISecretStore>(), provider.GetService<Func<System.Net.Http.HttpMessageHandler>>()?.Invoke()));
            services.AddSingleton(_ =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new ApprovalOwner("desktop", identity.User?.Value ?? throw new InvalidOperationException("The local owner identity is unavailable."));
            });
            services.AddSingleton<McpConnectionFactory>();
            services.AddSingleton<IMcpTransportProvider, StdioMcpTransportProvider>();
            services.AddSingleton<IMcpTransportProvider, HttpMcpTransportProvider>();
            services.AddSingleton<McpToolDiscovery>();
            services.AddSingleton<McpConnectionManager>();
            services.AddSingleton<ControlPanelViewModel>();
            services.AddSingleton<ControlWindow>();
            services.AddSingleton<ISystemIdleSource, WindowsSystemIdleSource>();
            services.AddSingleton<IDesktopAccessCoordinator>(provider => new DesktopAccessCoordinator(beforeAcquire:
                (id, ct) =>
                {
                    provider.GetRequiredService<UserActivityPauseService>().RequireAvailable();
                    provider.GetRequiredService<DesktopEnvironmentPauseService>().RequireAvailable();
                    return provider.GetService<ScheduledDesktopGate>()?.WaitAsync(id, ct) ?? Task.CompletedTask;
                }));
            services.AddSingleton<TaskRunCoordinator>();
            services.AddSingleton<ChannelMessageRouter>();
            services.AddSingleton<INotificationSubscriptionSource>(new FixedNotificationSubscriptions([]));
            services.AddSingleton<DesktopNotificationSink>();
            services.AddSingleton<IDesktopNotificationSink>(provider => provider.GetRequiredService<DesktopNotificationSink>());
            services.AddSingleton<ISecretStore>(_ => new WindowsDpapiSecretStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalAgent", "secrets")));
            services.AddSingleton<OpenAiModelProvider>();
            services.AddSingleton<AnthropicModelProvider>();
            services.AddSingleton<IModelProvider>(provider => provider.GetRequiredService<AnthropicModelProvider>());
            services.AddSingleton<IModelProvider>(provider => provider.GetRequiredService<OpenAiModelProvider>());
            services.AddSingleton<TakeControlHotkeyService>();
            services.AddSingleton<UserActivityPauseService>();
            services.AddSingleton<DesktopEnvironmentPauseService>();
            services.AddSingleton<ITaskCommands>(provider => provider.GetRequiredService<TaskRunCoordinator>());
            services.AddSingleton<FakeAgentRuntime>(provider => new FakeAgentRuntime(
                [new("Read local task", TimeSpan.FromSeconds(1)), new("Prepare local result", TimeSpan.FromSeconds(1))],
                provider.GetRequiredService<TaskRunCoordinator>().WaitAtBoundaryAsync));
            services.AddSingleton<IAgentRuntime>(provider => new ProfileAgentRuntime(provider.GetRequiredService<FakeAgentRuntime>(),
                provider.GetServices<IModelProvider>(), provider.GetRequiredService<TaskRunCoordinator>().WaitAtBoundaryAsync,
                provider.GetService<IConversationRepository>()));
            services.AddSingleton<TaskRunner>();
            services.AddHostedService<LifecycleLoggingService>();
            services.AddHostedService<McpLifecycleService>();
            if (historyDatabasePath is not null)
            {
                services.AddSingleton(new SqliteAgentDbContextFactory(historyDatabasePath));
                services.AddSingleton(provider => new SettingsTransfer(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<SqliteDatabaseMaintenance>();
                services.AddSingleton<IRunRepository>(provider => new SqliteRunRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<IConversationRepository>(provider => new SqliteConversationRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<IModelProfileRepository>(provider => new SqliteModelProfileRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<IMcpConnectionRepository>(provider => new SqliteMcpConnectionRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<IApprovalRepository>(provider => new SqliteApprovalRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<IChannelBindingRepository>(provider => new SqliteChannelBindingRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>(), provider.GetService<TimeProvider>()));
                services.AddSingleton<IChannelCursorRepository>(provider => new SqliteChannelCursorRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<IChannelActionRepository>(provider => new SqliteChannelActionRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<IChannelConfigurationRepository>(provider => new SqliteChannelConfigurationRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<IChannelConversationRepository>(provider => new SqliteChannelConversationRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<ChannelConversationCorrelator>();
                services.AddSingleton<INotificationSubscriptionSource, ConfiguredNotificationSubscriptions>();
                services.AddSingleton<ConfiguredChannelCatalog>();
                services.AddSingleton<IOutboundChannelCatalog>(provider => provider.GetRequiredService<ConfiguredChannelCatalog>());
                services.AddSingleton<ChannelApprovalPresenter>();
                services.AddSingleton<ChannelOwnershipPolicy>();
                services.AddSingleton<IChannelAccessPolicy>(provider => provider.GetRequiredService<ChannelOwnershipPolicy>());
                services.AddSingleton<ChannelOwnerCommands>();
                services.AddSingleton<ChannelActionHandler>();
                services.AddSingleton<IToolCallRepository>(provider => new SqliteToolCallRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<IScheduleRepository>(provider => new SqliteScheduleRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>(), provider.GetService<TimeProvider>()));
                services.AddHostedService<RunHistoryService>();
                services.AddSingleton<IHistoryRetentionRepository>(provider => new SqliteHistoryRetentionRepository(provider.GetRequiredService<SqliteAgentDbContextFactory>()));
                services.AddSingleton<HistoryRetentionService>();
                services.AddHostedService(provider => provider.GetRequiredService<HistoryRetentionService>());
                services.AddSingleton<INotificationOutbox>(provider => new SqliteNotificationOutbox(provider.GetRequiredService<SqliteAgentDbContextFactory>(), provider.GetService<TimeProvider>()));
                services.AddSingleton<NotificationDispatcher>();
                services.AddSingleton<IOutboundChannel>(provider => new DesktopOutboundChannel(provider.GetRequiredService<ApprovalOwner>().UserId,
                    new ConfiguredDesktopPolicy(provider.GetRequiredService<IChannelConfigurationRepository>(), provider.GetRequiredService<ApprovalOwner>(),
                        provider.GetRequiredService<ChannelOwnershipPolicy>()), provider.GetRequiredService<IDesktopNotificationSink>()));
                services.AddSingleton<RunNotificationPublisher>();
                services.AddHostedService<RunNotificationService>();
                services.AddHostedService<NotificationDeliveryService>();
                services.AddHostedService<ConfiguredTelegramService>();
                services.AddSingleton<ScheduleDispatcher>();
                services.AddSingleton<ScheduledDesktopGate>();
                services.AddHostedService<SchedulerService>();
            }
            services.AddHostedService(provider => provider.GetRequiredService<RunDiagnosticsService>());
            services.AddHostedService<TaskRunnerService>();
        });
}
