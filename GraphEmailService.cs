using EmailDownloader.Auth;
using EmailDownloader.Config;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Authentication;
using Spectre.Console;

namespace EmailDownloader.Email;

public interface IEmailService
{
    Task<List<MailFolder>> GetFoldersAsync(CancellationToken ct = default);
    IAsyncEnumerable<EmailMessage> GetMessagesAsync(
        IEnumerable<MailFolder> folders,
        IProgress<DownloadStats> progress,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? untilDate = null,
        CancellationToken ct = default);
    Task<EmailMessage?> GetMessageWithBodyAsync(string messageId, CancellationToken ct = default);
    Task DeleteMessageAsync(string messageId, CancellationToken ct = default);
}

/// <summary>
/// Wraps Microsoft Graph SDK to fetch emails page by page.
/// </summary>
public sealed class GraphEmailService : IEmailService
{
    private readonly IAuthService _auth;
    private readonly DownloadConfig _config;
    private GraphServiceClient? _graphClient;

    public GraphEmailService(IAuthService auth, DownloadConfig config)
    {
        _auth = auth;
        _config = config;
    }

    private async Task<GraphServiceClient> GetClientAsync(CancellationToken ct)
    {
        if (_graphClient != null) return _graphClient;

        var token = await _auth.GetAccessTokenAsync(ct);
        var authProvider = new BaseBearerTokenAuthenticationProvider(
            new StaticTokenProvider(token));

        _graphClient = new GraphServiceClient(authProvider);
        return _graphClient;
    }

    public async Task<List<MailFolder>> GetFoldersAsync(CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        var folders = new List<MailFolder>();

        var response = await client.Me.MailFolders.GetAsync(req =>
        {
            req.QueryParameters.Top = 50;
            req.QueryParameters.IncludeHiddenFolders = "true";
        }, ct);

        var page = response;
        while (page?.Value != null)
        {
            foreach (var f in page.Value)
            {
                var shouldInclude = _config.IncludeFolders.Length == 0
                    || _config.IncludeFolders.Any(inc =>
                        inc.Equals(f.DisplayName, StringComparison.OrdinalIgnoreCase));

                var shouldExclude = _config.ExcludeFolders.Any(exc =>
                    exc.Equals(f.DisplayName, StringComparison.OrdinalIgnoreCase));

                if (shouldInclude && !shouldExclude)
                {
                    folders.Add(new MailFolder
                    {
                        Id = f.Id ?? "",
                        DisplayName = f.DisplayName ?? "",
                        TotalItemCount = f.TotalItemCount ?? 0,
                        UnreadItemCount = f.UnreadItemCount ?? 0,
                        ParentFolderId = f.ParentFolderId ?? ""
                    });
                }
            }

            if (page.OdataNextLink == null) break;
            page = await client.Me.MailFolders
                .WithUrl(page.OdataNextLink)
                .GetAsync(cancellationToken: ct);
        }

        return folders;
    }

    public async IAsyncEnumerable<EmailMessage> GetMessagesAsync(
        IEnumerable<MailFolder> folders,
        IProgress<DownloadStats> progress,
        DateTimeOffset? fromDate = null,
        DateTimeOffset? untilDate = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        var stats = new DownloadStats();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Build OData $filter for date range
        var filters = new List<string>();
        if (fromDate.HasValue)
            filters.Add($"receivedDateTime ge {fromDate.Value.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}");
        if (untilDate.HasValue)
            filters.Add($"receivedDateTime le {untilDate.Value.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}");
        var dateFilter = filters.Count > 0 ? string.Join(" and ", filters) : null;

        foreach (var folder in folders)
        {
            if (ct.IsCancellationRequested) yield break;

            var pageResponse = await client.Me.MailFolders[folder.Id].Messages
                .GetAsync(req =>
                {
                    req.QueryParameters.Top = _config.BatchSize;
                    req.QueryParameters.Select = new[]
                    {
                        "id", "subject", "from", "toRecipients", "ccRecipients",
                        "receivedDateTime", "hasAttachments", "isRead",
                        "importance", "conversationId", "internetMessageId",
                        "categories", "bodyPreview"
                    };
                    req.QueryParameters.Orderby = new[] { "receivedDateTime desc" };
                    if (dateFilter != null)
                        req.QueryParameters.Filter = dateFilter;
                }, ct);

            var page = pageResponse;
            while (page?.Value != null)
            {
                if (ct.IsCancellationRequested) yield break;

                foreach (var msg in page.Value)
                {
                    if (ct.IsCancellationRequested) yield break;

                    // Fetch full body separately to get complete content
                    Microsoft.Graph.Models.Message? fullMsg = null;
                    for (int attempt = 0; attempt < _config.MaxRetries; attempt++)
                    {
                        try
                        {
                            fullMsg = await client.Me.Messages[msg.Id]
                                .GetAsync(req =>
                                {
                                    req.QueryParameters.Select = new[]
                                    {
                                        "id", "subject", "from", "toRecipients",
                                        "ccRecipients", "bccRecipients",
                                        "body", "receivedDateTime", "hasAttachments",
                                        "isRead", "importance", "conversationId",
                                        "internetMessageId", "categories"
                                    };
                                }, ct);
                            break;
                        }
                        catch (Exception) when (attempt < _config.MaxRetries - 1)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(_config.RetryDelaySeconds), ct);
                        }
                    }

                    if (fullMsg == null)
                    {
                        stats.Failed++;
                        continue;
                    }

                    var attachments = new List<EmailAttachment>();
                    if (fullMsg.HasAttachments == true)
                    {
                        for (int attAttempt = 0; attAttempt < _config.MaxRetries; attAttempt++)
                        {
                            try
                            {
                                var attResponse = await client.Me.Messages[fullMsg.Id!].Attachments
                                    .GetAsync(cancellationToken: ct);
                                if (attResponse?.Value != null)
                                {
                                    foreach (var att in attResponse.Value.OfType<FileAttachment>())
                                    {
                                        attachments.Add(new EmailAttachment
                                        {
                                            Id = att.Id ?? "",
                                            Name = att.Name ?? "attachment",
                                            ContentType = att.ContentType ?? "application/octet-stream",
                                            Content = att.ContentBytes,
                                            Size = att.Size ?? 0,
                                            IsInline = att.IsInline ?? false,
                                            ContentId = att.ContentId ?? ""
                                        });
                                    }
                                }
                                break;
                            }
                            catch (Exception) when (attAttempt < _config.MaxRetries - 1)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(_config.RetryDelaySeconds), ct);
                            }
                        }
                    }

                    var emailMsg = MapToEmailMessage(fullMsg, folder.DisplayName, attachments);
                    stats.Downloaded++;
                    stats.Elapsed = sw.Elapsed;

                    if (!stats.ByYear.ContainsKey(emailMsg.Year))
                        stats.ByYear[emailMsg.Year] = 0;
                    stats.ByYear[emailMsg.Year]++;

                    if (!stats.ByFolder.ContainsKey(folder.DisplayName))
                        stats.ByFolder[folder.DisplayName] = 0;
                    stats.ByFolder[folder.DisplayName]++;

                    progress.Report(stats with { });
                    yield return emailMsg;
                }

                if (page.OdataNextLink == null) break;
                page = await client.Me.MailFolders[folder.Id].Messages
                    .WithUrl(page.OdataNextLink)
                    .GetAsync(cancellationToken: ct);
            }
        }
    }

    public async Task<EmailMessage?> GetMessageWithBodyAsync(string messageId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        var msg = await client.Me.Messages[messageId].GetAsync(cancellationToken: ct);
        return msg == null ? null : MapToEmailMessage(msg, "");
    }

    public async Task DeleteMessageAsync(string messageId, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        await client.Me.Messages[messageId].DeleteAsync(cancellationToken: ct);
    }

    private static EmailMessage MapToEmailMessage(
        Microsoft.Graph.Models.Message msg, string folderName,
        List<EmailAttachment>? attachments = null)
    {
        var from = msg.From?.EmailAddress?.Address ?? "";
        if (!string.IsNullOrEmpty(msg.From?.EmailAddress?.Name))
            from = $"{msg.From.EmailAddress.Name} <{from}>";

        var to = string.Join("; ",
            msg.ToRecipients?.Select(r =>
                $"{r.EmailAddress?.Name} <{r.EmailAddress?.Address}>") ?? Array.Empty<string>());

        var cc = string.Join("; ",
            msg.CcRecipients?.Select(r =>
                $"{r.EmailAddress?.Name} <{r.EmailAddress?.Address}>") ?? Array.Empty<string>());

        var bcc = string.Join("; ",
            msg.BccRecipients?.Select(r =>
                $"{r.EmailAddress?.Name} <{r.EmailAddress?.Address}>") ?? Array.Empty<string>());

        return new EmailMessage
        {
            Id = msg.Id ?? "",
            Subject = msg.Subject ?? "(No Subject)",
            From = from,
            To = to,
            CcRecipients = cc,
            BccRecipients = bcc,
            Body = msg.Body?.Content ?? "",
            BodyType = msg.Body?.ContentType?.ToString() ?? "Text",
            ReceivedAt = msg.ReceivedDateTime?.DateTime ?? DateTime.UtcNow,
            FolderName = folderName,
            HasAttachments = msg.HasAttachments ?? false,
            IsRead = msg.IsRead ?? false,
            Importance = msg.Importance?.ToString() ?? "Normal",
            ConversationId = msg.ConversationId ?? "",
            InternetMessageId = msg.InternetMessageId ?? "",
            Categories = msg.Categories?.ToList() ?? new List<string>(),
            Attachments = attachments ?? new List<EmailAttachment>()
        };
    }
}

/// <summary>
/// Simple static bearer token provider for Graph SDK.
/// </summary>
internal sealed class StaticTokenProvider : IAccessTokenProvider
{
    private readonly string _token;
    public StaticTokenProvider(string token) => _token = token;

    public Task<string> GetAuthorizationTokenAsync(
        Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_token);

    public AllowedHostsValidator AllowedHostsValidator { get; } =
        new(new[] { "graph.microsoft.com" });
}
