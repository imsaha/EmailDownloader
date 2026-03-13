namespace EmailDownloader.Email;

public sealed record EmailMessage
{
    public string Id { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string BodyType { get; init; } = "Text";
    public DateTime ReceivedAt { get; init; }
    public int Year => ReceivedAt.Year;
    public string FolderName { get; init; } = string.Empty;
    public List<EmailAttachment> Attachments { get; init; } = new();
    public bool HasAttachments { get; init; }
    public bool IsRead { get; init; }
    public string Importance { get; init; } = "Normal";
    public string ConversationId { get; init; } = string.Empty;
    public List<string> Categories { get; init; } = new();
    public string InternetMessageId { get; init; } = string.Empty;
    public string CcRecipients { get; init; } = string.Empty;
    public string BccRecipients { get; init; } = string.Empty;
}

public sealed record EmailAttachment
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public byte[]? Content { get; init; }
    public long Size { get; init; }
    public bool IsInline { get; init; }
    public string ContentId { get; init; } = string.Empty;
}

public sealed record MailFolder
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int TotalItemCount { get; init; }
    public int UnreadItemCount { get; init; }
    public string ParentFolderId { get; init; } = string.Empty;
    /// <summary>Full slash-separated path, e.g. "Inbox/Work/Projects".</summary>
    public string Path { get; init; } = string.Empty;
    public int ChildFolderCount { get; init; }
}

public sealed record DownloadStats
{
    public int TotalDiscovered { get; set; }
    public int Downloaded { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public long TotalBytes { get; set; }
    public TimeSpan Elapsed { get; set; }
    public Dictionary<int, int> ByYear { get; set; } = new();
    public Dictionary<string, int> ByFolder { get; set; } = new();
}
