using Spectre.Console;

namespace CodexHistoryFixer;

internal static class TuiApplication
{
    public static int Run(string[] args)
    {
        var interactiveLaunch = args.Length == 0;
        try
        {
            var options = Parse(args);
            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            if (interactiveLaunch &&
                (!AnsiConsole.Profile.Capabilities.Interactive || Console.IsInputRedirected))
            {
                AnsiConsole.MarkupLine(
                    "[yellow]无参数模式需要真实交互终端。自动化运行请使用 --dry-run 或 --yes。[/]");
                return 2;
            }

            return interactiveLaunch
                ? RunInteractive(options)
                : RunCommand(options);
        }
        catch (ArgumentException exception)
        {
            AnsiConsole.MarkupLine($"[red]参数错误：[/]{Markup.Escape(exception.Message)}");
            return FinishWithPause(2, interactiveLaunch);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            Microsoft.Data.Sqlite.SqliteException)
        {
            AnsiConsole.MarkupLine($"[red]无法继续：[/]{Markup.Escape(exception.Message)}");
            return FinishWithPause(1, interactiveLaunch);
        }
        catch (Exception exception)
        {
            AnsiConsole.WriteException(exception, ExceptionFormats.ShortenEverything);
            return FinishWithPause(1, interactiveLaunch);
        }
    }

    private static int RunInteractive(Options options)
    {
        PrintBanner();
        var detectedCodexHome = Path.GetFullPath(options.CodexHome);
        AnsiConsole.MarkupLine("[grey70]检测到 Codex 数据目录[/]");
        AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(detectedCodexHome)}[/]\n");

        const string useDetectedDirectory = "使用此目录";
        const string chooseAnotherDirectory = "手动指定其他目录";
        var directoryChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[grey70]选择数据目录[/]")
                .AddChoices(useDetectedDirectory, chooseAnotherDirectory));

        var codexHome = directoryChoice == useDetectedDirectory
            ? detectedCodexHome
            : AnsiConsole.Prompt(
                new TextPrompt<string>("[grey70]请输入 Codex 数据目录[/]")
                    .Validate(path => Directory.Exists(Path.GetFullPath(path))
                        ? ValidationResult.Success()
                        : ValidationResult.Error("[red]目录不存在。[/]")));

        var service = new MigrationService();
        var analysis = AnalyzeWithStatus(service, codexHome, null);
        PrintAnalysis(analysis);

        if (!HasPendingChanges(analysis))
        {
            AnsiConsole.MarkupLine("\n[green]所有会话已经使用当前 Provider，无需迁移。[/]");
            return FinishWithPause(0, true);
        }

        AnsiConsole.MarkupLine(
            "\n[yellow]活动会话占用的 JSONL 会自动跳过；其余会话与索引将继续迁移。[/]");
        var confirmed = AnsiConsole.Confirm(
            $"将全部会话统一为 [cyan]{Markup.Escape(analysis.TargetProvider)}[/]，并先创建完整备份。继续？",
            false);
        if (!confirmed)
        {
            AnsiConsole.MarkupLine("[grey]已取消，未修改任何数据。[/]");
            return FinishWithPause(2, true);
        }

        var result = MigrateWithProgress(service, analysis);
        PrintResult(result);
        return FinishWithPause(0, true);
    }

    private static int RunCommand(Options options)
    {
        var service = new MigrationService();
        var analysis = AnalyzeWithStatus(service, options.CodexHome, options.Provider);
        PrintAnalysis(analysis);

        if (options.DryRun)
        {
            AnsiConsole.MarkupLine("\n[green]预演完成，未修改任何文件。[/]");
            return 0;
        }

        if (!HasPendingChanges(analysis))
        {
            AnsiConsole.MarkupLine("\n[green]所有会话已经使用当前 Provider，无需迁移。[/]");
            return 0;
        }

        if (!options.AssumeYes &&
            !AnsiConsole.Confirm(
                $"将全部会话统一为 [cyan]{Markup.Escape(analysis.TargetProvider)}[/]。继续？",
                false))
        {
            AnsiConsole.MarkupLine("[grey]已取消，未修改任何数据。[/]");
            return 2;
        }

        var result = MigrateWithProgress(service, analysis);
        PrintResult(result);
        return 0;
    }

    private static MigrationAnalysis AnalyzeWithStatus(
        MigrationService service,
        string codexHome,
        string? provider)
    {
        MigrationAnalysis? analysis = null;
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .Start("正在扫描会话和 SQLite 索引...", _ =>
            {
                analysis = service.Analyze(codexHome, provider);
            });

        return analysis ?? throw new InvalidOperationException("扫描未返回结果。");
    }

    private static MigrationResult MigrateWithProgress(
        MigrationService service,
        MigrationAnalysis analysis)
    {
        MigrationResult? result = null;
        AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .Start(context =>
            {
                var tasks = new Dictionary<string, ProgressTask>(StringComparer.Ordinal);

                void Report(MigrationProgress item)
                {
                    if (!tasks.TryGetValue(item.Stage, out var task))
                    {
                        task = context.AddTask(
                            Markup.Escape(item.Stage),
                            maxValue: Math.Max(1, item.Total));
                        tasks[item.Stage] = task;
                    }

                    task.MaxValue = Math.Max(1, item.Total);
                    task.Value = Math.Clamp(item.Current, 0, Math.Max(1, item.Total));
                    task.Description =
                        $"{Markup.Escape(item.Stage)} [grey]{Markup.Escape(item.Message)}[/]";
                }

                result = service.Migrate(analysis, Report);
                foreach (var task in tasks.Values)
                {
                    task.Value = task.MaxValue;
                }
            });

        return result ?? throw new InvalidOperationException("迁移未返回结果。");
    }

    private static void PrintBanner()
    {
        AnsiConsole.Write(new Rule("[bold cyan]Codex History Fixer[/]")
        {
            Justification = Justify.Left
        });
        AnsiConsole.MarkupLine(
            "[grey]Developed by Desin[/]\n");
    }

    private static void PrintAnalysis(MigrationAnalysis analysis)
    {
        var summary = new Grid();
        summary.AddColumn(new GridColumn().NoWrap());
        summary.AddColumn();
        summary.AddRow("[grey70]Codex 目录[/]", Markup.Escape(analysis.CodexHome));
        summary.AddRow(
            "[grey70]目标 Provider[/]",
            $"[cyan]{Markup.Escape(analysis.TargetProvider)}[/]");
        summary.AddRow("[grey70]会话文件[/]", analysis.SessionFiles.Count.ToString("N0"));
        summary.AddRow(
            "[grey70]占用中[/]",
            analysis.LockedSessionFiles.Count == 0
                ? "[green]0[/]"
                : $"[yellow]{analysis.LockedSessionFiles.Count:N0}[/]");
        summary.AddRow(
            "[grey70]需修改 JSONL[/]",
            FormatPending(analysis.SessionFilesNeedingChange));
        summary.AddRow("[grey70]SQLite 数据库[/]", analysis.StateDatabases.Count.ToString("N0"));
        summary.AddRow(
            "[grey70]需修改 SQLite[/]",
            FormatPending(analysis.SqliteRowsNeedingChange));

        AnsiConsole.Write(new Panel(summary)
        {
            Header = new PanelHeader(" 扫描结果 "),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 0)
        });

        var providers = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("Provider")
            .AddColumn(new TableColumn("会话数").RightAligned());
        foreach (var item in analysis.ProviderCounts
                     .OrderByDescending(item => item.Value)
                     .ThenBy(item => item.Key))
        {
            providers.AddRow(Markup.Escape(item.Key), item.Value.ToString("N0"));
        }
        AnsiConsole.Write(providers);

        if (analysis.SessionFilesWithoutMetadata > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]注意：{analysis.SessionFilesWithoutMetadata:N0} 个 JSONL 没有可读取的 session_meta，将保持不变。[/]");
        }

        if (analysis.LockedSessionFiles.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]注意：{analysis.LockedSessionFiles.Count:N0} 个活动 JSONL 正在被占用，将跳过且不会修改。[/]");
            foreach (var path in analysis.LockedSessionFiles.Take(5))
            {
                AnsiConsole.MarkupLine(
                    $"[grey]  {Markup.Escape(Path.GetRelativePath(analysis.CodexHome, path))}[/]");
            }
        }
    }

    private static void PrintResult(MigrationResult result)
    {
        var results = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("项目")
            .AddColumn("结果")
            .AddRow("JSONL 已修改", result.SessionFilesChanged.ToString("N0"))
            .AddRow("已跳过", result.JsonlFilesSkippedAsLocked.ToString("N0"))
            .AddRow("SQLite 已修改", result.SqliteRowsChanged.ToString("N0"))
            .AddRow("无法读取 metadata", result.SessionFilesWithoutMetadata.ToString("N0"))
            .AddRow("备份目录", Markup.Escape(result.BackupDirectory));

        AnsiConsole.MarkupLine("\n[bold green]迁移完成。[/]");
        AnsiConsole.Write(results);
    }

    private static bool HasPendingChanges(MigrationAnalysis analysis)
    {
        return analysis.SessionFilesNeedingChange > 0 ||
               analysis.SqliteRowsNeedingChange > 0;
    }

    private static string FormatPending(int count)
    {
        return count == 0 ? "[green]0[/]" : $"[yellow]{count:N0}[/]";
    }

    private static int FinishWithPause(int exitCode, bool pause)
    {
        if (pause && !Console.IsInputRedirected)
        {
            AnsiConsole.MarkupLine("\n[grey]按 Enter 退出。[/]");
            Console.ReadLine();
        }

        return exitCode;
    }

    private static Options Parse(string[] args)
    {
        var options = new Options
        {
            CodexHome = MigrationService.GetDefaultCodexHome()
        };

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    break;
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--yes":
                case "-y":
                    options.AssumeYes = true;
                    break;
                case "--provider":
                    options.Provider = ReadValue(args, ref index, "--provider");
                    break;
                case "--codex-home":
                    options.CodexHome = ReadValue(args, ref index, "--codex-home");
                    break;
                default:
                    throw new ArgumentException($"未知参数：{args[index]}");
            }
        }

        return options;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{option} 缺少参数值。");
        }

        return args[index];
    }

    private static void PrintHelp()
    {
        PrintBanner();
        var help = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("参数")
            .AddColumn("说明")
            .AddRow("无参数", "启动交互式 TUI")
            .AddRow("--dry-run", "只扫描，不修改")
            .AddRow("--yes, -y", "跳过迁移确认")
            .AddRow("--provider <name>", "覆盖 config.toml 中的 Provider")
            .AddRow("--codex-home <path>", "指定 Codex 数据目录")
            .AddRow("--help, -h", "显示帮助");
        AnsiConsole.Write(help);
    }

    private sealed class Options
    {
        public string CodexHome { get; set; } = string.Empty;
        public string? Provider { get; set; }
        public bool DryRun { get; set; }
        public bool AssumeYes { get; set; }
        public bool ShowHelp { get; set; }
    }
}
