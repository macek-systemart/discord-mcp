using DiscordMcp.Discord;
using DiscordMcp.Http;
using DiscordMcp.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DiscordMcp.Tools;

/// <summary>
/// The one place where tool failures become results: <see cref="DiscordApiException"/> keeps its fixed (localized)
/// message, everything else gets a fixed text (exception messages may carry URLs or response details). Client
/// cancellation and SDK protocol errors pass through.
/// </summary>
public static class ToolErrorFilter
{
    internal static string GuardMessage => Languages.Current.GuardBlocked;

    internal static string GenericMessage => Languages.Current.ToolFailed;

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            try
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }
            catch (DiscordApiException e)
            {
                return Error(e.Message);
            }
            catch (RequestGuardException e)
            {
                // Guard messages name only method and path, never the query or headers.
                Logger(request)?.LogError("Tool {Tool}: {Reason}", request.Params?.Name, e.Message);
                return Error(GuardMessage);
            }
            catch (McpException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                Logger(request)?.LogError("Tool {Tool} failed with {Exception}.{NewLine}{StackTrace}", request.Params?.Name, e.GetType().FullName, Environment.NewLine, e.StackTrace);
                return Error(GenericMessage);
            }
        };

    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    private static ILogger? Logger(RequestContext<CallToolRequestParams> request) =>
        request.Services?.GetService<ILoggerFactory>()?.CreateLogger("DiscordMcp.Tools");
}
