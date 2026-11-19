using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LANCommander.SDK.Events;
using Microsoft.AspNetCore.DataProtection;

namespace LANCommander.Community.Server.DiscordWebhook;

public sealed class DiscordStore(IDataProtectionProvider protection) : IDisposable
{
    internal const int MaximumPending = 256;
    internal const int MaximumHistory = 128;
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly IDataProtector _protector = protection.CreateProtector(DiscordPlugin.PluginId, "endpoint-v1");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _deliveryGate = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32
    };
    private string? _path;
    private FileStream? _lease;
    private bool _hasSavedState;
    private string? _health;
    internal Task Ready => _ready.Task;

    public async Task InitializeAsync(string dataDirectory, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        await _gate.WaitAsync(token);
        try
        {
            if (_path is not null)
                return;
            Directory.CreateDirectory(dataDirectory);
            _lease = new FileStream(Path.Combine(dataDirectory, "discord.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var path = Path.Combine(Path.GetFullPath(dataDirectory), "discord.json");
            await ReadAsync(path, token);
            _hasSavedState = File.Exists(path);
            _path = path;
            _ready.TrySetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DiscordStateException)
        {
            _lease?.Dispose();
            _lease = null;
            _health = "Discord storage is unavailable or invalid; no state was replaced.";
            throw new DiscordStateException(_health);
        }
        finally { _gate.Release(); }
    }

    public Task<DiscordSnapshot> GetAsync(CancellationToken token = default) =>
        AccessAsync(state => new DiscordSnapshot(
            state.Subscriptions.Select(s => new DiscordSubscriptionView(s.Id, s.Name, s.EventTypes.ToArray(),
                s.Enabled, s.Content, s.TimeoutSeconds, s.MaxAttempts, s.Revision)).ToArray(),
            state.Deliveries.OrderByDescending(d => d.NextAttemptOn)
                .Select(d => new DiscordDeliveryView(d.Id, d.SubscriptionId, d.EventId, d.EventType,
                    d.Status, d.Attempts, d.HttpStatusCode, d.NextAttemptOn, d.LastOutcome)).ToArray(),
            _health ?? state.Warning), false, token);

    public Task<Guid> SaveAsync(DiscordSubscriptionEdit edit, long? expectedRevision, CancellationToken token = default)
    {
        ValidateEdit(edit);
        var endpoint = string.IsNullOrEmpty(edit.WebhookUrl) ? null : DiscordWebhookEndpoint.Parse(edit.WebhookUrl);
        return AccessAsync(state =>
        {
            var existing = state.Subscriptions.SingleOrDefault(s => s.Id == edit.Id);
            if (existing is null && (edit.Id != Guid.Empty || expectedRevision is not null))
                throw new InvalidOperationException("Subscription no longer exists. Refresh before editing.");
            if (existing is not null && existing.Revision != expectedRevision)
                throw new InvalidOperationException("Subscription changed in another session. Refresh before editing.");
            if (existing is null && state.Subscriptions.Count >= 32)
                throw new ArgumentException("At most 32 Discord subscriptions are supported.");
            if (existing is null && endpoint is null)
                throw new ArgumentException("A Discord webhook URL is required for a new subscription.");
            var secret = endpoint is null ? existing!.ProtectedEndpoint : _protector.Protect(edit.WebhookUrl!);
            var id = existing?.Id ?? Guid.NewGuid();
            var saved = new SavedDiscordSubscription(id, edit.Name.Trim(), secret,
                edit.EventTypes.Distinct(StringComparer.Ordinal).ToArray(), edit.Enabled,
                string.IsNullOrEmpty(edit.Content) ? null : edit.Content,
                edit.TimeoutSeconds, edit.MaxAttempts, (existing?.Revision ?? 0) + 1);
            state.Subscriptions.RemoveAll(s => s.Id == id);
            state.Subscriptions.Add(saved);
            // A save cancels queued work so old notifications cannot unexpectedly use new settings.
            CancelPending(state, id);
            return id;
        }, true, token);
    }

    public Task DeleteAsync(Guid id, long revision, CancellationToken token = default) =>
        AccessAsync(state =>
        {
            var existing = state.Subscriptions.SingleOrDefault(s => s.Id == id)
                ?? throw new InvalidOperationException("Subscription no longer exists.");
            if (existing.Revision != revision)
                throw new InvalidOperationException("Subscription changed in another session. Refresh before deleting.");
            state.Subscriptions.Remove(existing);
            state.Deliveries.RemoveAll(d => d.SubscriptionId == id);
            return true;
        }, true, token);

    public Task QueueTestAsync(Guid subscriptionId, CancellationToken token = default) =>
        EnqueueAsync(new ServerEvent(Guid.NewGuid(), ServerEventTypes.Test, DateTime.UtcNow, "server",
            null, "webhook", subscriptionId, "completed", null, JsonSerializer.SerializeToElement(new { })),
            subscriptionId, token);

    public async Task EnqueueAsync(ServerEvent serverEvent, Guid? testSubscription, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(serverEvent);
        var overflow = await AccessAsync(state =>
        {
            var matches = state.Subscriptions.Where(s => s.Enabled &&
                (testSubscription is { } id ? s.Id == id : s.EventTypes.Contains(serverEvent.EventType, StringComparer.Ordinal)))
                .Where(s => !state.Deliveries.Any(d => d.SubscriptionId == s.Id && d.EventId == serverEvent.EventId))
                .ToArray();
            if (testSubscription is not null && matches.Length == 0)
                throw new InvalidOperationException("Test requires an existing enabled subscription.");
            if (state.Deliveries.Count(IsPending) + matches.Length > MaximumPending)
            {
                state.Warning = "Discord queue capacity reached. At least one notification was dropped; narrow event filters or repair delivery.";
                return true;
            }
            foreach (var s in matches)
            {
                var message = DiscordEventMessage.Create(serverEvent, s.Content);
                DiscordPayload.Serialize(message);
                state.Deliveries.Add(new(Guid.NewGuid(), s.Id, serverEvent.EventId, serverEvent.EventType,
                    message, "Pending", 0, null, DateTimeOffset.UtcNow));
            }
            return false;
        }, true, token);
        if (overflow)
            throw new DiscordStateException("Discord queue capacity reached; notification was not queued.");
    }

    public async Task<bool> DeliverNextAsync(DiscordWebhookClient client, DateTimeOffset now, CancellationToken token = default)
    {
        await _deliveryGate.WaitAsync(token);
        try
        {
            var claim = await AccessAsync<(SavedDiscordDelivery Delivery, SavedDiscordSubscription Subscription)?>(state =>
            {
                if (state.NotBefore > now)
                    return null;
                var delivery = state.Deliveries.Where(d => IsPending(d) && d.NextAttemptOn <= now)
                    .OrderBy(d => d.NextAttemptOn).FirstOrDefault();
                if (delivery is null)
                    return null;
                var subscription = state.Subscriptions.SingleOrDefault(s => s.Id == delivery.SubscriptionId && s.Enabled);
                if (subscription is null || delivery.Attempts >= subscription.MaxAttempts)
                {
                    Replace(state, delivery with
                    {
                        Status = subscription is null ? "Canceled" : "Failed",
                        LastOutcome = "Attempt interrupted; budget exhausted", NextAttemptOn = now
                    });
                    return null;
                }
                var claimed = delivery with
                {
                    Status = "InFlight", Attempts = delivery.Attempts + 1,
                    NextAttemptOn = now.AddSeconds(subscription.TimeoutSeconds + 5), HttpStatusCode = null
                };
                Replace(state, claimed);
                return (Delivery: claimed, Subscription: subscription);
            }, true, token);
            if (claim is null)
                return false;

            var (work, settings) = claim.Value;
            DiscordDeliveryResult result;
            try
            {
                var endpoint = DiscordWebhookEndpoint.Parse(_protector.Unprotect(settings.ProtectedEndpoint));
                result = await client.SendAsync(endpoint, work.Message, settings.TimeoutSeconds, token);
            }
            catch (CryptographicException)
            {
                await CompleteAsync(work, new(DiscordDeliveryStatus.Rejected), now, settings.MaxAttempts,
                    "SecretUnavailable", token);
                return true;
            }
            // On shutdown the persisted InFlight attempt is recovered after its lease expires.
            await CompleteAsync(work, result, now, settings.MaxAttempts, null, token);
            return true;
        }
        finally { _deliveryGate.Release(); }
    }

    private Task CompleteAsync(SavedDiscordDelivery work, DiscordDeliveryResult result, DateTimeOffset started,
        int maxAttempts, string? failure, CancellationToken token) =>
        AccessAsync(state =>
        {
            var current = state.Deliveries.SingleOrDefault(d => d.Id == work.Id);
            var delay = result.Status == DiscordDeliveryStatus.RateLimited
                ? result.RetryAfter ?? TimeSpan.FromSeconds(60)
                : TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, work.Attempts - 1)));
            // Measure from completion, not request start, so Retry-After is never shortened.
            var completed = DateTimeOffset.UtcNow > started ? DateTimeOffset.UtcNow : started;
            var next = delay > DateTimeOffset.MaxValue - completed ? DateTimeOffset.MaxValue : completed + delay;
            if (result.Status == DiscordDeliveryStatus.RateLimited)
                state.NotBefore = next; // Conservatively honor global and per-webhook 429s alike.
            if (current?.Status != "InFlight")
                return true; // Subscription changed/deleted while the HTTP attempt was running.
            var retry = result.Status is DiscordDeliveryStatus.RateLimited or DiscordDeliveryStatus.TransientFailure
                or DiscordDeliveryStatus.TimedOut;
            var status = failure ?? (result.Status == DiscordDeliveryStatus.Delivered ? "Delivered" :
                retry && work.Attempts < maxAttempts ? "Pending" : "Failed");
            Replace(state, work with
            {
                Status = status, HttpStatusCode = result.HttpStatusCode, LastOutcome = failure ?? result.Status.ToString(),
                NextAttemptOn = status == "Pending" ? next : completed
            });
            return true;
        }, true, token);

    private async Task<T> AccessAsync<T>(Func<DiscordState, T> operation, bool write, CancellationToken token)
    {
        if (_path is null)
            throw new DiscordStateException(_health ?? "Discord plugin storage is not initialized.");
        await _gate.WaitAsync(token);
        string? pending = null;
        try
        {
            var state = await ReadAsync(_path, token);
            if (_hasSavedState && !File.Exists(_path))
                throw new DiscordStateException("Discord saved state is missing; refusing to replace it.");
            var original = write ? JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions) : null;
            var result = operation(state);
            if (write)
            {
                var history = state.Deliveries.Where(d => !IsPending(d)).ToArray();
                foreach (var old in history.Take(Math.Max(0, history.Length - MaximumHistory)))
                    state.Deliveries.Remove(old);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
                if (bytes.Length > MaximumBytes)
                    throw new DiscordStateException("Discord state exceeds the storage size limit.");
                if (original is not null && bytes.AsSpan().SequenceEqual(original))
                    return result;
                pending = _path + ".pending";
                await using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None,
                    8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, token);
                    await stream.FlushAsync(token);
                    stream.Flush(true);
                }
                token.ThrowIfCancellationRequested();
                File.Move(pending, _path, true);
                _hasSavedState = true;
                _health = null;
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DiscordStateException)
        {
            _health = "Discord storage could not be read or committed. Notifications may be lost; check disk and permissions.";
            throw new DiscordStateException(_health);
        }
        finally
        {
            try
            {
                if (pending is not null)
                {
                    try { File.Delete(pending); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _health = "Discord staged state cleanup failed; an operation may already have committed. Refresh before retrying.";
                        throw new DiscordStateException(_health);
                    }
                }
            }
            finally { _gate.Release(); }
        }
    }

    private static async Task<DiscordState> ReadAsync(string path, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            if (stream.Length > MaximumBytes)
                throw new DiscordStateException("Discord storage exceeds its size limit.");
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, token);
            var state = JsonSerializer.Deserialize<DiscordState>(bytes, JsonOptions);
            if (state is null || state.SchemaVersion != 1 || state.Subscriptions is null || state.Deliveries is null ||
                state.Subscriptions.Count > 32 || state.Subscriptions.Any(s => s is null || s.Id == Guid.Empty ||
                    string.IsNullOrEmpty(s.ProtectedEndpoint) || s.Revision < 1) ||
                state.Subscriptions.Select(s => s.Id).Distinct().Count() != state.Subscriptions.Count ||
                state.Deliveries.Count > MaximumPending + MaximumHistory ||
                state.Deliveries.Any(d => d is null || d.Id == Guid.Empty || d.Attempts is < 0 or > 5 ||
                    d.Message is null || d.EventType is null ||
                    d.Status is not ("Pending" or "InFlight" or "Delivered" or "Failed" or "Canceled" or "SecretUnavailable")) ||
                state.Deliveries.Select(d => d.Id).Distinct().Count() != state.Deliveries.Count ||
                state.Deliveries.Count(IsPending) > MaximumPending)
                throw new DiscordStateException("Discord storage is invalid.");
            try
            {
                foreach (var s in state.Subscriptions)
                    ValidateEdit(new(s.Id, s.Name, null, s.EventTypes, s.Enabled, s.Content, s.TimeoutSeconds, s.MaxAttempts));
                foreach (var d in state.Deliveries)
                    DiscordPayload.Serialize(d.Message);
            }
            catch (ArgumentException) { throw new DiscordStateException("Discord saved configuration is invalid."); }
            return state;
        }
        catch (FileNotFoundException) { return new(); }
    }

    private static void ValidateEdit(DiscordSubscriptionEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (string.IsNullOrWhiteSpace(edit.Name) || edit.Name.Length > 100)
            throw new ArgumentException("A subscription name of 1-100 characters is required.");
        if (edit.EventTypes is null || edit.EventTypes.Length is < 1 or > 64 ||
            edit.EventTypes.Any(t => !ServerEventTypes.All.Contains(t)))
            throw new ArgumentException("Select at least one supported event type.");
        if (edit.TimeoutSeconds is < 1 or > 30 || edit.MaxAttempts is < 1 or > 5)
            throw new ArgumentException("Timeout must be 1-30 seconds and attempts must be 1-5.");
        if (edit.Content?.Length > 1000)
            throw new ArgumentException("Optional message content must not exceed 1000 characters.");
    }

    private static bool IsPending(SavedDiscordDelivery delivery) => delivery.Status is "Pending" or "InFlight";
    private static void Replace(DiscordState state, SavedDiscordDelivery delivery)
    {
        var index = state.Deliveries.FindIndex(d => d.Id == delivery.Id);
        state.Deliveries[index] = delivery;
    }
    private static void CancelPending(DiscordState state, Guid id)
    {
        foreach (var delivery in state.Deliveries.Where(d => d.SubscriptionId == id && IsPending(d)).ToArray())
            Replace(state, delivery with
            {
                Status = "Canceled", LastOutcome = "Subscription settings changed", NextAttemptOn = DateTimeOffset.UtcNow
            });
    }

    public void Dispose()
    {
        _lease?.Dispose();
        _gate.Dispose();
        _deliveryGate.Dispose();
    }
}
