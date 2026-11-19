using System.Reflection;
using LANCommander.SDK.Events;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using LANCommander.Server.Plugins.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

[assembly: LANCommanderPlugin(typeof(LANCommander.Community.Server.DiscordWebhook.DiscordPlugin),
    Id = LANCommander.Community.Server.DiscordWebhook.DiscordPlugin.PluginId,
    MinHostVersion = "2.2.0-beta.4", Hosts = PluginHost.Server)]

namespace LANCommander.Community.Server.DiscordWebhook;

public sealed class DiscordPlugin : IPlugin
{
    public const string PluginId = "io.github.lancommander.community.discordwebhook";
    public string Id => PluginId;
    public string Name => "Discord webhooks";
    public string Author => "LANCommander Community contributors";
    public string Version => typeof(DiscordPlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddDataProtection();
        services.AddSingleton<IServerRouteAssemblyExtension>(new DiscordRoutes(typeof(DiscordPlugin).Assembly));
        services.AddSingleton<IServerNavigationExtension, DiscordNavigation>();
        services.AddSingleton<DiscordStore>();
        services.AddSingleton(_ => new DiscordWebhookClient());
        services.AddSingleton<IServerEventConsumer, DiscordEventConsumer>();
        services.AddHostedService<DiscordDeliveryWorker>();
        services.AddScoped<DiscordAdministration>();
    }

    public Task InitializeAsync(PluginContext context, CancellationToken cancellationToken) =>
        context.Services.GetRequiredService<DiscordStore>().InitializeAsync(context.DataDirectory, cancellationToken);
}

internal sealed record DiscordRoutes(Assembly Assembly) : IServerRouteAssemblyExtension;

internal sealed class DiscordNavigation : IServerNavigationExtension
{
    public string Id => DiscordPlugin.PluginId;
    public string Label => "Discord webhooks";
    public string Href => "/Plugins/DiscordWebhooks";
    public string? Icon => "ChatCircle";
    public int Order => 120;
    public PluginAccessPolicy Access => PluginAccessPolicy.Administrator;
}

public sealed class DiscordEventConsumer(DiscordStore store, ILogger<DiscordEventConsumer> logger) : IServerEventConsumer
{
    public async Task HandleAsync(ServerEvent serverEvent, CancellationToken cancellationToken)
    {
        try { await store.EnqueueAsync(serverEvent, null, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (DiscordStateException)
        {
            logger.LogError("Discord event enqueue failed; notification may be lost. Check Discord administration storage health.");
            throw;
        }
    }
}

internal sealed class DiscordDeliveryWorker(DiscordStore store, DiscordWebhookClient client,
    ILogger<DiscordDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.Ready.WaitAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await store.DeliverNextAsync(client, DateTimeOffset.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is DiscordStateException or ArgumentException or System.Security.Cryptography.CryptographicException)
            {
                // Never pass exceptions to the logger: the source may contain credentials.
                logger.LogError("Discord delivery processing failed ({FailureType}); check storage and data-protection keys.", ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
