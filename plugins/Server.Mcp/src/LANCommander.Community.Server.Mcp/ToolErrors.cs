using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LANCommander.Community.Server.Mcp;

/// <summary>
/// Returns the host's documented lookup and validation failures to the client as tool errors. The SDK
/// otherwise replaces every exception other than <see cref="ModelContextProtocol.McpException"/> with a
/// generic message. Any other exception keeps that generic message so internal details are not exposed.
/// </summary>
internal static class ToolErrors
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken);
            }
            catch (Exception e) when (e is KeyNotFoundException or ArgumentException)
            {
                return Error(e.Message);
            }
            catch (JsonException e)
            {
                // Arguments that cannot be bound to the tool's parameters, such as a malformed id.
                return Error($"Invalid arguments for '{context.Params?.Name}': {e.Message}");
            }
        };

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
