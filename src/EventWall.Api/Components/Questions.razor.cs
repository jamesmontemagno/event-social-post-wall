using EventWall.Api.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace EventWall.Api.Components;

public partial class Questions : IAsyncDisposable
{
    [Inject] private IJSRuntime JavaScript { get; set; } = null!;
    [Inject] private IConfiguration Configuration { get; set; } = null!;
    [Inject] private ILogger<Questions> Logger { get; set; } = null!;
    private readonly CancellationTokenSource lifetime = new();
    private IJSObjectReference? browser;
    private SubmissionConfigDto? config;
    private SubmissionRequest? attempt;
    private Guid attemptKey;
    private string text = "";
    private string displayName = "";
    private string website = "";
    private string message = "";
    private string themeError = "";
    private bool loading = true;
    private bool sending;
    private bool received;
    private bool renewToken;
    private bool coolingDown;
    private bool lightTheme;
    private bool challengeRendered;
    private bool ChallengeEnabled => !string.IsNullOrEmpty(config?.TurnstileSiteKey);
    private string EventName => config?.Name ?? Configuration["Event:Name"] ?? "Community Event";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Reload();
            return;
        }
        if (browser is null || loading || received || !ChallengeEnabled || challengeRendered) return;
        challengeRendered = true;
        try
        {
            var reply = await browser.InvokeAsync<ChallengeReply>("renderChallenge", config!.TurnstileSiteKey);
            if (reply.Ok) return;
            message = reply.Message ?? "The verification check could not load. Please reload the page.";
        }
        catch (Exception exception) when (exception is JSException or TaskCanceledException)
        {
            Logger.LogWarning(exception, "Could not render the question verification check.");
            message = "The verification check could not load. Please reload the page.";
        }
        if (!lifetime.IsCancellationRequested) StateHasChanged();
    }

    private async Task Reload()
    {
        loading = true;
        challengeRendered = false;
        message = "";
        try
        {
            browser ??= await JavaScript.InvokeAsync<IJSObjectReference>("import", "/" + Assets["questions/questions.js"]);
            lightTheme = await browser.InvokeAsync<bool>("isLightTheme");
            var draft = await browser.InvokeAsync<BrowserDraft?>("restoreDraft");
            if (draft is not null)
            {
                text = draft.Text;
                displayName = draft.DisplayName;
                website = draft.Website;
                attempt = draft.Attempt;
                attemptKey = Guid.TryParse(draft.Key, out var key) ? key : Guid.Empty;
            }
            config = await LoadConfig();
        }
        catch (Exception exception) when (exception is JSException or TaskCanceledException)
        {
            Logger.LogWarning(exception, "Could not initialize the question browser connection.");
            message = "The question page could not be loaded. Please try loading again.";
        }
        finally
        {
            loading = false;
            if (!lifetime.IsCancellationRequested) await InvokeAsync(StateHasChanged);
        }
    }

    private async Task<SubmissionConfigDto?> LoadConfig()
    {
        var reply = await browser!.InvokeAsync<BrowserReply<SubmissionConfigDto>>("loadConfig");
        if (reply.Ok && reply.Data is { Name.Length: > 0, SubmissionToken.Length: > 0 } value)
            return value;
        message = reply.Message ?? "The question page returned an invalid configuration. Please try loading again.";
        Logger.LogWarning("Question configuration failed with status {Status}.", reply.Status);
        return null;
    }

    private async Task Submit()
    {
        if (sending || config is null || coolingDown || string.IsNullOrWhiteSpace(text)) return;
        sending = true;
        message = "";
        var payload = new SubmissionRequest(text.Trim(), displayName.Trim(), website);
        if (attempt != payload || attemptKey == Guid.Empty)
        {
            attempt = payload;
            attemptKey = Guid.NewGuid();
        }
        try
        {
            if (renewToken)
            {
                var renewed = await LoadConfig();
                if (renewed is null)
                {
                    message += " Your draft has been kept. Please try again when ready.";
                    return;
                }
                config = renewed;
                renewToken = false;
            }
            // Fetch from the browser to retain its host-only cookie, Origin and trusted client-IP limits.
            var reply = await browser!.InvokeAsync<BrowserReply<SubmissionReceipt>>(
                "submit", payload, attemptKey.ToString("D"), config.SubmissionToken);
            if (reply.Ok && reply.Data?.Received == true)
            {
                received = true;
                challengeRendered = false;
                text = "";
                attempt = null;
                attemptKey = Guid.Empty;
                return;
            }
            renewToken = reply.Code == "submission-token-invalid";
            var retry = reply.Status == 429 ? reply.RetryAfterSeconds ?? 60 : 0;
            message = (reply.Message ?? "Your question could not be sent.") +
                (retry > 0 ? $" Please wait {retry} seconds before retrying." : " Your draft has been kept. Please try again when ready.");
            if (retry > 0)
            {
                coolingDown = true;
                _ = EndCooldown(retry);
            }
            Logger.LogWarning("Question submission failed with status {Status} and code {Code}.", reply.Status, reply.Code);
        }
        catch (Exception exception) when (exception is JSException or TaskCanceledException)
        {
            Logger.LogWarning(exception, "Could not send a question through the browser connection.");
            message = "Your question could not be sent. Your draft has been kept. Please try again when ready.";
        }
        finally
        {
            sending = false;
        }
    }

    private async Task EndCooldown(int seconds)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), lifetime.Token);
            await InvokeAsync(() => { coolingDown = false; StateHasChanged(); });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task ToggleTheme()
    {
        if (browser is null) return;
        try
        {
            var result = await browser.InvokeAsync<ThemeResult>("toggleTheme");
            lightTheme = result.Light;
            themeError = result.Error ?? "";
        }
        catch (Exception exception) when (exception is JSException or TaskCanceledException)
        {
            Logger.LogWarning(exception, "Could not change the question page color theme.");
            themeError = "The color theme could not be changed. Please try again.";
        }
    }

    private void AskAnother()
    {
        received = false;
        message = "";
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        lifetime.Dispose();
        if (browser is null) return;
        try { await browser.DisposeAsync(); }
        catch (JSDisconnectedException) { Logger.LogDebug("Question browser disconnected during disposal."); }
    }

    private sealed record BrowserReply<T>(bool Ok, int Status, T? Data, string? Message, string? Code, int? RetryAfterSeconds);
    private sealed record BrowserDraft(string Text, string DisplayName, string Website, SubmissionRequest? Attempt, string? Key);
    private sealed record SubmissionReceipt(bool Received);
    private sealed record ThemeResult(bool Light, string? Error);
    private sealed record ChallengeReply(bool Ok, string? Message);
}
