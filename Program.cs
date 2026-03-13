using EmailDownloader.Auth;
using EmailDownloader.Config;
using EmailDownloader.Email;
using EmailDownloader.Progress;
using EmailDownloader.Pst;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Diagnostics;
using Spectre.Console.Rendering;

namespace EmailDownloader;

static internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length > 0 && args[0].Equals("uninstall", StringComparison.OrdinalIgnoreCase))
            return Uninstall();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            int result;
            do { result = await RunAsync(cts, args); }
            while (result == RestartCode && !cts.IsCancellationRequested);
            return result == RestartCode ? 0 : result;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("\n[yellow]⚠ Cancelled.[/]");
            PressAnyKey();
            return 0;
        }
    }

    private static int Uninstall()
    {
        var installDir = Path.GetDirectoryName(Environment.ProcessPath)!;

        AnsiConsole.Write(new Rule("[bold red]Uninstall emaildl[/]").RuleStyle("red"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[white]Install directory:[/] [grey]{installDir}[/]");
        AnsiConsole.WriteLine();

        var confirm = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]This will delete the install directory and remove it from PATH. Continue?[/]")
                .AddChoices("Yes, uninstall", "No, cancel"));

        if (confirm.StartsWith("No"))
        {
            AnsiConsole.MarkupLine("[grey]Uninstall cancelled.[/]");
            return 0;
        }

        // Remove from user PATH
        var currentPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var entries = currentPath.Split(';').Where(e => !e.TrimEnd('\\', '/').Equals(
            installDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("PATH", string.Join(';', entries), EnvironmentVariableTarget.User);
        AnsiConsole.MarkupLine("[green]Removed from PATH.[/]");

        // Schedule directory deletion after process exits (can't delete the running exe on Windows)
        var bat = Path.Combine(Path.GetTempPath(), "emaildl_uninstall.bat");
        File.WriteAllText(bat,
            $"""
            @echo off
            ping -n 3 127.0.0.1 >nul
            rd /s /q "{installDir}"
            del "%~f0"
            """);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });

        AnsiConsole.MarkupLine("[green]emaildl uninstalled.[/]");
        return 0;
    }

    private const int RestartCode = -99;

    private static async Task<int> RunAsync(CancellationTokenSource cts, string[] args)
    {
        PrintBanner();

        // ── Configuration ─────────────────────────────────────────────────────
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables("EMAILDL_")
            .AddCommandLine(args);

        var configuration = configBuilder.Build();
        var appConfig = new AppConfig
        {
            AzureAd = configuration.GetSection("AzureAd").Get<AzureAdConfig>() ?? new AzureAdConfig(),
            Download = configuration.GetSection("Download").Get<DownloadConfig>() ?? new DownloadConfig()
        };

        // Validate config
        if (string.IsNullOrWhiteSpace(appConfig.AzureAd.ClientId) ||
            appConfig.AzureAd.ClientId == "YOUR_CLIENT_ID_HERE")
        {
            AnsiConsole.MarkupLine("[bold red]❌ Error: Azure AD ClientId is not configured.[/]");
            AnsiConsole.MarkupLine("[yellow]Edit appsettings.json and set your Azure AD App Registration ClientId.[/]");
            AnsiConsole.MarkupLine("\n[grey]See README.md for setup instructions.[/]");
            PressAnyKey();
            return 1;
        }

        // ── Auth Flow Selection ────────────────────────────────────────────────
        AnsiConsole.WriteLine();

        var cachedEmail = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("grey"))
            .StartAsync("Checking for saved session...",
                async _ => await Auth.TokenCacheHelper.GetCachedAccountEmailAsync(appConfig.AzureAd));

        var authChoices = new List<string>();
        if (cachedEmail != null)
            authChoices.Add($"⚡  Use saved session ({cachedEmail})");
        authChoices.Add("🌐  Browser (Interactive - Recommended)");
        authChoices.Add("📱  Device Code (for headless/server environments)");
        authChoices.Add("❌  Exit");

        var authMethod = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Choose authentication method:[/]")
                .AddChoices(authChoices));

        if (authMethod.StartsWith("❌")) return 0;

        // ── Services Setup ─────────────────────────────────────────────────────
        var services = new ServiceCollection();

        services.AddLogging(b => b
            .AddConsole()
            .SetMinimumLevel(LogLevel.Warning));

        services.AddSingleton(appConfig);
        services.AddSingleton(appConfig.AzureAd);
        services.AddSingleton(appConfig.Download);

        if (authMethod.StartsWith("📱"))
            services.AddSingleton<IAuthService, DeviceCodeAuthService>();
        else
            services.AddSingleton<IAuthService, MsalAuthService>(); // covers browser + saved session

        services.AddSingleton<IEmailService, GraphEmailService>();
        services.AddSingleton<ConsoleProgressDisplay>();

        var sp = services.BuildServiceProvider();
        var auth = sp.GetRequiredService<IAuthService>();
        var emailService = sp.GetRequiredService<IEmailService>();

        // ── Authentication ─────────────────────────────────────────────────────
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold cyan]Step 1 — Authentication[/]").RuleStyle("cyan"));

        AuthResult authResult;
        try
        {
            authResult = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync("Waiting for sign-in...", async _ =>
                    await auth.AuthenticateAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("\n[yellow]⚠ Cancelled.[/]");
            PressAnyKey();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]❌ Authentication failed: {ex.Message}[/]");
            return 1;
        }

        AnsiConsole.WriteLine();
        PrintUserCard(authResult);

        // ── Discover Folders ───────────────────────────────────────────────────
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold cyan]Step 2 — Discovering Mailbox[/]").RuleStyle("cyan"));

        List<Email.MailFolder> folders;
        try
        {
            folders = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync("Scanning mail folders...", async _ =>
                    await emailService.GetFoldersAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("\n[yellow]⚠ Cancelled.[/]");
            PressAnyKey();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]❌ Failed to access mailbox: {ex.Message}[/]");
            return 1;
        }

        if (folders.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]⚠ No accessible folders found (check Mail.Read permission).[/]");
            return 1;
        }

        PrintFolderSummary(folders);

        var outputPath = Path.GetFullPath(appConfig.Download.OutputPath);

        // ── Navigable wizard (steps 3-6) ──────────────────────────────────────
        // step 0 = folder choice, 1 = specific picker, 2 = date range,
        // 3 = confirm, 4 = delete choice  (step 5 = done → exit loop)
        List<Email.MailFolder> wizardFolders = folders.ToList();
        bool specificFolders = false;
        DateTimeOffset? fromDate = null;
        DateTimeOffset? untilDate = null;
        bool deleteAfterDownload = false;
        int step = 0;

        while (step <= 4)
        {
            switch (step)
            {
                // ── 0: Which folders? ──────────────────────────────────────────
                case 0:
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.Write(new Rule("[bold yellow]Step 3 — Options[/]").RuleStyle("yellow"));
                    AnsiConsole.WriteLine();

                    var choice = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[bold]Which folders do you want to download?[/]")
                            .AddChoices(
                                "✅  All folders",
                                "🔍  Let me choose specific folders",
                                "❌  Cancel",
                                "🔄  Start again"));

                    if (choice.StartsWith("❌")) return 0;
                    if (choice.StartsWith("🔄")) return RestartCode;

                    specificFolders = choice.StartsWith("🔍");
                    wizardFolders = folders.ToList(); // reset any prior selection
                    step++;
                    break;
                }

                // ── 1: Specific folder multi-picker ────────────────────────────
                case 1:
                {
                    if (!specificFolders) { step++; break; }

                    var orderedFolders = folders.OrderBy(f => f.Path).ToList();
                    var displayStrings = orderedFolders.Select(f =>
                    {
                        var depth = f.Path.Count(c => c == '/');
                        return $"{new string(' ', depth * 2)}{f.DisplayName} ({f.TotalItemCount:N0} messages)";
                    }).ToList();

                    var selected = AnsiConsole.Prompt(
                        new MultiSelectionPrompt<string>()
                            .Title("Select folders to download [grey](child folders included automatically)[/]:")
                            .InstructionsText("[grey](Space to select, Enter to confirm)[/]")
                            .AddChoices(displayStrings));

                    var selectedPaths = orderedFolders
                        .Where((_, i) => selected.Contains(displayStrings[i]))
                        .Select(f => f.Path)
                        .ToHashSet();

                    var picked = folders
                        .Where(f => selectedPaths.Any(p => f.Path == p || f.Path.StartsWith(p + "/")))
                        .ToList();

                    if (picked.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[yellow]No folders selected.[/]");
                        continue; // re-show picker
                    }

                    // Post-selection nav — multi-select has no room for nav items
                    var nav = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title($"[bold]{picked.Count} folder(s) selected — continue?[/]")
                            .AddChoices(
                                $"✅  Continue with {picked.Count} folder(s)",
                                "↩  Back",
                                "🔄  Start again"));

                    if (nav.StartsWith("🔄")) return RestartCode;
                    if (nav.StartsWith("↩")) { step--; break; }

                    wizardFolders = picked;
                    step++;
                    break;
                }

                // ── 2: Date range ──────────────────────────────────────────────
                case 2:
                {
                    AnsiConsole.WriteLine();
                    var useDateFilter = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[bold]Filter emails by date range?[/]")
                            .AddChoices(
                                "📅  Yes, specify a date range",
                                "⏭️   No, download all dates",
                                "↩  Back",
                                "🔄  Start again"));

                    if (useDateFilter.StartsWith("🔄")) return RestartCode;
                    if (useDateFilter.StartsWith("↩"))
                    {
                        fromDate = null; untilDate = null;
                        step = specificFolders ? 1 : 0;
                        break;
                    }

                    fromDate = null;
                    untilDate = null;

                    if (useDateFilter.StartsWith("📅"))
                    {
                        var (fd, fbBack, fbRestart) = PromptDate(
                            "Enter [cyan]start date[/] (from, inclusive)", allowEmpty: true,
                            hint: "blank = no lower bound  •  'back' to go back");
                        if (fbRestart) return RestartCode;
                        if (fbBack) break; // re-show this step

                        var (rd, rbBack, rbRestart) = PromptDate(
                            "Enter [cyan]end date[/] (until, inclusive)", allowEmpty: true,
                            hint: "blank = no upper bound  •  'back' to go back");
                        if (rbRestart) return RestartCode;
                        if (rbBack) break; // re-show this step

                        fromDate = fd;
                        untilDate = rd.HasValue ? rd.Value.AddDays(1).AddSeconds(-1) : null;

                        if (fromDate.HasValue && untilDate.HasValue && fromDate > untilDate)
                        {
                            AnsiConsole.MarkupLine("[yellow]⚠  Start date is after end date — swapping them.[/]");
                            (fromDate, untilDate) = (untilDate, fromDate);
                        }
                    }

                    step++;
                    break;
                }

                // ── 3: Confirmation panel ──────────────────────────────────────
                case 3:
                {
                    AnsiConsole.WriteLine();

                    var totalMessages = wizardFolders.Sum(f => f.TotalItemCount);
                    var dateFiltered = fromDate.HasValue || untilDate.HasValue;
                    var messagesDisplay = dateFiltered
                        ? $"[bold yellow]≤{totalMessages:N0}[/] [dim](date filter will reduce this)[/]"
                        : $"[bold yellow]{totalMessages:N0}[/]";

                    var dateRangeDisplay = (fromDate, untilDate) switch
                    {
                        (not null, not null) => $"[cyan]{fromDate.Value:yyyy-MM-dd}[/] → [cyan]{untilDate.Value:yyyy-MM-dd}[/]",
                        (not null, null)     => $"[cyan]{fromDate.Value:yyyy-MM-dd}[/] → [dim](no end)[/]",
                        (null, not null)     => $"[dim](no start)[/] → [cyan]{untilDate.Value:yyyy-MM-dd}[/]",
                        _                    => "[dim]all dates[/]"
                    };

                    AnsiConsole.Write(new Panel(
                        $"[white]Account:[/]  [bold cyan]{authResult.UserEmail}[/]\n" +
                        $"[white]Folders:[/]  [bold]{wizardFolders.Count}[/]\n" +
                        $"[white]Messages:[/] {messagesDisplay}\n" +
                        $"[white]Dates:[/]    {dateRangeDisplay}\n" +
                        $"[white]Output:[/]   [dim]{outputPath}[/]\n" +
                        $"[white]Format:[/]   [bold].EML[/] files grouped by year/folder")
                    {
                        Header = new PanelHeader("[bold]⚠  Ready to Download[/]"),
                        Border = BoxBorder.Double,
                        BorderStyle = Style.Parse("yellow"),
                        Padding = new Padding(2, 1)
                    });

                    AnsiConsole.WriteLine();

                    var confirmed = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[bold]Do you want to start downloading?[/]")
                            .AddChoices(
                                "✅  Yes, start downloading",
                                "↩  Back",
                                "🔄  Start again"));

                    if (confirmed.StartsWith("🔄")) return RestartCode;
                    if (confirmed.StartsWith("↩")) { step--; break; }

                    step++;
                    break;
                }

                // ── 4: Delete after download ───────────────────────────────────
                case 4:
                {
                    AnsiConsole.WriteLine();
                    var deleteChoice = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[bold]Delete emails from server after downloading?[/]")
                            .AddChoices(
                                "🚫  No, keep emails on server (recommended)",
                                "🗑️   Yes, delete from server after download",
                                "↩  Back",
                                "🔄  Start again"));

                    if (deleteChoice.StartsWith("🔄")) return RestartCode;
                    if (deleteChoice.StartsWith("↩")) { step--; break; }

                    deleteAfterDownload = deleteChoice.StartsWith("🗑");

                    if (deleteAfterDownload)
                    {
                        AnsiConsole.MarkupLine("[bold red]⚠  Emails will be permanently deleted from the server after download.[/]");
                        var confirmDelete = AnsiConsole.Prompt(
                            new SelectionPrompt<string>()
                                .Title("[bold red]Are you absolutely sure?[/]")
                                .AddChoices(
                                    "✅  Yes, I understand — proceed",
                                    "↩  Back",
                                    "🔄  Start again"));

                        if (confirmDelete.StartsWith("🔄")) return RestartCode;
                        if (confirmDelete.StartsWith("↩")) { deleteAfterDownload = false; break; } // re-show step 4
                    }

                    step++;
                    break;
                }
            }
        }

        folders = wizardFolders;

        // ── Download Loop ──────────────────────────────────────────────────────
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold green]Step 4 — Downloading[/]").RuleStyle("green"));
        AnsiConsole.WriteLine();

        var stats = new DownloadStats
        {
            TotalDiscovered = folders.Sum(f => f.TotalItemCount)
        };

        using var pstWriter = new PstWriter(appConfig.Download);
        using var display = new ConsoleProgressDisplay();

        var sw = Stopwatch.StartNew();

        var progressReporter = new System.Progress<DownloadStats>(s =>
        {
            stats = s;
            display.Update(s);
        });

        // Live display table
        await AnsiConsole.Live(BuildLiveLayout(stats))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .StartAsync(async ctx =>
            {
                var liveProgress = new System.Progress<DownloadStats>(s =>
                {
                    stats = s;
                    ctx.UpdateTarget(BuildLiveLayout(s));
                });

                try
                {
                    await foreach (var message in emailService.GetMessagesAsync(
                        folders, liveProgress, fromDate, untilDate, cts.Token))
                    {
                        await pstWriter.WriteMessageAsync(message, cts.Token);

                        if (deleteAfterDownload)
                        {
                            try
                            {
                                await emailService.DeleteMessageAsync(message.Id, cts.Token);
                            }
                            catch
                            {
                                // Non-fatal: deletion failure does not abort the download
                            }
                        }

                        // Update size estimate
                        stats = stats with
                        {
                            TotalBytes = stats.TotalBytes +
                                         System.Text.Encoding.UTF8.GetByteCount(message.Body) +
                                         message.Subject.Length * 2
                        };
                    }
                }
                catch (OperationCanceledException)
                {
                    AnsiConsole.MarkupLine("\n[yellow]⚠ Download interrupted by user.[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"\n[red]❌ Error during download: {ex.Message}[/]");
                }
            });

        sw.Stop();
        stats = stats with { Elapsed = sw.Elapsed };

        // ── Finalize PST files ─────────────────────────────────────────────────
        AnsiConsole.MarkupLine("[grey]Finalizing output...[/]");
        await pstWriter.FinalizeAsync();

        var createdFiles = pstWriter.GetCreatedFiles();
        display.PrintFinalSummary(stats, createdFiles);

        PressAnyKey();
        return 0;
    }

    private static IRenderable BuildLiveLayout(DownloadStats stats)
    {
        var elapsed = stats.Elapsed.TotalSeconds > 0 ? stats.Elapsed : TimeSpan.Zero;
        var rate = elapsed.TotalSeconds > 1
            ? (stats.Downloaded / elapsed.TotalSeconds).ToString("F1")
            : "0.0";

        var table = new Table()
            .Expand()
            .Border(TableBorder.Rounded)
            .BorderStyle(Style.Parse("cyan"))
            .AddColumn(new TableColumn("[bold grey]Metric[/]").Width(26))
            .AddColumn("[bold grey]Value[/]");

        table.AddRow("[white]📨 Downloaded[/]",
            $"[bold green]{stats.Downloaded:N0}[/] messages");

        if (stats.TotalDiscovered > 0)
        {
            var pct = (double)stats.Downloaded / stats.TotalDiscovered * 100;
            var bar = BuildBar(pct, 30);
            table.AddRow("[white]📊 Progress[/]",
                $"{bar} [yellow]{pct:F1}%[/] [dim]({stats.TotalDiscovered:N0} total)[/]");
        }

        table.AddRow("[white]❌ Failed[/]",
            stats.Failed > 0 ? $"[red]{stats.Failed:N0}[/]" : "[grey]0[/]");
        table.AddRow("[white]⏱  Elapsed[/]",
            $"[cyan]{FormatElapsed(elapsed)}[/]");
        table.AddRow("[white]⚡ Rate[/]",
            $"[yellow]{rate}[/] msg/sec");
        table.AddRow("[white]💾 Size[/]",
            $"[magenta]{FormatBytes(stats.TotalBytes)}[/]");

        if (stats.ByYear.Count > 0)
        {
            var yearLine = string.Join("   ", stats.ByYear
                .OrderByDescending(kv => kv.Key)
                .Take(6)
                .Select(kv => $"[bold cyan]{kv.Key}[/]:[yellow]{kv.Value:N0}[/]"));
            table.AddRow("[white]📅 By Year[/]", yearLine);
        }

        if (stats.ByFolder.Count > 0)
        {
            var topFolders = stats.ByFolder
                .OrderByDescending(kv => kv.Value)
                .Take(4)
                .Select(kv => $"[dim]{Truncate(kv.Key, 18)}[/]:[white]{kv.Value:N0}[/]");
            table.AddRow("[white]📁 Top Folders[/]", string.Join("   ", topFolders));
        }

        return new Panel(table)
        {
            Header = new PanelHeader("[bold yellow] 📧 Email Downloader — Live Progress [/]"),
            Border = BoxBorder.Double,
            BorderStyle = Style.Parse("yellow"),
            Padding = new Padding(0)
        };
    }

    private static string BuildBar(double percent, int width)
    {
        var filled = (int)(percent / 100.0 * width);
        filled = Math.Clamp(filled, 0, width);
        var empty = width - filled;
        return $"[green]{new string('█', filled)}[/][grey]{new string('░', empty)}[/]";
    }

    private static void PrintBanner()
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText("Email DL")
            .Centered()
            .Color(Color.Yellow));

        AnsiConsole.Write(new Rule("[bold yellow]📧 Microsoft 365 Email Downloader v1.0[/]").RuleStyle("grey"));
        AnsiConsole.MarkupLine("[grey]Downloads all emails to local PST files organised by year[/]");
        AnsiConsole.Write(new Rule().RuleStyle("grey"));
        AnsiConsole.WriteLine();
    }

    private static void PrintUserCard(AuthResult auth)
    {
        AnsiConsole.Write(new Panel(
            $"[bold green]✅ Authenticated Successfully[/]\n\n" +
            $"[white]Name:[/]  [bold]{auth.UserName}[/]\n" +
            $"[white]Email:[/] [bold cyan]{auth.UserEmail}[/]\n" +
            $"[white]Token valid until:[/] [dim]{auth.ExpiresOn:HH:mm:ss} UTC[/]")
        {
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse("green"),
            Padding = new Padding(2, 1)
        });
    }

    private static void PrintFolderSummary(List<Email.MailFolder> folders)
    {
        var table = new Table()
            .Border(TableBorder.Simple)
            .BorderStyle(Style.Parse("grey"))
            .AddColumn("[bold]Folder[/]")
            .AddColumn("[bold]Total[/]", c => c.RightAligned())
            .AddColumn("[bold]Unread[/]", c => c.RightAligned());

        foreach (var f in folders.OrderBy(x => x.Path))
        {
            var depth = f.Path.Count(c => c == '/');
            var indent = new string(' ', depth * 2);
            table.AddRow(
                $"[white]{indent}{f.DisplayName}[/]",
                $"[yellow]{f.TotalItemCount:N0}[/]",
                f.UnreadItemCount > 0
                    ? $"[cyan]{f.UnreadItemCount:N0}[/]"
                    : "[grey]0[/]");
        }

        table.AddEmptyRow();
        table.AddRow(
            "[bold]TOTAL[/]",
            $"[bold green]{folders.Sum(f => f.TotalItemCount):N0}[/]",
            $"[bold cyan]{folders.Sum(f => f.UnreadItemCount):N0}[/]");

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
    }

    private static void PressAnyKey()
    {
        AnsiConsole.MarkupLine("\n[grey]Press any key to exit...[/]");
        Console.ReadKey(true);
    }

    private static string FormatElapsed(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m {ts.Seconds:D2}s";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds:D2}s";
        return $"{ts.Seconds}s";
    }

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1_024 => $"{bytes / 1_024.0:F1} KB",
            _ => $"{bytes} B"
        };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private static (DateTimeOffset? Value, bool GoBack, bool Restart) PromptDate(
        string title, bool allowEmpty, string hint = "")
    {
        var hintText = hint.Length > 0 ? $" [grey]({hint})[/]" : "";
        while (true)
        {
            var input = AnsiConsole.Prompt(
                new TextPrompt<string>($"{title}{hintText} [[yyyy-MM-dd]]:")
                    .AllowEmpty());

            var trimmed = input.Trim().ToLowerInvariant();
            if (trimmed is "back" or "b") return (null, true, false);
            if (trimmed is "restart" or "start again" or "r") return (null, false, true);

            if (string.IsNullOrWhiteSpace(input) && allowEmpty)
                return (null, false, false);

            if (DateOnly.TryParseExact(input.Trim(), "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var date))
            {
                return (new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero), false, false);
            }

            AnsiConsole.MarkupLine("[red]Invalid date. Use format yyyy-MM-dd (e.g. 2023-06-15).[/]");
        }
    }
}
