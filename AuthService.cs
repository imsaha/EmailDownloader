using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailDownloader.Config;
using Microsoft.Identity.Client;
using Spectre.Console;

namespace EmailDownloader.Auth;

public sealed class AuthResult
{
    public string AccessToken { get; init; } = string.Empty;
    public string UserEmail { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public DateTimeOffset ExpiresOn { get; init; }
}

public interface IAuthService
{
    Task<AuthResult> AuthenticateAsync(CancellationToken ct = default);
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
}

public sealed class MsalAuthService : IAuthService
{
    private readonly AzureAdConfig _config;
    private IPublicClientApplication? _app;
    private AuthenticationResult? _lastResult;

    public MsalAuthService(AzureAdConfig config)
    {
        _config = config;
    }

    private IPublicClientApplication BuildApp()
    {
        return PublicClientApplicationBuilder
            .Create(_config.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _config.TenantId)
            .WithRedirectUri(_config.RedirectUri)
            .Build();
    }

    public async Task<AuthResult> AuthenticateAsync(CancellationToken ct = default)
    {
        _app = BuildApp();

        AnsiConsole.MarkupLine("[bold yellow]⚡ Initiating OpenID Connect authentication...[/]");
        AnsiConsole.MarkupLine("[grey]A browser window will open for you to sign in.[/]");
        AnsiConsole.WriteLine();

        AuthenticationResult result;

        try
        {
            // Try silent first (cached token)
            var accounts = await _app.GetAccountsAsync();
            var account = accounts.FirstOrDefault();

            if (account != null)
            {
                AnsiConsole.MarkupLine("[grey]Found cached account, attempting silent sign-in...[/]");
                result = await _app.AcquireTokenSilent(_config.Scopes, account)
                    .ExecuteAsync(ct);
            }
            else
            {
                // Interactive login via system browser
                result = await _app.AcquireTokenInteractive(_config.Scopes)
                    .WithPrompt(Prompt.SelectAccount)
                    .WithUseEmbeddedWebView(false)
                    .ExecuteAsync(ct);
            }
        }
        catch (MsalUiRequiredException)
        {
            // Silent failed, go interactive
            result = await _app.AcquireTokenInteractive(_config.Scopes)
                .WithPrompt(Prompt.SelectAccount)
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync(ct);
        }

        _lastResult = result;

        var email = result.Account?.Username ?? ExtractEmailFromClaims(result.IdToken);
        var name = result.ClaimsPrincipal?.FindFirst("name")?.Value
                   ?? result.ClaimsPrincipal?.FindFirst("preferred_username")?.Value
                   ?? email;

        return new AuthResult
        {
            AccessToken = result.AccessToken,
            UserEmail = email,
            UserName = name,
            ExpiresOn = result.ExpiresOn
        };
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_app == null || _lastResult == null)
            throw new InvalidOperationException("Not authenticated. Call AuthenticateAsync first.");

        if (_lastResult.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            return _lastResult.AccessToken;

        // Refresh silently
        var accounts = await _app.GetAccountsAsync();
        var account = accounts.FirstOrDefault();
        if (account == null) throw new InvalidOperationException("No cached account found.");

        var result = await _app.AcquireTokenSilent(_config.Scopes, account).ExecuteAsync(ct);
        _lastResult = result;
        return result.AccessToken;
    }

    private static string ExtractEmailFromClaims(string? idToken)
    {
        if (string.IsNullOrEmpty(idToken)) return "unknown@user.com";

        try
        {
            var parts = idToken.Split('.');
            if (parts.Length < 2) return "unknown@user.com";

            var payload = parts[1];
            // Pad base64
            payload += new string('=', (4 - payload.Length % 4) % 4);
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("email", out var email)) return email.GetString() ?? "";
            if (root.TryGetProperty("preferred_username", out var pun)) return pun.GetString() ?? "";
            if (root.TryGetProperty("upn", out var upn)) return upn.GetString() ?? "";
        }
        catch { /* ignore */ }

        return "unknown@user.com";
    }
}

/// <summary>
/// Device code flow alternative — useful when browser isn't available.
/// </summary>
public sealed class DeviceCodeAuthService : IAuthService
{
    private readonly AzureAdConfig _config;
    private IPublicClientApplication? _app;
    private AuthenticationResult? _lastResult;

    public DeviceCodeAuthService(AzureAdConfig config) => _config = config;

    private IPublicClientApplication BuildApp() =>
        PublicClientApplicationBuilder
            .Create(_config.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _config.TenantId)
            .Build();

    public async Task<AuthResult> AuthenticateAsync(CancellationToken ct = default)
    {
        _app = BuildApp();

        var result = await _app.AcquireTokenWithDeviceCode(_config.Scopes, deviceCodeResult =>
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold cyan]╔══════════════════════════════════════════╗[/]");
            AnsiConsole.MarkupLine("[bold cyan]║       DEVICE CODE AUTHENTICATION         ║[/]");
            AnsiConsole.MarkupLine("[bold cyan]╚══════════════════════════════════════════╝[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[white]1. Open:[/] [link]{deviceCodeResult.VerificationUrl}[/]");
            AnsiConsole.MarkupLine($"[white]2. Enter code:[/] [bold yellow]{deviceCodeResult.UserCode}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Waiting for authentication...[/]");
            return Task.CompletedTask;
        }).ExecuteAsync(ct);

        _lastResult = result;
        var email = result.Account?.Username ?? "";

        return new AuthResult
        {
            AccessToken = result.AccessToken,
            UserEmail = email,
            UserName = email,
            ExpiresOn = result.ExpiresOn
        };
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_app == null || _lastResult == null)
            throw new InvalidOperationException("Not authenticated.");

        if (_lastResult.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            return _lastResult.AccessToken;

        var accounts = await _app.GetAccountsAsync();
        var account = accounts.FirstOrDefault() ?? throw new InvalidOperationException("No account found.");
        var result = await _app.AcquireTokenSilent(_config.Scopes, account).ExecuteAsync(ct);
        _lastResult = result;
        return result.AccessToken;
    }
}
