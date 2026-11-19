using LANCommander.SDK.Events;

namespace LANCommander.Community.Server.DiscordWebhook;

public sealed record DiscordSubscriptionEdit(
    Guid Id, string Name, string? WebhookUrl, string[] EventTypes,
    bool Enabled = true, string? Content = null, int TimeoutSeconds = 10, int MaxAttempts = 3)
{
    public override string ToString() => $"Discord subscription edit {Id:D} (credentials redacted)";
}

public sealed record DiscordSubscriptionView(
    Guid Id, string Name, string[] EventTypes, bool Enabled, string? Content,
    int TimeoutSeconds, int MaxAttempts, long Revision);

public sealed record DiscordDeliveryView(
    Guid Id, Guid SubscriptionId, Guid EventId, string EventType, string Status,
    int Attempts, int? HttpStatusCode, DateTimeOffset NextAttemptOn, string? LastOutcome);

public sealed record DiscordSnapshot(
    IReadOnlyList<DiscordSubscriptionView> Subscriptions,
    IReadOnlyList<DiscordDeliveryView> Deliveries, string? Warning);

public sealed class DiscordStateException(string message) : Exception(message);

internal sealed class DiscordState
{
    public int SchemaVersion { get; set; } = 1;
    public List<SavedDiscordSubscription> Subscriptions { get; set; } = [];
    public List<SavedDiscordDelivery> Deliveries { get; set; } = [];
    public DateTimeOffset NotBefore { get; set; }
    public string? Warning { get; set; }
}

internal sealed record SavedDiscordSubscription(
    Guid Id, string Name, string ProtectedEndpoint, string[] EventTypes,
    bool Enabled, string? Content, int TimeoutSeconds, int MaxAttempts, long Revision);

internal sealed record SavedDiscordDelivery(
    Guid Id, Guid SubscriptionId, Guid EventId, string EventType, DiscordMessage Message,
    string Status, int Attempts, int? HttpStatusCode, DateTimeOffset NextAttemptOn, string? LastOutcome = null);

internal static class DiscordEventMessage
{
    public static DiscordMessage Create(ServerEvent value, string? content)
    {
        // Deliberately omit arbitrary payloads, actor identifiers and issue/user text.
        // Events can contain user-controlled or sensitive data.
        var type = value.EventType.Length <= 256 ? value.EventType : "Unknown event";
        var target = value.TargetType.Length <= 128 ? value.TargetType : "resource";
        var status = value.Status.Length <= 128 ? value.Status : "unknown";
        return new(content,
        [
            new(Title: type, Color: status == "failed" ? 0xD73A49 : 0x5865F2,
                Fields:
                [
                    new("Target", $"{target}: {value.TargetId:D}"),
                    new("Status", status),
                    new("Occurred (UTC)", value.OccurredAt.ToUniversalTime().ToString("O")),
                    new("Event ID", value.EventId.ToString("D"))
                ])
        ]);
    }
}
