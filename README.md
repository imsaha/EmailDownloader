# Email Downloader — Microsoft 365 to EML

A .NET 10 console application that authenticates via **OpenID Connect**, downloads all emails from a Microsoft 365 mailbox via **Microsoft Graph API**, and exports them as standard `.eml` files organised by year and folder.

---

## Install

```powershell
irm https://raw.githubusercontent.com/imsaha/EmailDownloader/master/install.ps1 | iex
```

The installer will:
1. Download the latest `emaildl.exe` from GitHub Releases
2. Prompt for your **Azure AD ClientId** and **TenantId** (default: `common`)
3. Write `appsettings.json` with your credentials
4. Add the install directory to your user `PATH`

Re-running the same command updates the exe if a newer version exists; existing config is never overwritten.

---

## Uninstall

```powershell
emaildl uninstall
```

Removes the install directory and cleans up `PATH`.

---

## Features

- **OpenID Connect** authentication via Microsoft Identity (MSAL)
  - Interactive browser login
  - Device code flow (for headless/server environments)
  - **Persistent token cache** — subsequent runs sign in silently without re-opening the browser
- Downloads emails across all (or selected) mail folders
- Exports to **.EML files** (RFC 2822 MIME format) grouped by year and folder
- **Attachments** are downloaded and embedded in each `.eml` file
- **Date range filter** — download only emails within a specified date range
- **Live progress display** with messages downloaded, progress bar, rate, breakdown by year/folder, elapsed time and data size
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

## Azure AD App Registration

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

---

## Manual Setup (build from source)

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A **Microsoft 365** account (personal or work/school)
- An **Azure AD App Registration** (see above)

### Configure

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

### Build & Run

```bash
dotnet build
dotnet run
```

---

## Usage Walkthrough

1. **Launch** — ASCII banner is displayed
2. **Choose auth method** — Use saved session (if available), Browser, or Device Code
3. **Sign in** — Browser opens for Microsoft login (skipped if a saved session exists)
4. **Mailbox scan** — Folders and message counts are shown
5. **Select folders** — Download all or pick specific ones
6. **Date range** — Optionally filter by date
7. **Confirm** — Review what will be downloaded
8. **Delete option** — Choose whether to delete emails from the server after saving
9. **Download** — Live progress table updates in real time
10. **Done** — Summary with output directory locations

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
| `Microsoft.Identity.Client.Extensions.Msal` | Persistent token cache |
| `Microsoft.Graph` | Microsoft Graph API SDK |
| `Spectre.Console` | Rich console UI, live display |
| `Microsoft.Extensions.Configuration` | JSON / env / CLI config |
| `Microsoft.Extensions.DependencyInjection` | Service container |
| `Microsoft.Extensions.Logging` | Logging infrastructure |

---

## Security Notes

- Token cache is stored in `%LOCALAPPDATA%\EmailDownloader\token_cache.bin` and is DPAPI-encrypted on Windows
- No passwords are ever stored or logged
- Without the delete option, only `Mail.Read` (read-only) permission is used
- The delete-after-download option requires `Mail.ReadWrite` and permanently removes messages from the server — use with caution

---

## Import EML files into Outlook

Use Outlook's **File → Open & Export → Import/Export → Import Internet Mail and Addresses** or drag `.eml` files directly into a folder.
