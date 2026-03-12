using EmailDownloader.Email;
using Spectre.Console;

namespace EmailDownloader.Progress;

public sealed class ConsoleProgressDisplay : IDisposable
{
    private readonly Table _statsTable;
    private readonly object _lock = new();
    private DownloadStats _latest = new();
    private bool _disposed;
    private readonly System.Timers.Timer _refreshTimer;

    // Pre-built display objects
    private readonly Panel _mainPanel;

    public ConsoleProgressDisplay()
    {
        _statsTable = new Table()
            .Expand()
            .BorderStyle(Style.Parse("grey"))
            .AddColumn(new TableColumn("[bold]Metric[/]").Width(25))
            .AddColumn(new TableColumn("[bold]Value[/]"));

        _mainPanel = new Panel(_statsTable)
        {
            Header = new PanelHeader("[bold yellow] 📧 Email Download Progress [/]"),
            Border = BoxBorder.Double,
            BorderStyle = Style.Parse("yellow")
        };

        _refreshTimer = new System.Timers.Timer(500);
        _refreshTimer.Elapsed += (_, _) => Refresh();
    }

    public void StartLive(Action<LiveDisplayContext> setup)
    {
        _refreshTimer.Start();
    }

    public void Update(DownloadStats stats)
    {
        lock (_lock)
        {
            _latest = stats;
        }
    }

    private void Refresh()
    {
        DownloadStats stats;
        lock (_lock) { stats = _latest; }

        // Console.Clear() + redraw approach for live stats
        try
        {
            var elapsed = FormatElapsed(stats.Elapsed);
            var rate = stats.Elapsed.TotalSeconds > 0
                ? (stats.Downloaded / stats.Elapsed.TotalSeconds).ToString("F1")
                : "0";
            var bytesStr = FormatBytes(stats.TotalBytes);

            // Build year breakdown
            var yearBreakdown = stats.ByYear.Count > 0
                ? string.Join("  ", stats.ByYear
                    .OrderByDescending(kv => kv.Key)
                    .Select(kv => $"[cyan]{kv.Key}[/]:[yellow]{kv.Value}[/]"))
                : "[grey]–[/]";

            // Build folder breakdown
            var folderBreakdown = stats.ByFolder.Count > 0
                ? string.Join("\n", stats.ByFolder
                    .OrderByDescending(kv => kv.Value)
                    .Take(5)
                    .Select(kv => $"  [dim]{Truncate(kv.Key, 22)}[/] [white]{kv.Value}[/]"))
                : "[grey]–[/]";

            lock (_lock)
            {
                _statsTable.Rows.Clear();
                _statsTable.AddRow(
                    "[white]✅ Downloaded[/]",
                    $"[bold green]{stats.Downloaded:N0}[/] messages");
                _statsTable.AddRow(
                    "[white]❌ Failed[/]",
                    stats.Failed > 0 ? $"[red]{stats.Failed:N0}[/]" : "[grey]0[/]");
                _statsTable.AddRow(
                    "[white]⏱  Elapsed[/]",
                    $"[cyan]{elapsed}[/]");
                _statsTable.AddRow(
                    "[white]⚡ Rate[/]",
                    $"[yellow]{rate}[/] msg/sec");
                _statsTable.AddRow(
                    "[white]💾 Data[/]",
                    $"[magenta]{bytesStr}[/]");
                _statsTable.AddRow(
                    "[white]📅 By Year[/]",
                    yearBreakdown);
                _statsTable.AddRow(
                    "[white]📁 Folders[/]",
                    folderBreakdown);
            }
        }
        catch { /* Ignore render errors */ }
    }

    public void PrintFinalSummary(DownloadStats stats, IReadOnlyList<string> pstFiles)
    {
        _refreshTimer.Stop();

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold green]✅ Download Complete[/]").RuleStyle("green"));
        AnsiConsole.WriteLine();

        var summaryTable = new Table()
            .Expand()
            .Border(TableBorder.Rounded)
            .BorderStyle(Style.Parse("green"))
            .AddColumn("[bold]Statistic[/]")
            .AddColumn("[bold]Value[/]");

        summaryTable.AddRow("Total Downloaded", $"[bold green]{stats.Downloaded:N0}[/] messages");
        summaryTable.AddRow("Failed", stats.Failed > 0 ? $"[red]{stats.Failed:N0}[/]" : "0");
        summaryTable.AddRow("Total Time", FormatElapsed(stats.Elapsed));
        summaryTable.AddRow("Data Processed", FormatBytes(stats.TotalBytes));

        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();

        if (stats.ByYear.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold cyan]Messages by Year:[/]");
            foreach (var (year, count) in stats.ByYear.OrderByDescending(k => k.Key))
            {
                var bar = new string('█', Math.Min(count / Math.Max(stats.ByYear.Values.Max() / 40, 1), 40));
                AnsiConsole.MarkupLine($"  [white]{year}[/]  [green]{bar}[/] [yellow]{count:N0}[/]");
            }
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine("[bold cyan]Output Folders:[/]");
        foreach (var dir in pstFiles)
        {
            var count = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.eml", SearchOption.AllDirectories).Length
                : 0;
            AnsiConsole.MarkupLine($"  📁 [white]{Path.GetFileName(dir)}[/]  [grey]({count:N0} .eml files)[/]");
            AnsiConsole.MarkupLine($"     [dim]{dir}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule().RuleStyle("grey"));
    }

    private static string FormatElapsed(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m {ts.Seconds:D2}s";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds:D2}s";
        return $"{ts.Seconds}s";
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1_024 => $"{bytes / 1_024.0:F1} KB",
            _ => $"{bytes} B"
        };
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Dispose();
    }
}
