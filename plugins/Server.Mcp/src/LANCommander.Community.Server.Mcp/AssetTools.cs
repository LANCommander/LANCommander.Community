using System.ComponentModel;
using LANCommander.Server.Plugins;
using ModelContextProtocol.Server;

namespace LANCommander.Community.Server.Mcp;

public sealed record KeyCounts(Guid GameId, int Total, int Free, int Claimed);

/// <summary>Read-only access to media, archives and key availability. Files and key values are never returned.</summary>
[McpServerToolType]
public sealed class AssetTools(IMediaProvider media, IArchiveProvider archives, IKeyProvider keys)
{
    private static readonly RecordOwnerKind[] ArchiveOwners = [RecordOwnerKind.Game, RecordOwnerKind.Redistributable, RecordOwnerKind.Tool];

    [McpServerTool(Name = "list_media", ReadOnly = true, Idempotent = true)]
    [Description("Read a game's media records (icons, covers, screenshots…), ordered by type and sort order. Returns metadata only, not files.")]
    public async Task<CatalogPage<MediaInfo>> ListMedia(Guid gameId, int offset = 0, int limit = 25, CancellationToken cancellationToken = default)
    {
        ToolInput.PageBounds(offset, limit);
        return ToolInput.Page(await media.GetAsync(ToolInput.Id(gameId), cancellationToken), x => x.Name ?? "", null, offset, limit);
    }

    [McpServerTool(Name = "get_media", ReadOnly = true, Idempotent = true)]
    [Description("Read a media record's metadata. Does not return the file.")]
    public async Task<MediaInfo> GetMedia(Guid id, CancellationToken cancellationToken) =>
        ToolInput.Found(await media.GetMediaAsync(ToolInput.Id(id), cancellationToken));

    [McpServerTool(Name = "list_archives", ReadOnly = true, Idempotent = true)]
    [Description("Read the archives of a Game, Redistributable or Tool owner, newest first. For a game, every version's archive unless versionId is given. Metadata only.")]
    public async Task<CatalogPage<ArchiveInfo>> ListArchives(RecordOwnerKind ownerKind, Guid ownerId, Guid? versionId = null,
        int offset = 0, int limit = 25, CancellationToken cancellationToken = default)
    {
        ToolInput.PageBounds(offset, limit);
        var owner = ToolInput.Owner(ownerKind, ownerId, versionId, ArchiveOwners);
        return ToolInput.Page(await archives.GetAsync(owner, cancellationToken), x => x.Version ?? "", null, offset, limit);
    }

    [McpServerTool(Name = "get_archive", ReadOnly = true, Idempotent = true)]
    [Description("Read an archive's metadata (version, sizes, storage location, owner ids). Does not return the file.")]
    public async Task<ArchiveInfo> GetArchive(Guid id, CancellationToken cancellationToken) =>
        ToolInput.Found(await archives.GetArchiveAsync(ToolInput.Id(id), cancellationToken));

    [McpServerTool(Name = "get_key_counts", ReadOnly = true, Idempotent = true)]
    [Description("Count a game's CD keys: total, free and claimed. Never returns key values or who claimed them.")]
    public async Task<KeyCounts> GetKeyCounts(Guid gameId, CancellationToken cancellationToken)
    {
        var owner = ToolInput.Owner(RecordOwnerKind.Game, gameId, null, RecordOwnerKind.Game);
        var all = await keys.GetAsync(owner, cancellationToken);
        var free = all.Count(x => x.AllocationMethod is null);
        return new KeyCounts(gameId, all.Count, free, all.Count - free);
    }
}
