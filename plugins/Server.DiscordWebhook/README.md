# Discord webhooks

A separate administrator-only page at **`/Plugins/DiscordWebhooks`**, linked in
the server sidebar as **Discord webhooks**. This plugin receives LANCommander's
published server events, selects its own enabled subscriptions, and sends
Discord-compatible messages. It does not replace the event transport, poll for
events, or modify the built-in generic webhook settings, subscriptions or UI.

## Requirements and installation

Requires **LANCommander Server 2.2.0-beta.4 or later** and .NET 10. Compiled and
loader-tested against published `LANCommander.SDK`, `LANCommander.Server.Plugins`
and `LANCommander.Server.UI` **2.2.0-beta.4**, corresponding to upstream source
`675befe0673c03aded2b45995694661ce454ba95`. A compile-time package restore does
not upgrade the host. Newer hosts are not live-tested.

The package and assembly identity is
`LANCommander.Community.Server.DiscordWebhook`; its stable plugin ID is
`io.github.lancommander.community.discordwebhook`. The independent initial
version is **0.1.0-beta.1**, not a claim that it has been published.
Package types are `LANCommander.Plugin` and `LANCommander.Plugin.Server`.
Only the plugin DLL is bundled; host/framework dependencies are never bundled.
No Discord SDK, bot account or bot token is required.

Build the local `.nupkg` with the validation command below. Install an
authorized published package through LANCommander's server plugin management,
or extract the contents of `lib/net10.0` from the locally built package to a
dedicated server `Plugins` subdirectory while the host is stopped. Restart
the server for discovery. Do not copy test output or host dependency DLLs.
This PR does not publish a package, create release tags, or install anything.

## Management

Sign in as a LANCommander administrator and open **Discord webhooks**.
The host page access attribute, navigation policy and every administration
service operation require the host's administrator policy. There are no
unauthenticated plugin HTTP endpoints.

Choose **New subscription**, then supply:

- A name (1-100 characters) and an incoming Discord webhook URL.
- One or more exact event types from the published host event catalogue.
- Enabled/disabled state, optional fixed message content (up to 1,000 characters),
  timeout (1-30 seconds, default 10), and total attempt budget (1-5, default 3).

Use **Save subscription** to create or update. Editing shows a blank password
field, never the existing URL; leave it blank to preserve the saved credential.
Saving cancels pending/in-flight records for that subscription so old queued
events do not unexpectedly use changed destinations or settings. An HTTP
request already sent cannot be recalled. Concurrent edits/deletes are rejected
using revision checks rather than silently overwriting another administrator.
Delete requires confirmation and removes the subscription and its history.

**Send test** explicitly queues a synthetic `server.webhook.test` message for
only that enabled subscription, regardless of its event filter. It does not
change host resources, but **does send a real message** if used on a running
installation. Tests in this repository never invoke real Discord endpoints.
Refresh the page to see pending, in-flight, canceled, delivered, failed or
secret-unavailable status, attempts, last outcome, HTTP code and next retry.
Receiver text, exception messages and webhook URLs never appear in history.

## Messages and endpoint safety

Messages contain optional fixed content and a safe embed with the event type,
target type/ID, status, UTC timestamp and event ID. Arbitrary host payloads,
actor identity, usernames, issue descriptions, credentials and other free-form
event data are **not** forwarded. Administrators must still consider target IDs,
event types/times, status and fixed content sensitive when choosing a channel.
All Discord mention parsing is disabled, including user, role and everyone
mentions. Formatting/escaping uses JSON serialization, not interpolation.

Only canonical HTTPS incoming webhook URLs on `discord.com` or `discordapp.com`
are accepted, at `/api/webhooks/{id}/{token}` or
`/api/v10/webhooks/{id}/{token}`. Queries, fragments, credentials, custom ports,
encoded/normalized paths and arbitrary hosts are rejected without echoing input.
The client adds controlled `wait=true` for server-confirmed message creation.
Forum/media thread destinations, attachments, avatars, components, arbitrary
JSON and custom templates are not supported.

The reusable payload serializer enforces Discord's content (2,000), embed count
(10), title/author (256), description (4,096), footer (2,048), field count (25),
field name/value (256/1,024) and combined embed text (6,000) limits. It rejects
oversized messages rather than truncating, using conservative UTF-16 lengths.
The UI deliberately exposes a smaller, predictable message shape.

URLs are protected with the host's ASP.NET Core data-protection provider and a
plugin-specific purpose; they are not returned to the browser. The URL input is
cleared after submission, including validation failure. The default HTTP handler
disables redirects, cookies, proxies and trace-header propagation; no
HTTP-client-factory request logger is used. HTTP status and fixed classifications
are recorded, not bodies/reason phrases or exception text.
Host-installed HTTP tracing/profilers can still observe URLs: disable or redact
webhook paths in that instrumentation. Restrict outbound access and plugin Data
directory permissions at the OS/container level. Discord execution tokens are
credentials; never put them in logs, examples, fixed message content or commits.

## Delivery guarantees and limits

The public beta.4
[`IServerEventConsumer`](https://github.com/LANCommander/LANCommander/blob/675befe0673c03aded2b45995694661ce454ba95/LANCommander.Server.Plugins/Messaging/ServerEventContracts.cs)
receives every event independently of built-in webhook filters. The plugin
filters that stream; it does not duplicate host event publication.
The host's default Rebus transport is in-memory, and the host dispatcher logs
and drops consumer failures. **Events may be lost before plugin receipt or if
enqueue fails.** The built-in generic webhook outbox does not replay events to
this plugin. There is no claim of end-to-end guaranteed delivery.

Once queued and committed locally, work survives normal host restarts.
A single worker claims and persists each attempt before HTTP. Interrupted
attempts consume budget and are recovered after their saved timeout lease
expires; the final interrupted attempt is marked failed rather than sent again.
Caller/host cancellation propagates. Crashes, timeouts or failed result commits
after Discord accepted a message can lead to duplicates. Delivery is not
exactly-once or ordered. Event-ID deduplication covers only retained entries.

2xx is delivered; HTTP 408/5xx, network errors and timeouts retry within the
attempt budget with 5/10/20/40-second backoff (maximum 60 seconds).
Other HTTP failures, including redirects, 400, 401 and deleted webhooks, fail
without retry. Missing data-protection keys produce `SecretUnavailable`.
HTTP 429 honors the greater valid `Retry-After` header (seconds/date) and JSON
`retry_after` (fractional seconds); invalid/missing delay uses 60 seconds.
The bounded 4-KiB response parser never exposes receiver content. The entire
plugin pauses conservatively after any 429, so even global limits are respected
across subscriptions and restarts. The worker starts at most one attempt each
second; there is no hardcoded Discord route quota or cross-process rate broker.

Storage is one atomic JSON-replacement file in **`PluginContext.DataDirectory`**,
with an exclusive lifetime lock, serialized operations and disk flush before
replacement. Limits are 32 subscriptions, 256 pending/in-flight deliveries,
128 retained terminal entries and 4 MiB total state. Capacity overflow rejects
the new event atomically (without evicting accepted work), records a persistent
warning and logs a credential-free error. That warning describes past loss and
remains until operator repair. Invalid/unreadable state is an explicit error,
never reset to empty. Mutable state is never stored in the install directory.

This is **single-server local storage**, not a clustered outbox. Do not share its
Data directory across processes or network filesystems. When scaling the host,
Rebus consumers may run on different instances and local subscriptions/queues
are not synchronized. Keep one receiving instance for this plugin.
Back up the plugin Data directory **and the host's persistent data-protection
key ring** before upgrades. Losing keys prevents decrypting saved URLs; edit
and resupply credentials to recover. Preserve Data during upgrades.
Storage/permission failures require operator repair; do not delete state as an
automatic recovery. A process/power failure can have an ambiguous final commit.

## Validation and release

Requires .NET 10 SDK and PowerShell 7:

```powershell
dotnet test .\plugins\Server.DiscordWebhook\tests\LANCommander.Community.Server.DiscordWebhook.Tests\LANCommander.Community.Server.DiscordWebhook.Tests.csproj -c Release --nologo
pwsh .\eng\Test-ReleaseSelection.ps1
pwsh .\eng\Test-Repository.ps1
pwsh .\eng\Validate-Plugin.ps1 -Plugin Server.DiscordWebhook -OutputDirectory .\artifacts\validation-discord-new
```

Use a fresh output directory. Validation runs unit/integration/rendered UI tests,
NuGet metadata/private-only inventory checks and shared-host-type discovery
against the DLL extracted from the package. All HTTP tests use in-process fake
handlers and synthetic credentials. These are not live LANCommander install,
browser screenshots, real Discord delivery or live-host upgrade tests.

CI and the independent registry/release selection include `Server.DiscordWebhook`.
Future authorized tag format: `server-discordwebhook-v<version>`. See the child
changelog and [repository release procedure](../../RELEASES.md). No release,
package-feed publication or merge is performed by validation.

Discord references:
[execute webhook](https://docs.discord.com/developers/resources/webhook#execute-webhook),
[embed limits](https://docs.discord.com/developers/resources/message#embed-object-embed-limits),
[rate limits](https://docs.discord.com/developers/topics/rate-limits).
