using System.Net;
using System.Security.Claims;
using System.Text.Json;
using LANCommander.SDK.Events;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LANCommander.Community.Server.DiscordWebhook.Tests;

public sealed class SubscriptionTests
{
    [Fact]
    public async Task CrudPersistsAcrossRestartWithoutReturningOrStoringPlaintextEndpoint()
    {
        using var fixture = new StoreFixture();
        var id = await fixture.Store.SaveAsync(StoreFixture.Edit(), null);
        var snapshot = await fixture.Store.GetAsync();
        var view = Assert.Single(snapshot.Subscriptions);
        Assert.Equal(id, view.Id);
        Assert.Equal(1, view.Revision);
        Assert.DoesNotContain("synthetic-token", JsonSerializer.Serialize(snapshot));
        Assert.DoesNotContain("synthetic-token", await File.ReadAllTextAsync(fixture.StatePath));
        fixture.Restart();
        Assert.Equal(id, Assert.Single((await fixture.Store.GetAsync()).Subscriptions).Id);
        await fixture.Store.SaveAsync(StoreFixture.Edit(id, url: "") with { Name = "Renamed", Enabled = false }, view.Revision);
        view = Assert.Single((await fixture.Store.GetAsync()).Subscriptions);
        Assert.Equal(2, view.Revision);
        Assert.False(view.Enabled);
        await fixture.Store.DeleteAsync(id, view.Revision);
        Assert.Empty((await fixture.Store.GetAsync()).Subscriptions);
    }

    [Theory]
    [InlineData("", 10, 3)]
    [InlineData("Name", 0, 3)]
    [InlineData("Name", 31, 3)]
    [InlineData("Name", 10, 0)]
    [InlineData("Name", 10, 6)]
    public async Task InvalidSubscriptionSettingsDoNotCommit(string name, int timeout, int attempts)
    {
        using var fixture = new StoreFixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.SaveAsync(
            StoreFixture.Edit() with { Name = name, TimeoutSeconds = timeout, MaxAttempts = attempts }, null));
        Assert.Empty((await fixture.Store.GetAsync()).Subscriptions);
    }

    [Fact]
    public async Task ValidatesEventsContentAndRequiredNewSecret()
    {
        using var fixture = new StoreFixture();
        foreach (var edit in new[]
        {
            StoreFixture.Edit(url: ""), StoreFixture.Edit() with { EventTypes = [] },
            StoreFixture.Edit() with { EventTypes = ["unknown.event"] },
            StoreFixture.Edit() with { Content = new string('x', 1001) },
            StoreFixture.Edit() with { WebhookUrl = "https://evil.example/secret" }
        })
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.SaveAsync(edit, null));
        Assert.Empty((await fixture.Store.GetAsync()).Subscriptions);
    }

    [Fact]
    public async Task OptimisticRevisionPreventsLostUpdatesAndDeletes()
    {
        using var fixture = new StoreFixture();
        var id = await fixture.Store.SaveAsync(StoreFixture.Edit(), null);
        await fixture.Store.SaveAsync(StoreFixture.Edit(id, "") with { Name = "Latest" }, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.SaveAsync(StoreFixture.Edit(id, ""), 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.DeleteAsync(id, 1));
        Assert.Equal("Latest", Assert.Single((await fixture.Store.GetAsync()).Subscriptions).Name);
    }

    [Fact]
    public async Task ConsumerFiltersAllStreamByEnabledSubscriptionsAndDeduplicatesRetainedEventIds()
    {
        using var fixture = new StoreFixture();
        var selected = await fixture.Store.SaveAsync(StoreFixture.Edit(), null);
        await fixture.Store.SaveAsync(StoreFixture.Edit() with { Name = "Disabled", Enabled = false }, null);
        await fixture.Store.SaveAsync(StoreFixture.Edit() with { Name = "Other", EventTypes = [ServerEventTypes.UserLogin] }, null);
        var consumer = new DiscordEventConsumer(fixture.Store, NullLogger<DiscordEventConsumer>.Instance);
        await consumer.HandleAsync(StoreFixture.Event(ServerEventTypes.UserLogout), default);
        Assert.Empty((await fixture.Store.GetAsync()).Deliveries);
        var value = StoreFixture.Event();
        await consumer.HandleAsync(value, default);
        await consumer.HandleAsync(value, default);
        var delivery = Assert.Single((await fixture.Store.GetAsync()).Deliveries);
        Assert.Equal(selected, delivery.SubscriptionId);
        Assert.Equal(value.EventId, delivery.EventId);
    }

    [Fact]
    public async Task TestTargetsOnlySelectedEnabledSubscriptionDespiteItsFilter()
    {
        using var fixture = new StoreFixture();
        var selected = await fixture.Store.SaveAsync(StoreFixture.Edit(), null);
        await fixture.Store.SaveAsync(StoreFixture.Edit(), null);
        await fixture.Store.QueueTestAsync(selected);
        var delivery = Assert.Single((await fixture.Store.GetAsync()).Deliveries);
        Assert.Equal(selected, delivery.SubscriptionId);
        Assert.Equal(ServerEventTypes.Test, delivery.EventType);
        await fixture.Store.SaveAsync(StoreFixture.Edit(selected, "") with { Enabled = false }, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.QueueTestAsync(selected));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.QueueTestAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task EventFormattingDoesNotForwardArbitraryPayloadOrActorIdentity()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.SaveAsync(StoreFixture.Edit() with { Content = "Community news" }, null);
        var value = StoreFixture.Event() with
        {
            ActorId = Guid.NewGuid(),
            Payload = JsonSerializer.SerializeToElement(new { password = "do-not-forward", description = "@everyone private" })
        };
        await fixture.Store.EnqueueAsync(value, null);
        string? body = null;
        using var client = StoreFixture.Client(async (request, token) =>
        {
            body = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK);
        });
        await fixture.Store.DeliverNextAsync(client, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.NotNull(body);
        Assert.Contains("Community news", body);
        Assert.DoesNotContain("do-not-forward", body);
        Assert.DoesNotContain(value.ActorId.Value.ToString(), body);
        Assert.DoesNotContain("private", body);
        Assert.Contains(value.TargetId.ToString(), body);
        Assert.Contains(value.EventId.ToString(), body);
    }

    [Theory]
    [InlineData(204, "Delivered", false)]
    [InlineData(400, "Failed", false)]
    [InlineData(404, "Failed", false)]
    [InlineData(500, "Pending", true)]
    [InlineData(408, "Pending", true)]
    public async Task CompletionDistinguishesPermanentAndRetryableFailures(int status, string expected, bool retry)
    {
        using var fixture = new StoreFixture();
        await fixture.QueueAsync();
        var calls = 0;
        using var client = StoreFixture.Client((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        });
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        Assert.True(await fixture.Store.DeliverNextAsync(client, now));
        var view = Assert.Single((await fixture.Store.GetAsync()).Deliveries);
        Assert.Equal(expected, view.Status);
        Assert.Equal(1, view.Attempts);
        Assert.Equal(status, view.HttpStatusCode);
        Assert.False(await fixture.Store.DeliverNextAsync(client, now));
        if (retry)
        {
            Assert.True(view.NextAttemptOn >= now.AddSeconds(5));
            await fixture.Store.DeliverNextAsync(client, view.NextAttemptOn);
            view = Assert.Single((await fixture.Store.GetAsync()).Deliveries);
            await fixture.Store.DeliverNextAsync(client, view.NextAttemptOn);
            view = Assert.Single((await fixture.Store.GetAsync()).Deliveries);
            Assert.Equal("Failed", view.Status);
            Assert.Equal(3, view.Attempts);
            Assert.False(await fixture.Store.DeliverNextAsync(client, now.AddHours(1)));
        }
        Assert.Equal(retry ? 3 : 1, calls);
    }

    [Fact]
    public async Task RateLimitSurvivesRestartAndPausesOtherSubscriptionsWithoutShorteningDelay()
    {
        using var fixture = new StoreFixture();
        await fixture.QueueAsync();
        await fixture.Store.SaveAsync(StoreFixture.Edit(), null);
        await fixture.Store.EnqueueAsync(StoreFixture.Event(), null);
        var calls = 0;
        using var client = StoreFixture.Client((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"retry_after\":120.5,\"global\":true}")
            });
        });
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        Assert.True(await fixture.Store.DeliverNextAsync(client, now));
        fixture.Restart();
        Assert.False(await fixture.Store.DeliverNextAsync(client, now.AddSeconds(120)));
        Assert.Equal(1, calls);
        Assert.True(await fixture.Store.DeliverNextAsync(client, now.AddSeconds(121)));
    }

    [Fact]
    public async Task UnknownRateLimitDelayUsesConservativeMinuteNotImmediateRetry()
    {
        using var fixture = new StoreFixture();
        await fixture.QueueAsync();
        using var client = StoreFixture.Client((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("bad json") }));
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        await fixture.Store.DeliverNextAsync(client, now);
        var next = Assert.Single((await fixture.Store.GetAsync()).Deliveries).NextAttemptOn;
        Assert.True(next >= now.AddSeconds(60));
    }

    [Fact]
    public async Task InterruptedFinalAttemptRecoversAsFailedWithoutExceedingBudget()
    {
        using var fixture = new StoreFixture();
        await fixture.QueueAsync(attempts: 1);
        using var cts = new CancellationTokenSource();
        using var client = StoreFixture.Client(async (_, token) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return new(HttpStatusCode.OK);
        });
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Store.DeliverNextAsync(client, now, cts.Token));
        Assert.Equal("InFlight", Assert.Single((await fixture.Store.GetAsync()).Deliveries).Status);
        fixture.Restart();
        using var noSend = StoreFixture.Client((_, _) => throw new InvalidOperationException("Must not send"));
        Assert.False(await fixture.Store.DeliverNextAsync(noSend, now.AddMinutes(1)));
        var view = Assert.Single((await fixture.Store.GetAsync()).Deliveries);
        Assert.Equal("Failed", view.Status);
        Assert.Equal(1, view.Attempts);
    }

    [Fact]
    public async Task PersistedPendingAttemptResumesAfterRestartWithProtectedSecret()
    {
        using var fixture = new StoreFixture();
        await fixture.QueueAsync();
        fixture.Restart();
        using var client = StoreFixture.Client((request, _) =>
        {
            Assert.EndsWith("synthetic-token?wait=true", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        await fixture.Store.DeliverNextAsync(client, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal("Delivered", Assert.Single((await fixture.Store.GetAsync()).Deliveries).Status);
    }

    [Fact]
    public async Task LostProtectionKeyMarksSecretUnavailableWithoutMakingRequest()
    {
        using var fixture = new StoreFixture();
        await fixture.QueueAsync();
        fixture.Restart(new EphemeralDataProtectionProvider());
        using var noSend = StoreFixture.Client((_, _) => throw new InvalidOperationException("Must not send"));
        Assert.True(await fixture.Store.DeliverNextAsync(noSend, DateTimeOffset.UtcNow.AddSeconds(1)));
        var view = Assert.Single((await fixture.Store.GetAsync()).Deliveries);
        Assert.Equal("SecretUnavailable", view.Status);
        Assert.DoesNotContain("synthetic-token", JsonSerializer.Serialize(view));
    }

    [Fact]
    public async Task SavingDisablingOrDeletingCancelsQueuedWork()
    {
        using var fixture = new StoreFixture();
        var id = await fixture.QueueAsync();
        await fixture.Store.SaveAsync(StoreFixture.Edit(id, "") with { Enabled = false }, 1);
        Assert.Equal("Canceled", Assert.Single((await fixture.Store.GetAsync()).Deliveries).Status);
        using var noSend = StoreFixture.Client((_, _) => throw new InvalidOperationException("Must not send"));
        Assert.False(await fixture.Store.DeliverNextAsync(noSend, DateTimeOffset.UtcNow.AddDays(1)));
        await fixture.Store.DeleteAsync(id, 2);
        Assert.Empty((await fixture.Store.GetAsync()).Deliveries);
    }

    [Fact]
    public async Task ConcurrentDeliveryCallsClaimOneAttempt()
    {
        using var fixture = new StoreFixture();
        await fixture.QueueAsync();
        var calls = 0;
        using var client = StoreFixture.Client((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => fixture.Store.DeliverNextAsync(client, now)));
        Assert.Single(results, value => value);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task QueueOverflowIsVisibleAndDoesNotEvictPreviouslyAcceptedWork()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.SaveAsync(StoreFixture.Edit(), null);
        for (var i = 0; i < DiscordStore.MaximumPending; i++)
            await fixture.Store.EnqueueAsync(StoreFixture.Event(), null);
        await Assert.ThrowsAsync<DiscordStateException>(() => fixture.Store.EnqueueAsync(StoreFixture.Event(), null));
        var snapshot = await fixture.Store.GetAsync();
        Assert.Equal(DiscordStore.MaximumPending, snapshot.Deliveries.Count);
        Assert.Contains("dropped", snapshot.Warning);
        fixture.Restart();
        Assert.Contains("dropped", (await fixture.Store.GetAsync()).Warning);
    }

    [Fact]
    public async Task CorruptStateIsNeverReplacedWithAnEmptyConfiguration()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.SaveAsync(StoreFixture.Edit(), null);
        const string corrupt = "{\"secret\":\"synthetic-token\"}";
        await File.WriteAllTextAsync(fixture.StatePath, corrupt);
        var exception = await Assert.ThrowsAsync<DiscordStateException>(() => fixture.Store.GetAsync());
        Assert.DoesNotContain("synthetic-token", exception.ToString());
        await Assert.ThrowsAsync<DiscordStateException>(() => fixture.Store.SaveAsync(StoreFixture.Edit(), null));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(fixture.StatePath));
    }

    [Fact]
    public async Task MissingPreviouslySavedStateIsAnErrorRatherThanAnEmptyReset()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.SaveAsync(StoreFixture.Edit(), null);
        File.Delete(fixture.StatePath);
        await Assert.ThrowsAsync<DiscordStateException>(() => fixture.Store.GetAsync());
        await Assert.ThrowsAsync<DiscordStateException>(() => fixture.Store.SaveAsync(StoreFixture.Edit(), null));
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public async Task FailedClaimCommitDoesNotSendAndPreservesAcceptedWork()
    {
        using var fixture = new StoreFixture();
        await fixture.QueueAsync();
        var saved = await File.ReadAllTextAsync(fixture.StatePath);
        // A directory at the exact staging path forces a real filesystem write failure.
        var staging = fixture.StatePath + ".pending";
        Directory.CreateDirectory(staging);
        using var noSend = StoreFixture.Client((_, _) => throw new InvalidOperationException("Must not send"));
        try
        {
            var failure = await Assert.ThrowsAsync<DiscordStateException>(() =>
                fixture.Store.DeliverNextAsync(noSend, DateTimeOffset.UtcNow.AddSeconds(1)));
            Assert.DoesNotContain("synthetic-token", failure.ToString());
            Assert.Equal(saved, await File.ReadAllTextAsync(fixture.StatePath));
        }
        finally { Directory.Delete(staging); }
        Assert.Equal(0, Assert.Single((await fixture.Store.GetAsync()).Deliveries).Attempts);
    }

    [Fact]
    public async Task EditingDuringHttpDoesNotResurrectCanceledWork()
    {
        using var fixture = new StoreFixture();
        var id = await fixture.QueueAsync();
        using var client = StoreFixture.Client(async (_, _) =>
        {
            await fixture.Store.SaveAsync(StoreFixture.Edit(id, "") with { Enabled = false }, 1);
            return new(HttpStatusCode.ServiceUnavailable);
        });
        await fixture.Store.DeliverNextAsync(client, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal("Canceled", Assert.Single((await fixture.Store.GetAsync()).Deliveries).Status);
    }

    [Fact]
    public async Task RejectedDeliveryHistoryIsBoundedWithoutDroppingPendingEntries()
    {
        using var fixture = new StoreFixture();
        await fixture.Store.SaveAsync(StoreFixture.Edit(), null);
        using var client = StoreFixture.Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        for (var i = 0; i < DiscordStore.MaximumHistory + 2; i++)
        {
            await fixture.Store.EnqueueAsync(StoreFixture.Event(), null);
            await fixture.Store.DeliverNextAsync(client, DateTimeOffset.UtcNow.AddMinutes(1));
        }
        await fixture.Store.EnqueueAsync(StoreFixture.Event(), null);
        var entries = (await fixture.Store.GetAsync()).Deliveries;
        Assert.Equal(DiscordStore.MaximumHistory + 1, entries.Count);
        Assert.Single(entries, d => d.Status == "Pending");
    }

    [Fact]
    public async Task SecondStoreCannotShareTheSameDataDirectory()
    {
        using var fixture = new StoreFixture();
        using var second = new DiscordStore(fixture.Protection);
        await Assert.ThrowsAsync<DiscordStateException>(() => second.InitializeAsync(fixture.Root));
    }

    [Theory]
    [InlineData(false, "Administrator")]
    [InlineData(true, "User")]
    [InlineData(true, "Moderator")]
    public async Task UnauthorizedAdministrationCannotReadOrMutateAnyState(bool authenticated, string role)
    {
        using var fixture = new StoreFixture();
        var administration = new DiscordAdministration(fixture.Store, new TestAuthentication(authenticated, role));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => administration.GetAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => administration.SaveAsync(StoreFixture.Edit(), null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => administration.DeleteAsync(Guid.NewGuid(), 1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => administration.TestAsync(Guid.NewGuid()));
        Assert.Empty((await fixture.Store.GetAsync()).Subscriptions);
    }

    [Fact]
    public async Task AdministratorCanManageAndTestSubscription()
    {
        using var fixture = new StoreFixture();
        var administration = new DiscordAdministration(fixture.Store, new TestAuthentication(true, "Administrator"));
        await administration.SaveAsync(StoreFixture.Edit(), null);
        var id = Assert.Single((await administration.GetAsync()).Subscriptions).Id;
        await administration.TestAsync(id);
        Assert.Single((await administration.GetAsync()).Deliveries);
        await administration.DeleteAsync(id, 1);
        Assert.Empty((await administration.GetAsync()).Subscriptions);
    }

    [Fact]
    public void EditStringRepresentationNeverIncludesSubmittedCredential()
    {
        Assert.DoesNotContain("synthetic-token", StoreFixture.Edit().ToString());
    }
}

internal sealed class StoreFixture : IDisposable
{
    public const string Endpoint = "https://discord.com/api/webhooks/123456789012345678/synthetic-token";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "discord-tests-" + Guid.NewGuid().ToString("N"));
    public string StatePath => Path.Combine(Root, "discord.json");
    public IDataProtectionProvider Protection { get; private set; } = new EphemeralDataProtectionProvider();
    public DiscordStore Store { get; private set; }

    public StoreFixture()
    {
        Store = new(Protection);
        Store.InitializeAsync(Root).GetAwaiter().GetResult();
    }
    public void Restart(IDataProtectionProvider? protection = null)
    {
        Store.Dispose();
        Protection = protection ?? Protection;
        Store = new(Protection);
        Store.InitializeAsync(Root).GetAwaiter().GetResult();
    }
    public async Task<Guid> QueueAsync(int attempts = 3)
    {
        var id = await Store.SaveAsync(Edit() with { MaxAttempts = attempts }, null);
        await Store.EnqueueAsync(Event(), null);
        return id;
    }
    public static DiscordSubscriptionEdit Edit(Guid id = default, string url = Endpoint) =>
        new(id, "Community", url, [ServerEventTypes.GameCreated]);
    public static ServerEvent Event(string type = ServerEventTypes.GameCreated) =>
        new(Guid.NewGuid(), type, DateTime.UtcNow, ServerEventTypes.GetSource(type), null, "game",
            Guid.NewGuid(), "completed", null, JsonSerializer.SerializeToElement(new { }));
    public static DiscordWebhookClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Handler(send));
    public void Dispose()
    {
        Store.Dispose();
        // The fixture owns this unique test-only directory, never a shared plugin directory.
        foreach (var file in Directory.GetFiles(Root))
            File.Delete(file);
        Directory.Delete(Root);
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}

internal sealed class TestAuthentication(bool authenticated, string role) : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
        Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role)], authenticated ? "test" : null))));
}
