using LANCommander.Server.Plugins;
using Microsoft.AspNetCore.Components.Authorization;

namespace LANCommander.Community.Server.DiscordWebhook;

public sealed class DiscordAdministration(DiscordStore store, AuthenticationStateProvider authentication)
{
    private async Task AuthorizeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var state = await authentication.GetAuthenticationStateAsync().WaitAsync(token);
        if (!PluginAccessPolicy.Administrator.IsSatisfiedBy(state.User))
            throw new UnauthorizedAccessException("LANCommander administrator access is required.");
    }

    public async Task<DiscordSnapshot> GetAsync(CancellationToken token = default)
    {
        await AuthorizeAsync(token);
        return await store.GetAsync(token);
    }

    public async Task SaveAsync(DiscordSubscriptionEdit edit, long? expectedRevision, CancellationToken token = default)
    {
        await AuthorizeAsync(token);
        await store.SaveAsync(edit, expectedRevision, token);
    }

    public async Task DeleteAsync(Guid id, long revision, CancellationToken token = default)
    {
        await AuthorizeAsync(token);
        await store.DeleteAsync(id, revision, token);
    }

    public async Task TestAsync(Guid id, CancellationToken token = default)
    {
        await AuthorizeAsync(token);
        await store.QueueTestAsync(id, token);
    }
}
