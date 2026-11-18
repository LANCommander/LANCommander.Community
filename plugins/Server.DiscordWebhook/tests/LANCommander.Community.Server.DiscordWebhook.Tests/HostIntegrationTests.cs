using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using Bunit;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using LANCommander.Server.Plugins.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using DiscordPage = LANCommander.Community.Server.DiscordWebhook.Pages.Index;

namespace LANCommander.Community.Server.DiscordWebhook.Tests;

public sealed class HostIntegrationTests
{
    [Fact]
    public async Task AdminPageCreatesEditsTestsAndDeletesWithoutRenderingSavedSecret()
    {
        using var fixture = new StoreFixture();
        await using var context = new BunitContext();
        context.Services.AddLogging();
        context.Services.AddSingleton(fixture.Store);
        context.Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication(true, "Administrator"));
        context.Services.AddScoped<DiscordAdministration>();
        var page = context.Render<DiscordPage>();
        page.WaitForAssertion(() => Assert.Contains("No Discord subscriptions configured", page.Markup));
        Button(page, "New subscription").Click();
        page.Find("#discord-name").Change("Release alerts");
        page.Find("#discord-url").Change(StoreFixture.Endpoint);
        var eventBox = page.FindAll("label").Single(label => label.TextContent.Trim() == "server.game.created")
            .QuerySelector("input")!;
        eventBox.Change(true);
        Button(page, "Save subscription").Click();
        page.WaitForAssertion(() => Assert.Contains("Subscription saved", page.Markup));
        Assert.DoesNotContain("synthetic-token", page.Markup);
        Assert.Empty(page.FindAll("iframe"));
        Button(page, "Edit").Click();
        Assert.Equal("", page.Find("#discord-url").GetAttribute("value") ?? "");
        Assert.Equal("password", page.Find("#discord-url").GetAttribute("type"));
        page.Find("#discord-name").Change("Updated alerts");
        Button(page, "Save subscription").Click();
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Updated alerts", page.Markup);
            Assert.Empty(page.FindAll("#discord-name"));
            Assert.False(Button(page, "Send test").HasAttribute("disabled"));
        });
        Button(page, "Send test").Click();
        page.WaitForAssertion(() => Assert.Contains("Synthetic test queued", page.Markup));
        Assert.Single((await fixture.Store.GetAsync()).Deliveries);
        Button(page, "Delete").Click();
        Assert.Contains("Confirm delete", page.Markup);
        Button(page, "Confirm delete").Click();
        page.WaitForAssertion(() => Assert.Contains("No Discord subscriptions configured", page.Markup));
        Assert.Empty((await fixture.Store.GetAsync()).Deliveries);
    }

    [Fact]
    public async Task ValidationErrorsAreVisibleAndDoNotEchoSubmittedSecret()
    {
        using var fixture = new StoreFixture();
        await using var context = new BunitContext();
        context.Services.AddLogging();
        context.Services.AddSingleton(fixture.Store);
        context.Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication(true, "Administrator"));
        context.Services.AddScoped<DiscordAdministration>();
        var page = context.Render<DiscordPage>();
        Button(page, "New subscription").Click();
        page.Find("#discord-name").Change("Bad URL");
        page.Find("#discord-url").Change("https://evil.example/synthetic-token");
        page.FindAll("label").Single(label => label.TextContent.Trim() == "server.game.created")
            .QuerySelector("input")!.Change(true);
        Button(page, "Save subscription").Click();
        page.WaitForAssertion(() => Assert.Contains("Operation failed", page.Markup));
        Assert.DoesNotContain("evil.example", page.Markup);
        Assert.DoesNotContain("synthetic-token", page.Markup);
        Assert.Empty((await fixture.Store.GetAsync()).Subscriptions);
    }

    [Fact]
    public async Task PageDoesNotExposeSubscriptionsWhenAuthorizationFailsEvenWithoutRouter()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.SaveAsync(StoreFixture.Edit() with { Name = "Private administration" }, null);
        await using var context = new BunitContext();
        context.Services.AddLogging();
        context.Services.AddSingleton(fixture.Store);
        context.Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication(true, "User"));
        context.Services.AddScoped<DiscordAdministration>();
        var page = context.Render<DiscordPage>();
        page.WaitForAssertion(() => Assert.Contains("administrator access is required", page.Markup));
        Assert.DoesNotContain("Private administration", page.Markup);
        Assert.DoesNotContain("synthetic-token", page.Markup);
    }

    [Fact]
    public async Task PluginRegistersFullStreamConsumerAndInitializesHostProvidedDataDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "discord-registration-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        var plugin = new DiscordPlugin();
        plugin.ConfigureServices(services);
        await using (var provider = services.BuildServiceProvider())
        {
            await plugin.InitializeAsync(new PluginContext
            {
                Services = provider, DataDirectory = root, Host = PluginHost.Server,
                Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
            }, default);
            var store = provider.GetRequiredService<DiscordStore>();
            await store.SaveAsync(StoreFixture.Edit(), null);
            var consumer = Assert.Single(provider.GetServices<IServerEventConsumer>());
            Assert.IsType<DiscordEventConsumer>(consumer);
            await consumer.HandleAsync(StoreFixture.Event(), default);
            Assert.Single((await store.GetAsync()).Deliveries);
            Assert.Single(provider.GetServices<IHostedService>(), service => service is DiscordDeliveryWorker);
            Assert.Single(provider.GetServices<IServerRouteAssemblyExtension>());
        }
        foreach (var file in Directory.GetFiles(root)) File.Delete(file);
        Directory.Delete(root);
    }

    [Fact]
    public async Task HostedWorkerDeliversAcceptedWorkAndStopsOnCancellation()
    {
        using var fixture = new StoreFixture();
        await fixture.QueueAsync();
        using var client = StoreFixture.Client((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        using var worker = new DiscordDeliveryWorker(fixture.Store, client,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DiscordDeliveryWorker>.Instance);
        await worker.StartAsync(default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (Assert.Single((await fixture.Store.GetAsync(timeout.Token)).Deliveries).Status != "Delivered")
                await Task.Delay(20, timeout.Token);
        }
        finally { await worker.StopAsync(default); }
        Assert.True(worker.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public void PackagedPluginLoadsWithSharedHostContractsAndAdminPageDiscovery()
    {
        var context = new SharedHostLoader();
        try
        {
            var package = Environment.GetEnvironmentVariable("DISCORD_TEST_PACKAGE");
            using var archive = package is null ? null : ZipFile.OpenRead(package);
            using var stream = archive is null
                ? File.OpenRead(typeof(DiscordPlugin).Assembly.Location)
                : archive.GetEntry("lib/net10.0/LANCommander.Community.Server.DiscordWebhook.dll")!.Open();
            using var extracted = new MemoryStream();
            stream.CopyTo(extracted);
            extracted.Position = 0;
            var assembly = context.LoadFromStream(extracted);
            var attribute = assembly.GetCustomAttribute<LANCommanderPluginAttribute>()!;
            Assert.Equal(DiscordPlugin.PluginId, attribute.Id);
            Assert.Equal("2.2.0-beta.4", attribute.MinHostVersion);
            Assert.Equal(PluginHost.Server, attribute.Hosts);
            var type = Assert.Single(assembly.GetTypes(), t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract);
            var plugin = Assert.IsAssignableFrom<IPlugin>(Activator.CreateInstance(type));
            Assert.Equal("Discord webhooks", plugin.Name);
            var page = Assert.Single(assembly.GetTypes(), t => t.GetCustomAttribute<RouteAttribute>()?.Template == "/Plugins/DiscordWebhooks");
            Assert.Equal(PluginAccessPolicy.Administrator, page.GetCustomAttribute<PluginAccessAttribute>()!.Policy);
            var services = new ServiceCollection();
            plugin.ConfigureServices(services);
            using var provider = services.BuildServiceProvider();
            var nav = Assert.Single(provider.GetServices<IServerNavigationExtension>());
            Assert.Equal(PluginAccessPolicy.Administrator, nav.Access);
            Assert.Equal("/Plugins/DiscordWebhooks", nav.Href);
            Assert.Equal(nameof(LANCommander.Server.UI.Controls.IconType.ChatCircle), nav.Icon);
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IServerEventConsumer));
            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IServerEventTransport));
        }
        finally { context.Unload(); }
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<DiscordPage> page, string text) =>
        page.FindAll("button").Single(button => button.TextContent.Trim() == text);

    private sealed class SharedHostLoader() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => Default.LoadFromAssemblyName(assemblyName);
    }
}
