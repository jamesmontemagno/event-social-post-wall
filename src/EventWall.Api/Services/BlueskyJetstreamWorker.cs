using System.Net.WebSockets;
using System.Text.Json;
using EventWall.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventWall.Api.Services;

public sealed class BlueskyJetstreamWorker(
    IConfiguration configuration,
    IBlueskyJetstreamClient jetstream,
    BlueskyCursorStore cursorStore,
    BlueskyProfileResolver profiles,
    EventPostIngestor ingestor,
    SourceHealthRegistry health,
    ILogger<BlueskyJetstreamWorker> logger) : BackgroundService
{
    private const int CursorBatchSize = 100;
    private const int MaximumReconnectDelaySeconds = 60;
    private readonly EventConfiguration _event = new(configuration);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("Social:Bluesky:Enabled"))
        {
            health.Report("Bluesky", "disabled", "Jetstream collection is disabled.");
            return;
        }

        if (!configuration.GetValue<bool>("Social:Bluesky:PolicyApproved"))
        {
            health.Report("Bluesky", "policy-review-required", "Collection remains paused until platform rules are reviewed.");
            return;
        }

        var cursor = await cursorStore.GetAsync(stoppingToken);
        long? pendingCursor = null;
        var pendingCount = 0;
        var imported = 0;
        var reconnectDelaySeconds = 2;

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTime? connectedAtUtc = null;
            try
            {
                await jetstream.ConnectAsync(cursor, stoppingToken);
                connectedAtUtc = DateTime.UtcNow;
                health.Report("Bluesky", "connected", "Connected to the public Jetstream stream; filtering configured event terms locally.", DateTime.UtcNow);

                await foreach (var item in jetstream.ReadEventsAsync(stoppingToken).WithCancellation(stoppingToken))
                {
                    imported += await ProcessEventAsync(item, stoppingToken);
                    pendingCursor = item.Sequence;
                    pendingCount++;

                    if (pendingCount >= CursorBatchSize)
                    {
                        await FlushCursorAsync(stoppingToken);
                    }

                    health.Report(
                        "Bluesky",
                        "connected",
                        $"Jetstream is live. {imported} matching posts ingested in this connection.",
                        DateTime.UtcNow);
                }

                throw new WebSocketException("The Jetstream connection closed.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException exception)
            {
                ReportConnectionFailure(exception, "The Jetstream connection timed out; reconnecting.");
            }
            catch (WebSocketException exception)
            {
                ReportConnectionFailure(exception, "The Jetstream WebSocket disconnected; reconnecting.");
            }
            catch (HttpRequestException exception)
            {
                ReportConnectionFailure(exception, "The Jetstream endpoint could not be reached; reconnecting.");
            }
            catch (IOException exception)
            {
                ReportConnectionFailure(exception, "The Jetstream stream could not be read; reconnecting.");
            }
            catch (InvalidDataException exception)
            {
                ReportConnectionFailure(exception, "Jetstream returned an invalid message; reconnecting.");
            }
            catch (JsonException exception)
            {
                ReportConnectionFailure(exception, "Jetstream returned an unexpected event; reconnecting.");
            }
            catch (DbUpdateException exception)
            {
                health.Report("Bluesky", "error", "A Bluesky event or stream cursor could not be saved.");
                logger.LogError(exception, "Could not persist a Bluesky event or cursor.");
            }
            catch (InvalidOperationException exception)
            {
                ReportConnectionFailure(exception, "The Bluesky stream could not be processed; reconnecting.");
            }
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (connectedAtUtc is not null && DateTime.UtcNow - connectedAtUtc.Value >= TimeSpan.FromSeconds(30))
            {
                reconnectDelaySeconds = 2;
            }

            try
            {
                await FlushCursorAsync(stoppingToken);
            }
            catch (DbUpdateException exception)
            {
                health.Report("Bluesky", "error", "The Bluesky stream cursor could not be saved; reconnecting from the last saved cursor.");
                logger.LogError(exception, "Could not persist the Bluesky stream cursor.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(reconnectDelaySeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            reconnectDelaySeconds = Math.Min(reconnectDelaySeconds * 2, MaximumReconnectDelaySeconds);
        }

        async Task FlushCursorAsync(CancellationToken cancellationToken)
        {
            if (pendingCursor is null)
            {
                return;
            }

            var requestedCursor = pendingCursor.Value;
            var savedCursor = await cursorStore.SaveAsync(requestedCursor, cancellationToken);
            if (savedCursor > requestedCursor)
            {
                logger.LogInformation("The Bluesky checkpoint has already advanced; using the saved cursor.");
            }
            cursor = savedCursor;
            pendingCursor = null;
            pendingCount = 0;
        }
    }

    private async Task<int> ProcessEventAsync(BlueskyStreamEvent item, CancellationToken cancellationToken)
    {
        var externalId = $"at://{item.Did}/{item.Collection}/{item.RKey}";
        if (item.Operation == "delete")
        {
            await ingestor.RemoveMessageAsync(
                PostSource.Bluesky,
                externalId,
                "Bluesky reported that the post was deleted.",
                cancellationToken);
            return 0;
        }

        if (item.Operation is not ("create" or "update"))
        {
            return 0;
        }

        if (!_event.Matches(item.Text))
        {
            await ingestor.RemoveMessageAsync(
                PostSource.Bluesky,
                externalId,
                "The Bluesky post no longer matches the event terms.",
                cancellationToken);
            return 0;
        }

        var profile = await profiles.GetAsync(item.Did, cancellationToken);
        var authorHandle = profile.Handle ?? item.Did;
        var authorName = string.IsNullOrWhiteSpace(profile.DisplayName)
            ? $"@{authorHandle}"
            : profile.DisplayName;
        var postUrl = $"https://bsky.app/profile/{Uri.EscapeDataString(authorHandle)}/post/{Uri.EscapeDataString(item.RKey)}";
        var profileUrl = $"https://bsky.app/profile/{Uri.EscapeDataString(authorHandle)}";

        return await ingestor.IngestAsync(
            PostSource.Bluesky,
            externalId,
            item.Text!,
            authorName,
            item.Did,
            postUrl,
            item.CreatedAtUtc,
            cancellationToken,
            profileUrl,
            profile.AvatarUrl,
            item.Images) ? 1 : 0;
    }

    private void ReportConnectionFailure(Exception exception, string detail)
    {
        health.Report("Bluesky", "error", detail);
        logger.LogWarning(exception, "Bluesky Jetstream connection failed.");
    }
}
