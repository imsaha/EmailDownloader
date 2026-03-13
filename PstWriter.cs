using EmailDownloader.Config;
using EmailDownloader.Email;
using System.Text;

namespace EmailDownloader.Pst;

public interface IPstWriter : IDisposable
{
    Task WriteMessageAsync(EmailMessage message, CancellationToken ct = default);
    Task FinalizeAsync(CancellationToken ct = default);
    string GetOutputPath(int year);
    IReadOnlyList<string> GetCreatedFiles();
}

/// <summary>
/// Writes emails as individual .eml files organised by year/folder.
/// No third-party library required — pure RFC 2822 MIME output.
/// </summary>
public sealed class PstWriter : IPstWriter
{
    private readonly DownloadConfig _config;
    private readonly HashSet<string> _createdDirs = new(StringComparer.OrdinalIgnoreCase);

    public PstWriter(DownloadConfig config)
    {
        _config = config;
        Directory.CreateDirectory(config.OutputPath);
    }

    public string GetOutputPath(int year) =>
        _config.GroupByYear
            ? Path.Combine(_config.OutputPath, year.ToString())
            : _config.OutputPath;

    public Task WriteMessageAsync(EmailMessage message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var folderName = string.IsNullOrEmpty(message.FolderName) ? "Inbox" : message.FolderName;
        // FolderName may be a slash-separated path (e.g. "Inbox/Work/Projects").
        // Sanitize each segment individually so subdirectory hierarchy is preserved.
        var segments = folderName.Split('/').Select(SanitizeName).ToArray();
        var dir = Path.Combine(new[] { GetOutputPath(message.Year) }.Concat(segments).ToArray());

        if (_createdDirs.Add(dir))
            Directory.CreateDirectory(dir);

        var filePath = UniqueFilePath(dir, message);
        File.WriteAllText(filePath, BuildEml(message), Encoding.UTF8);
        return Task.CompletedTask;
    }

    private static string UniqueFilePath(string dir, EmailMessage message)
    {
        var dateStr = message.ReceivedAt.ToString("yyyyMMdd_HHmmss");
        var subject = SanitizeName(message.Subject);
        if (subject.Length > 60) subject = subject[..60];

        var baseName = $"{dateStr}_{subject}";
        var path = Path.Combine(dir, baseName + ".eml");

        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(dir, $"{baseName}_{i}.eml");

        return path;
    }

    private static string BuildEml(EmailMessage message)
    {
        var sb = new StringBuilder();

        var date = message.ReceivedAt.ToString("ddd, dd MMM yyyy HH:mm:ss +0000",
            System.Globalization.CultureInfo.InvariantCulture);

        sb.AppendLine($"From: {message.From}");
        sb.AppendLine($"To: {message.To}");
        if (!string.IsNullOrEmpty(message.CcRecipients))
            sb.AppendLine($"CC: {message.CcRecipients}");
        if (!string.IsNullOrEmpty(message.BccRecipients))
            sb.AppendLine($"BCC: {message.BccRecipients}");
        sb.AppendLine($"Subject: {message.Subject}");
        sb.AppendLine($"Date: {date}");
        if (!string.IsNullOrEmpty(message.InternetMessageId))
            sb.AppendLine($"Message-ID: {message.InternetMessageId}");
        if (message.Categories.Count > 0)
            sb.AppendLine($"X-Categories: {string.Join("; ", message.Categories)}");
        if (!string.Equals(message.Importance, "Normal", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine($"Importance: {message.Importance}");
        sb.AppendLine("MIME-Version: 1.0");

        var isHtml = message.BodyType.Equals("Html", StringComparison.OrdinalIgnoreCase)
                  || message.BodyType.Equals("HTML", StringComparison.OrdinalIgnoreCase);
        var bodyContentType = isHtml ? "text/html" : "text/plain";

        var inlineAtts  = message.Attachments.Where(a => a.IsInline).ToList();
        var regularAtts = message.Attachments.Where(a => !a.IsInline).ToList();

        if (inlineAtts.Count == 0 && regularAtts.Count == 0)
        {
            // ── Plain body, no attachments ────────────────────────────────────
            sb.AppendLine($"Content-Type: {bodyContentType}; charset=utf-8");
            sb.AppendLine("Content-Transfer-Encoding: 8bit");
            sb.AppendLine();
            sb.Append(message.Body);
        }
        else if (regularAtts.Count == 0)
        {
            // ── Inline images only → multipart/related ────────────────────────
            var relBoundary = $"----=_Related_{Guid.NewGuid():N}";
            sb.AppendLine($"Content-Type: multipart/related; boundary=\"{relBoundary}\"");
            sb.AppendLine();
            AppendBodyPart(sb, relBoundary, bodyContentType, message.Body);
            foreach (var att in inlineAtts)
                AppendInlinePart(sb, relBoundary, att);
            sb.AppendLine($"--{relBoundary}--");
        }
        else if (inlineAtts.Count == 0)
        {
            // ── Regular attachments only → multipart/mixed ────────────────────
            var mixBoundary = $"----=_Mixed_{Guid.NewGuid():N}";
            sb.AppendLine($"Content-Type: multipart/mixed; boundary=\"{mixBoundary}\"");
            sb.AppendLine();
            AppendBodyPart(sb, mixBoundary, bodyContentType, message.Body);
            foreach (var att in regularAtts)
                AppendRegularPart(sb, mixBoundary, att);
            sb.AppendLine($"--{mixBoundary}--");
        }
        else
        {
            // ── Both inline and regular → multipart/mixed wrapping multipart/related
            var mixBoundary = $"----=_Mixed_{Guid.NewGuid():N}";
            var relBoundary = $"----=_Related_{Guid.NewGuid():N}";

            sb.AppendLine($"Content-Type: multipart/mixed; boundary=\"{mixBoundary}\"");
            sb.AppendLine();

            // Inner multipart/related: body + inline images
            sb.AppendLine($"--{mixBoundary}");
            sb.AppendLine($"Content-Type: multipart/related; boundary=\"{relBoundary}\"");
            sb.AppendLine();
            AppendBodyPart(sb, relBoundary, bodyContentType, message.Body);
            foreach (var att in inlineAtts)
                AppendInlinePart(sb, relBoundary, att);
            sb.AppendLine($"--{relBoundary}--");
            sb.AppendLine();

            // Regular attachments in the outer mixed
            foreach (var att in regularAtts)
                AppendRegularPart(sb, mixBoundary, att);
            sb.AppendLine($"--{mixBoundary}--");
        }

        return sb.ToString();
    }

    private static void AppendBodyPart(StringBuilder sb, string boundary, string contentType, string body)
    {
        sb.AppendLine($"--{boundary}");
        sb.AppendLine($"Content-Type: {contentType}; charset=utf-8");
        sb.AppendLine("Content-Transfer-Encoding: 8bit");
        sb.AppendLine();
        sb.AppendLine(body);
    }

    private static void AppendInlinePart(StringBuilder sb, string boundary, EmailAttachment att)
    {
        sb.AppendLine($"--{boundary}");
        sb.AppendLine($"Content-Type: {att.ContentType}; name=\"{att.Name}\"");
        sb.AppendLine("Content-Transfer-Encoding: base64");
        sb.AppendLine("Content-Disposition: inline" +
            (string.IsNullOrEmpty(att.Name) ? "" : $"; filename=\"{att.Name}\""));
        if (!string.IsNullOrEmpty(att.ContentId))
            sb.AppendLine($"Content-ID: <{att.ContentId.Trim('<', '>')}>");
        sb.AppendLine();
        AppendBase64(sb, att.Content);
    }

    private static void AppendRegularPart(StringBuilder sb, string boundary, EmailAttachment att)
    {
        sb.AppendLine($"--{boundary}");
        sb.AppendLine($"Content-Type: {att.ContentType}; name=\"{att.Name}\"");
        sb.AppendLine("Content-Transfer-Encoding: base64");
        sb.AppendLine($"Content-Disposition: attachment; filename=\"{att.Name}\"");
        sb.AppendLine();
        AppendBase64(sb, att.Content);
    }

    private static void AppendBase64(StringBuilder sb, byte[]? content)
    {
        if (content == null) return;
        var b64 = Convert.ToBase64String(content);
        for (var i = 0; i < b64.Length; i += 76)
            sb.AppendLine(b64.Substring(i, Math.Min(76, b64.Length - i)));
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString().Trim('.', ' ');
    }

    public Task FinalizeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<string> GetCreatedFiles() =>
        _createdDirs.OrderBy(d => d).ToList().AsReadOnly();

    public void Dispose() { }
}
