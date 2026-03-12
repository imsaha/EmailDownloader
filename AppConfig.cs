namespace EmailDownloader.Config;

public sealed class AzureAdConfig
{
    public string ClientId { get; init; } = string.Empty;
    public string TenantId { get; init; } = "common";
    public string RedirectUri { get; init; } = "http://localhost";
    public string[] Scopes { get; init; } = Array.Empty<string>();
}

public sealed class DownloadConfig
{
    public string OutputPath { get; init; } = "./output";
    public int BatchSize { get; init; } = 50;
    public int MaxRetries { get; init; } = 3;
    public int RetryDelaySeconds { get; init; } = 2;
    public bool GroupByYear { get; init; } = true;
    public string[] IncludeFolders { get; init; } = Array.Empty<string>();
    public string[] ExcludeFolders { get; init; } = Array.Empty<string>();
}

public sealed class AppConfig
{
    public AzureAdConfig AzureAd { get; init; } = new();
    public DownloadConfig Download { get; init; } = new();
}
