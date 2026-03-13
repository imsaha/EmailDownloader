using System.Text;
using System.Text.Json;
using EmailDownloader.Config;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
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

    private async Task<IPublicClientApplication> BuildAppAsync()
    {
        var app = PublicClientApplicationBuilder
            .Create(_config.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _config.TenantId)
            .WithRedirectUri(_config.RedirectUri)
            .Build();
        await TokenCacheHelper.RegisterAsync(app);
        return app;
    }

    public async Task<AuthResult> AuthenticateAsync(CancellationToken ct = default)
    {
        _app = await BuildAppAsync();

        AuthenticationResult result;

        try
        {
            var accounts = await _app.GetAccountsAsync();
            var account = accounts.FirstOrDefault();

            if (account != null)
            {
                // Refresh token available — sign in silently without a browser
                result = await _app.AcquireTokenSilent(_config.Scopes, account)
                    .ExecuteAsync(ct);
            }
            else
            {
                AnsiConsole.MarkupLine("[bold yellow]⚡ Initiating OpenID Connect authentication...[/]");
                AnsiConsole.MarkupLine("[grey]A browser window will open for you to sign in.[/]");
                AnsiConsole.WriteLine();

                result = await _app.AcquireTokenInteractive(_config.Scopes)
                    .WithPrompt(Prompt.SelectAccount)
                    .WithUseEmbeddedWebView(false)
                    .ExecuteAsync(ct);
            }
        }
        catch (MsalUiRequiredException)
        {
            // Silent failed — refresh token expired or revoked; fall back to browser
            AnsiConsole.MarkupLine("[bold yellow]⚡ Session expired. Re-authenticating...[/]");
            AnsiConsole.MarkupLine("[grey]A browser window will open for you to sign in.[/]");
            AnsiConsole.WriteLine();

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

    private async Task<IPublicClientApplication> BuildAppAsync()
    {
        var app = PublicClientApplicationBuilder
            .Create(_config.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _config.TenantId)
            .Build();
        await TokenCacheHelper.RegisterAsync(app);
        return app;
    }

    public async Task<AuthResult> AuthenticateAsync(CancellationToken ct = default)
    {
        _app = await BuildAppAsync();

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

/// <summary>
/// Manages a persistent MSAL token cache stored on disk.
/// On Windows the file is DPAPI-encrypted; macOS uses Keychain; Linux uses SecretService.
/// </summary>
internal static class TokenCacheHelper
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EmailDownloader");

    private const string CacheFileName = "token_cache.bin";

    public static async Task RegisterAsync(IPublicClientApplication app)
    {
        var props = new StorageCreationPropertiesBuilder(CacheFileName, CacheDir)
            .WithMacKeyChain("EmailDownloader", "TokenCache")
            .WithLinuxKeyring(
                "EmailDownloader.secrets",
                MsalCacheHelper.LinuxKeyRingDefaultCollection,
                "EmailDownloader Token Cache",
                new KeyValuePair<string, string>("Version", "1"),
                new KeyValuePair<string, string>("ProductGroup", "EmailDownloader"))
            .Build();

        var helper = await MsalCacheHelper.CreateAsync(props);
        helper.RegisterCache(app.UserTokenCache);
    }

    /// <summary>
    /// Returns the cached account's email/username if a valid session exists, otherwise null.
    /// </summary>
    public static async Task<string?> GetCachedAccountEmailAsync(AzureAdConfig config)
    {
        var app = PublicClientApplicationBuilder
            .Create(config.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, config.TenantId)
            .WithRedirectUri(config.RedirectUri)
            .Build();

        await RegisterAsync(app);
        var accounts = await app.GetAccountsAsync();
        return accounts.FirstOrDefault()?.Username;
    }
}
