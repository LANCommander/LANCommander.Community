using System.Text.Json;
using LANCommander.Server.Plugins;
using Moq;
using Xunit;

namespace LANCommander.Community.Server.Mcp.Tests;

public sealed partial class McpHttpTests
{
    private static string ErrorText(JsonElement response)
    {
        var result = response.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean(), result.ToString());
        return result.GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    [Fact]
    public async Task HostLookupFailuresReachTheClient()
    {
        var gameId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        _actions.Setup(x => x.GetAsync(RecordOwner.Game(gameId, versionId), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException($"There is no version of game {gameId} with the id {versionId}."));

        var text = ErrorText(await Call("list_actions", new { ownerKind = "Game", ownerId = gameId, versionId }));

        Assert.Contains($"There is no version of game {gameId} with the id {versionId}.", text);
    }

    [Fact]
    public async Task HostArgumentFailuresReachTheClient()
    {
        var game = new GameInfo { Id = Guid.NewGuid(), Title = "Doom" };
        _games.Setup(x => x.UpdateAsync(It.IsAny<GameInfo>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("The base game cannot be the game itself.", "game"));

        var text = ErrorText(await Call("update_game", new { game }));

        Assert.Contains("The base game cannot be the game itself.", text);
    }

    [Fact]
    public async Task InvalidArgumentsExplainTheProblem()
    {
        var text = ErrorText(await Call("list_actions", new { ownerKind = "Game", ownerId = "not-a-guid" }));

        Assert.DoesNotContain("An error occurred invoking", text);
        Assert.Contains("Invalid arguments for 'list_actions'", text);
        Assert.Contains("Guid", text);
        _actions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnexpectedFailuresStayGeneric()
    {
        _actions.Setup(x => x.GetAsync(It.IsAny<RecordOwner>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidCastException("connection string Password=secret"));

        var text = ErrorText(await Call("list_actions", new { ownerKind = "Tool", ownerId = Guid.NewGuid() }));

        Assert.DoesNotContain("secret", text);
        Assert.Contains("An error occurred invoking 'list_actions'", text);
    }
}
