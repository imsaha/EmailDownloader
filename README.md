# Email Downloader — Microsoft 365 to EML

A .NET 10 console application that authenticates via **OpenID Connect**, downloads all emails from a Microsoft 365 mailbox via **Microsoft Graph API**, and exports them as standard `.eml` files organised by year and folder.

---

## Features

- **OpenID Connect** authentication via Microsoft Identity (MSAL)
  - Interactive browser login
  - Device code flow (for headless/server environments)
  - Automatic silent token refresh
- Downloads emails across all (or selected) mail folders
- Exports to **.EML files** (RFC 2822 MIME format) grouped by year and folder
- **Attachments** are downloaded and embedded in each `.eml` file
- **Live progress display** with:
  - Messages downloaded count
  - Progress bar with percentage
  - Rate (messages/second)
  - Breakdown by year and folder
  - Elapsed time and data size
- **Folder selection** — download all folders or pick specific ones
- **Delete after download** — optionally remove emails from the server after saving
- **Confirmation step** before downloading
- Retry logic for transient API failures
- Graceful cancellation with `Ctrl+C`

---

## Output Structure

```text
output/
├── 2024/
│   ├── Inbox/
│   │   ├── 20241215_143022_Meeting notes.eml
│   │   └── 20241210_090011_Project update.eml
│   └── Sent Items/
│       └── 20241201_120000_Re_ Hello.eml
├── 2023/
│   └── Inbox/
│       └── ...
└── ...
```

Each `.eml` file is a standard RFC 2822 MIME message importable by most email clients (Thunderbird, Outlook, Apple Mail, etc.).

---

## Quick Start

### 1. Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A **Microsoft 365** account (personal or work/school)
- An **Azure AD App Registration** (free)

### 2. Azure AD App Registration

1. Go to [portal.azure.com](https://portal.azure.com) → **Azure Active Directory** → **App registrations**
2. Click **New registration**
   - Name: `Email Downloader`
   - Supported account types: **Accounts in any organizational directory and personal Microsoft accounts**
   - Redirect URI: `Public client/native` → `http://localhost`
3. After creation, copy the **Application (client) ID**
4. Go to **API permissions** → **Add a permission** → **Microsoft Graph** → **Delegated**:
   - `Mail.Read` — required for downloading emails
   - `Mail.ReadWrite` — required only if using the "delete after download" option
   - `User.Read`
   - `offline_access`
5. Click **Grant admin consent** (or have the user consent during login)
6. Under **Authentication**, enable **Allow public client flows** → **Yes**

### 3. Configure

Edit `appsettings.json`:

```json
{
  "AzureAd": {
    "ClientId": "YOUR_APPLICATION_CLIENT_ID_HERE",
    "TenantId": "common",
    "RedirectUri": "http://localhost"
  },
  "Download": {
    "OutputPath": "./output",
    "BatchSize": 50,
    "ExcludeFolders": ["Junk Email", "Deleted Items"]
  }
}
```

You can also set values via environment variables (prefix: `EMAILDL_`):

```bash
export EMAILDL_AzureAd__ClientId="your-client-id"
```

### 4. Build & Run

```bash
# Build
dotnet build

# Run
dotnet run
```

---

## Usage Walkthrough

1. **Launch** — ASCII banner is displayed
2. **Choose auth method** — Browser (interactive) or Device Code
3. **Sign in** — Browser opens for Microsoft login
4. **Mailbox scan** — Folders and message counts are shown
5. **Confirm** — Review what will be downloaded; choose all folders or select specific ones
6. **Delete option** — Choose whether to delete emails from the server after saving
7. **Download** — Live progress table updates in real time
8. **Done** — Summary with output directory locations

---

## Configuration Reference

| Setting | Default | Description |
| ------- | ------- | ----------- |
| `AzureAd.ClientId` | *(required)* | Your Azure AD App Client ID |
| `AzureAd.TenantId` | `common` | Tenant ID or `common` for multi-tenant |
| `AzureAd.RedirectUri` | `http://localhost` | Must match App Registration |
| `Download.OutputPath` | `./output` | Where EML files are saved |
| `Download.BatchSize` | `50` | Messages per API page request |
| `Download.MaxRetries` | `3` | Retries on transient failures |
| `Download.RetryDelaySeconds` | `2` | Delay between retries |
| `Download.GroupByYear` | `true` | Create year subdirectories |
| `Download.IncludeFolders` | `[]` | Only these folders (empty = all) |
| `Download.ExcludeFolders` | `[Junk, Deleted]` | Skip these folders |

---

## Dependencies

| Package | Purpose |
| ------- | ------- |
| `Microsoft.Identity.Client` | MSAL — OpenID Connect / OAuth2 |
| `Microsoft.Graph` | Microsoft Graph API SDK |
| `Spectre.Console` | Rich console UI, live display |
| `Microsoft.Extensions.Configuration` | JSON / env / CLI config |
| `Microsoft.Extensions.DependencyInjection` | Service container |
| `Microsoft.Extensions.Logging` | Logging infrastructure |

---

## Security Notes

- Tokens are stored in the MSAL token cache (in-memory by default)
- No passwords are ever stored or logged
- Without the delete option, only `Mail.Read` (read-only) permission is used
- The delete-after-download option requires `Mail.ReadWrite` and permanently removes messages from the server — use with caution
- Add `TokenCacheHelper` for persistent token cache across runs

---

## Extending

### Persistent token cache

```csharp
PublicClientApplicationBuilder.Create(clientId)
    .WithCacheOptions(CacheOptions.EnableSharedCacheOptions)
    ...
```

### Import EML files into Outlook

Use Outlook's **File → Open & Export → Import/Export → Import Internet Mail and Addresses** or drag `.eml` files directly into a folder.

### Filter by date range

In `GraphEmailService.cs`, add a `$filter` query parameter to `GetAsync`:

```csharp
req.QueryParameters.Filter = "receivedDateTime ge 2023-01-01T00:00:00Z";
```
