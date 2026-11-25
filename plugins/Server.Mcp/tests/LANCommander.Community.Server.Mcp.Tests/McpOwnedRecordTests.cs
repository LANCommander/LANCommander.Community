using System.Text.Json;
using LANCommander.SDK.Enums;
using LANCommander.Server.Plugins;
using Moq;
using Xunit;

namespace LANCommander.Community.Server.Mcp.Tests;

public sealed partial class McpHttpTests
{
    private const int ExpectedToolCount = 60;

    private static bool IsError(JsonElement response) =>
        response.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean();

    [Fact]
    public async Task OwnedRecordToolsCarrySafetyAnnotations()
    {
        var tools = (await Request("tools/list")).GetProperty("result").GetProperty("tools").EnumerateArray()
            .ToDictionary(x => x.GetProperty("name").GetString()!);
        foreach (var area in new[] { "action", "script", "save_path", "multiplayer_mode" })
        {
            Assert.True(tools[$"list_{area}s"].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
            Assert.True(tools[$"get_{area}"].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
            Assert.True(tools[$"update_{area}"].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
            var delete = tools[$"delete_{area}"];
            Assert.True(delete.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
            Assert.Contains("Obtain explicit user approval", delete.GetProperty("description").GetString());
        }
        // Scripts run on clients, possibly elevated: creating one is treated as destructive.
        Assert.True(tools["create_script"].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        Assert.Contains("approval", tools["create_script"].GetProperty("description").GetString());
        foreach (var name in new[] { "list_media", "get_media", "list_archives", "get_archive", "get_key_counts" })
            Assert.True(tools[name].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.DoesNotContain(tools.Keys, x => x.Contains("delete_media") || x.Contains("delete_archive")
            || x is "list_keys" or "get_key" or "create_key" or "release_key");
    }

    [Fact]
    public async Task ActionCrudTargetsGameVersionAndIgnoresSuppliedId()
    {
        var gameId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var owner = RecordOwner.Game(gameId, versionId);
        var action = new ActionInfo { Id = id, Name = "Play", Path = "{InstallDir}/game.exe", Arguments = "-keep",
            Type = ActionType.Primary, Platforms = RuntimePlatform.Windows, Owner = owner };
        _actions.Setup(x => x.GetAsync(owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync([action, action with { Id = Guid.NewGuid(), Name = "Setup", Type = ActionType.Config }]);
        _actions.Setup(x => x.AddAsync(owner, It.Is<ActionInfo>(a => a.Id == Guid.Empty && a.Name == "Play"
            && a.Type == ActionType.Primary), It.IsAny<CancellationToken>())).ReturnsAsync(action);
        _actions.Setup(x => x.GetRecordAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(action);
        _actions.Setup(x => x.UpdateAsync(It.Is<ActionInfo>(a => a.Id == id && a.Arguments == "-keep"
            && a.Type == ActionType.Secondary && a.Platforms == RuntimePlatform.Windows), It.IsAny<CancellationToken>()))
            .ReturnsAsync(action with { Type = ActionType.Secondary });
        _actions.Setup(x => x.DeleteAsync(id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var page = ToolContent(await Call("list_actions", new { ownerKind = "Game", ownerId = gameId, versionId, search = "set" }));
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal("Setup", page.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(id, ToolContent(await Call("create_action", new { ownerKind = "Game", ownerId = gameId, versionId, action }))
            .GetProperty("id").GetGuid());
        Assert.Equal("-keep", ToolContent(await Call("get_action", new { id })).GetProperty("arguments").GetString());
        Assert.False(IsError(await Call("update_action", new { action = action with { Type = ActionType.Secondary } })));
        Assert.False(IsError(await Call("delete_action", new { id })));
        _actions.VerifyAll();
    }

    [Fact]
    public async Task ActionsOnLatestVersionPassNullVersion()
    {
        var toolId = Guid.NewGuid();
        _actions.Setup(x => x.GetAsync(RecordOwner.Tool(toolId), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var page = ToolContent(await Call("list_actions", new { ownerKind = "Tool", ownerId = toolId }));
        Assert.Equal(0, page.GetProperty("total").GetInt32());
        _actions.VerifyAll();
    }

    [Fact]
    public async Task InvalidOwnersDoNotCallProviders()
    {
        var id = Guid.NewGuid();
        // Actions cannot belong to redistributables.
        Assert.True(IsError(await Call("list_actions", new { ownerKind = "Redistributable", ownerId = id })));
        // A version only scopes game-owned records.
        Assert.True(IsError(await Call("list_scripts", new { ownerKind = "Tool", ownerId = id, versionId = Guid.NewGuid() })));
        Assert.True(IsError(await Call("list_save_paths", new { gameId = Guid.Empty })));
        Assert.True(IsError(await Call("list_archives", new { ownerKind = "Server", ownerId = id })));
        Assert.True(IsError(await Call("create_action", new { ownerKind = "Game", ownerId = id, action = new ActionInfo { Name = " " } })));
        _actions.VerifyNoOtherCalls();
        _scripts.VerifyNoOtherCalls();
        _savePaths.VerifyNoOtherCalls();
        _archives.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ScriptCrudUsesOwnerAndPreservesContents()
    {
        var redistributableId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var owner = RecordOwner.Redistributable(redistributableId);
        var script = new ScriptInfo { Id = id, Name = "Install runtime", Type = ScriptType.Install,
            Contents = "Write-Host keep", RequiresAdmin = true, Platforms = RuntimePlatform.Windows, Owner = owner };
        _scripts.Setup(x => x.GetAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync([script]);
        _scripts.Setup(x => x.AddAsync(owner, It.Is<ScriptInfo>(s => s.Id == Guid.Empty && s.Contents == script.Contents
            && s.RequiresAdmin), It.IsAny<CancellationToken>())).ReturnsAsync(script);
        _scripts.Setup(x => x.GetRecordAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(script);
        _scripts.Setup(x => x.UpdateAsync(It.Is<ScriptInfo>(s => s.Id == id && s.Contents == script.Contents
            && s.RequiresAdmin && s.Name == "Renamed"), It.IsAny<CancellationToken>())).ReturnsAsync(script with { Name = "Renamed" });
        _scripts.Setup(x => x.DeleteAsync(id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Assert.Equal(1, ToolContent(await Call("list_scripts", new { ownerKind = "Redistributable", ownerId = redistributableId }))
            .GetProperty("total").GetInt32());
        Assert.Equal(id, ToolContent(await Call("create_script", new { ownerKind = "Redistributable", ownerId = redistributableId, script }))
            .GetProperty("id").GetGuid());
        Assert.Equal("Write-Host keep", ToolContent(await Call("get_script", new { id })).GetProperty("contents").GetString());
        Assert.Equal("Renamed", ToolContent(await Call("update_script", new { script = script with { Name = "Renamed" } }))
            .GetProperty("name").GetString());
        Assert.False(IsError(await Call("delete_script", new { id })));
        _scripts.VerifyAll();
    }

    [Fact]
    public async Task PackageScriptsAreRejected()
    {
        var script = new ScriptInfo { Id = Guid.NewGuid(), Name = "Package", Type = ScriptType.Package, Contents = "x" };
        Assert.True(IsError(await Call("create_script", new { ownerKind = "Game", ownerId = Guid.NewGuid(), script })));
        Assert.True(IsError(await Call("update_script", new { script })));
        _scripts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SavePathCrudTargetsGameVersion()
    {
        var gameId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var owner = RecordOwner.Game(gameId, versionId);
        var savePath = new SavePathInfo { Id = id, Type = SavePathType.File, Path = "{InstallDir}/saves/*.sav",
            WorkingDirectory = "{InstallDir}", IsRegex = false, Platforms = RuntimePlatform.Windows, Owner = owner };
        _savePaths.Setup(x => x.GetAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync([savePath]);
        _savePaths.Setup(x => x.AddAsync(owner, It.Is<SavePathInfo>(p => p.Id == Guid.Empty && p.Path == savePath.Path),
            It.IsAny<CancellationToken>())).ReturnsAsync(savePath);
        _savePaths.Setup(x => x.GetRecordAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(savePath);
        _savePaths.Setup(x => x.UpdateAsync(It.Is<SavePathInfo>(p => p.Id == id && p.WorkingDirectory == "{InstallDir}" && p.IsRegex),
            It.IsAny<CancellationToken>())).ReturnsAsync(savePath with { IsRegex = true });
        _savePaths.Setup(x => x.DeleteAsync(id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Assert.Equal(1, ToolContent(await Call("list_save_paths", new { gameId, versionId })).GetProperty("total").GetInt32());
        Assert.Equal(id, ToolContent(await Call("create_save_path", new { gameId, versionId, savePath })).GetProperty("id").GetGuid());
        Assert.Equal(savePath.Path, ToolContent(await Call("get_save_path", new { id })).GetProperty("path").GetString());
        Assert.True(ToolContent(await Call("update_save_path", new { savePath = savePath with { IsRegex = true } }))
            .GetProperty("isRegex").GetBoolean());
        Assert.False(IsError(await Call("delete_save_path", new { id })));
        Assert.True(IsError(await Call("create_save_path", new { gameId, savePath = savePath with { Path = "" } })));
        _savePaths.VerifyAll();
    }

    [Fact]
    public async Task MultiplayerModeCrudBelongsToGame()
    {
        var gameId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var owner = RecordOwner.Game(gameId);
        var mode = new MultiplayerModeInfo { Id = id, Type = MultiplayerType.LAN, NetworkProtocol = NetworkProtocol.TCPIP,
            Description = "Deathmatch", MinPlayers = 2, MaxPlayers = 16, Spectators = 4, Owner = owner };
        _multiplayerModes.Setup(x => x.GetAsync(owner, It.IsAny<CancellationToken>())).ReturnsAsync([mode]);
        _multiplayerModes.Setup(x => x.AddAsync(owner, It.Is<MultiplayerModeInfo>(m => m.Id == Guid.Empty && m.MaxPlayers == 16),
            It.IsAny<CancellationToken>())).ReturnsAsync(mode);
        _multiplayerModes.Setup(x => x.GetRecordAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(mode);
        _multiplayerModes.Setup(x => x.UpdateAsync(It.Is<MultiplayerModeInfo>(m => m.Id == id && m.Spectators == 4 && m.MaxPlayers == 32),
            It.IsAny<CancellationToken>())).ReturnsAsync(mode with { MaxPlayers = 32 });
        _multiplayerModes.Setup(x => x.DeleteAsync(id, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Assert.Equal(1, ToolContent(await Call("list_multiplayer_modes", new { gameId })).GetProperty("total").GetInt32());
        Assert.Equal(id, ToolContent(await Call("create_multiplayer_mode", new { gameId, multiplayerMode = mode })).GetProperty("id").GetGuid());
        Assert.Equal(16, ToolContent(await Call("get_multiplayer_mode", new { id })).GetProperty("maxPlayers").GetInt32());
        Assert.Equal(32, ToolContent(await Call("update_multiplayer_mode", new { multiplayerMode = mode with { MaxPlayers = 32 } }))
            .GetProperty("maxPlayers").GetInt32());
        Assert.False(IsError(await Call("delete_multiplayer_mode", new { id })));
        Assert.True(IsError(await Call("update_multiplayer_mode", new { multiplayerMode = mode with { MinPlayers = -1 } })));
        _multiplayerModes.VerifyAll();
    }

    [Fact]
    public async Task MediaAndArchivesAreReadOnlyMetadata()
    {
        var gameId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();
        var archiveId = Guid.NewGuid();
        var media = new MediaInfo { Id = mediaId, GameId = gameId, Name = "Cover", MimeType = "image/png" };
        var archive = new ArchiveInfo { Id = archiveId, GameId = gameId, GameVersionId = versionId, Version = "1.0", CompressedSize = 42 };
        _media.Setup(x => x.GetAsync(gameId, It.IsAny<CancellationToken>())).ReturnsAsync([media]);
        _media.Setup(x => x.GetMediaAsync(mediaId, It.IsAny<CancellationToken>())).ReturnsAsync(media);
        _archives.Setup(x => x.GetAsync(RecordOwner.Game(gameId, versionId), It.IsAny<CancellationToken>())).ReturnsAsync([archive]);
        _archives.Setup(x => x.GetArchiveAsync(archiveId, It.IsAny<CancellationToken>())).ReturnsAsync(archive);

        Assert.Equal(1, ToolContent(await Call("list_media", new { gameId })).GetProperty("total").GetInt32());
        Assert.Equal("image/png", ToolContent(await Call("get_media", new { id = mediaId })).GetProperty("mimeType").GetString());
        Assert.Equal(1, ToolContent(await Call("list_archives", new { ownerKind = "Game", ownerId = gameId, versionId }))
            .GetProperty("total").GetInt32());
        Assert.Equal(42, ToolContent(await Call("get_archive", new { id = archiveId })).GetProperty("compressedSize").GetInt64());
        _media.VerifyAll();
        _archives.VerifyAll();
    }

    [Fact]
    public async Task KeyCountsNeverExposeKeyValues()
    {
        var gameId = Guid.NewGuid();
        _keys.Setup(x => x.GetAsync(RecordOwner.Game(gameId), It.IsAny<CancellationToken>())).ReturnsAsync([
            new KeyInfo { Id = Guid.NewGuid(), Value = "SECRET-FREE-KEY" },
            new KeyInfo { Id = Guid.NewGuid(), Value = "SECRET-USED-KEY", AllocationMethod = KeyAllocationMethod.UserAccount,
                ClaimedByComputerName = "SECRET-PC" },
        ]);
        var response = await Call("get_key_counts", new { gameId });
        var counts = ToolContent(response);
        Assert.Equal(2, counts.GetProperty("total").GetInt32());
        Assert.Equal(1, counts.GetProperty("free").GetInt32());
        Assert.Equal(1, counts.GetProperty("claimed").GetInt32());
        Assert.DoesNotContain("SECRET", response.ToString());
        _keys.VerifyAll();
        _keys.VerifyNoOtherCalls();
    }
}
