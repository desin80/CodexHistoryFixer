using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodexHistoryFixer;

internal sealed partial class MigrationService
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true
    };

    public static string GetDefaultCodexHome()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
    }

    public MigrationAnalysis Analyze(string codexHome, string? providerOverride = null)
    {
        var resolvedHome = Path.GetFullPath(codexHome);
        if (!Directory.Exists(resolvedHome))
        {
            throw new DirectoryNotFoundException($"Codex 数据目录不存在：{resolvedHome}");
        }

        var targetProvider = string.IsNullOrWhiteSpace(providerOverride)
            ? ReadConfiguredProvider(Path.Combine(resolvedHome, "config.toml"))
            : ValidateProvider(providerOverride);

        var sessionFiles = EnumerateSessionFiles(resolvedHome).ToArray();
        var stateDatabases = Directory
            .EnumerateFiles(resolvedHome, "state_*.sqlite", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sessionFiles.Length == 0 && stateDatabases.Length == 0)
        {
            throw new InvalidOperationException($"{resolvedHome} 中没有找到 Codex 会话或状态数据库。");
        }

        var providerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var lockedSessionFiles = new List<string>();
        var filesNeedingChange = 0;
        var filesWithoutMetadata = 0;

        foreach (var sessionFile in sessionFiles)
        {
            SessionMetadata metadata;
            try
            {
                metadata = ReadSessionMetadata(sessionFile);
            }
            catch (IOException exception) when (IsFileInUse(exception))
            {
                lockedSessionFiles.Add(sessionFile);
                providerCounts["(in use; skipped)"] =
                    providerCounts.GetValueOrDefault("(in use; skipped)") + 1;
                continue;
            }

            var sourceProvider = metadata.HasMetadata
                ? metadata.Provider ?? "(missing)"
                : "(unreadable)";
            providerCounts[sourceProvider] = providerCounts.GetValueOrDefault(sourceProvider) + 1;

            if (!metadata.HasMetadata)
            {
                filesWithoutMetadata++;
            }
            else if (!string.Equals(metadata.Provider, targetProvider, StringComparison.Ordinal))
            {
                filesNeedingChange++;
            }
        }

        var sqliteRowsNeedingChange = 0;
        foreach (var database in stateDatabases)
        {
            sqliteRowsNeedingChange += CountSqliteRowsNeedingChange(database, targetProvider);
        }

        return new MigrationAnalysis(
            resolvedHome,
            targetProvider,
            sessionFiles,
            lockedSessionFiles,
            stateDatabases,
            providerCounts,
            filesNeedingChange,
            filesWithoutMetadata,
            sqliteRowsNeedingChange);
    }

    public MigrationResult Migrate(
        MigrationAnalysis analysis,
        Action<MigrationProgress>? report = null)
    {
        var skippedJsonlFiles = new HashSet<string>(
            analysis.LockedSessionFiles,
            StringComparer.OrdinalIgnoreCase);
        var backupDirectory = CreateBackup(analysis, skippedJsonlFiles, report);
        var changedFiles = 0;
        var filesWithoutMetadata = 0;

        for (var index = 0; index < analysis.SessionFiles.Count; index++)
        {
            var file = analysis.SessionFiles[index];
            if (skippedJsonlFiles.Contains(file))
            {
                continue;
            }

            report?.Invoke(new MigrationProgress(
                "JSONL",
                index + 1,
                analysis.SessionFiles.Count,
                Path.GetFileName(file)));

            SessionUpdateResult update;
            try
            {
                update = UpdateSessionFile(file, analysis.TargetProvider);
            }
            catch (IOException exception) when (IsFileInUse(exception))
            {
                skippedJsonlFiles.Add(file);
                report?.Invoke(new MigrationProgress(
                    "跳过占用",
                    skippedJsonlFiles.Count,
                    skippedJsonlFiles.Count,
                    Path.GetFileName(file)));
                continue;
            }

            if (update.Changed)
            {
                changedFiles++;
            }
            if (!update.HasMetadata)
            {
                filesWithoutMetadata++;
            }
        }

        var sqliteRowsChanged = 0;
        for (var index = 0; index < analysis.StateDatabases.Count; index++)
        {
            var database = analysis.StateDatabases[index];
            report?.Invoke(new MigrationProgress(
                "SQLite",
                index + 1,
                analysis.StateDatabases.Count,
                Path.GetFileName(database)));
            sqliteRowsChanged += UpdateSqliteThreads(database, analysis.TargetProvider);
        }

        WriteManifest(
            backupDirectory,
            analysis,
            changedFiles,
            filesWithoutMetadata,
            sqliteRowsChanged,
            skippedJsonlFiles);

        report?.Invoke(new MigrationProgress("完成", 1, 1, "迁移清单已写入备份目录"));
        return new MigrationResult(
            backupDirectory,
            changedFiles,
            filesWithoutMetadata,
            skippedJsonlFiles.Count,
            sqliteRowsChanged);
    }

    private static string ReadConfiguredProvider(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("没有找到 Codex config.toml。", configPath);
        }

        var match = ModelProviderRegex().Match(File.ReadAllText(configPath));
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"{configPath} 中没有顶层 model_provider。可使用 --provider 显式指定。");
        }

        return ValidateProvider(match.Groups["value"].Value);
    }

    private static string ValidateProvider(string provider)
    {
        var value = provider.Trim();
        if (value.Length == 0 || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("Provider 为空或包含非法控制字符。");
        }

        return value;
    }

    private static IEnumerable<string> EnumerateSessionFiles(string codexHome)
    {
        foreach (var directoryName in new[] { "sessions", "archived_sessions" })
        {
            var directory = Path.Combine(codexHome, directoryName);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(
                         directory,
                         "*.jsonl",
                         SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
    }

    private static SessionMetadata ReadSessionMetadata(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

        for (var lineNumber = 0; lineNumber < 50; lineNumber++)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            if (TryReadSessionMetadata(line, out var provider))
            {
                return new SessionMetadata(true, provider);
            }
        }

        return new SessionMetadata(false, null);
    }

    private static bool TryReadSessionMetadata(string line, out string? provider)
    {
        provider = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) ||
                !string.Equals(type.GetString(), "session_meta", StringComparison.Ordinal) ||
                !root.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (payload.TryGetProperty("model_provider", out var modelProvider) &&
                modelProvider.ValueKind == JsonValueKind.String)
            {
                provider = modelProvider.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static SessionUpdateResult UpdateSessionFile(string path, string targetProvider)
    {
        var metadata = ReadSessionMetadata(path);
        if (!metadata.HasMetadata)
        {
            return new SessionUpdateResult(false, false);
        }

        if (string.Equals(metadata.Provider, targetProvider, StringComparison.Ordinal))
        {
            return new SessionUpdateResult(false, true);
        }

        var temporaryPath = $"{path}.provider-migration-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
        var updated = false;

        try
        {
            using (var reader = new StreamReader(
                       path,
                       Encoding.UTF8,
                       detectEncodingFromByteOrderMarks: true))
            using (var writer = new StreamWriter(
                       temporaryPath,
                       append: false,
                       new UTF8Encoding(false)))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (!updated)
                    {
                        line = UpdateMetadataLine(line, targetProvider, out var lineUpdated);
                        updated = lineUpdated;
                    }

                    writer.WriteLine(line);
                }
            }

            if (!updated)
            {
                return new SessionUpdateResult(false, false);
            }

            try
            {
                File.Replace(temporaryPath, path, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Move(temporaryPath, path, overwrite: true);
            }
            catch (IOException exception) when (!IsFileInUse(exception))
            {
                File.Move(temporaryPath, path, overwrite: true);
            }

            return new SessionUpdateResult(true, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string UpdateMetadataLine(
        string line,
        string targetProvider,
        out bool updated)
    {
        updated = false;
        try
        {
            if (JsonNode.Parse(line) is not JsonObject root ||
                root["type"]?.GetValue<string>() != "session_meta" ||
                root["payload"] is not JsonObject payload)
            {
                return line;
            }

            payload["model_provider"] = targetProvider;
            updated = true;
            return root.ToJsonString(CompactJsonOptions);
        }
        catch (JsonException)
        {
            return line;
        }
        catch (InvalidOperationException)
        {
            return line;
        }
    }

    private static int CountSqliteRowsNeedingChange(
        string databasePath,
        string targetProvider)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        if (!HasThreadsTable(connection))
        {
            return 0;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM threads " +
            "WHERE model_provider IS NULL OR model_provider <> $provider;";
        command.Parameters.AddWithValue("$provider", targetProvider);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int UpdateSqliteThreads(
        string databasePath,
        string targetProvider)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        if (!HasThreadsTable(connection))
        {
            return 0;
        }

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE threads SET model_provider = $provider " +
            "WHERE model_provider IS NULL OR model_provider <> $provider;";
        command.Parameters.AddWithValue("$provider", targetProvider);
        var changed = command.ExecuteNonQuery();
        transaction.Commit();
        return changed;
    }

    private static bool HasThreadsTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master " +
            "WHERE type = 'table' AND name = 'threads';";
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static bool IsFileInUse(IOException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is IOException ioException)
            {
                var errorCode = ioException.HResult & 0xFFFF;
                if (errorCode is 32 or 33)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string CreateBackup(
        MigrationAnalysis analysis,
        ISet<string> skippedJsonlFiles,
        Action<MigrationProgress>? report)
    {
        var parent = Directory.GetParent(analysis.CodexHome)?.FullName
            ?? throw new InvalidOperationException("无法确定备份目录。");
        var backupDirectory = Path.Combine(
            parent,
            "CodexHistoryProviderBackups",
            $"provider-migration-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        Directory.CreateDirectory(backupDirectory);

        var sources = new List<string>();
        foreach (var directoryName in new[] { "sessions", "archived_sessions" })
        {
            var directory = Path.Combine(analysis.CodexHome, directoryName);
            if (Directory.Exists(directory))
            {
                sources.AddRange(Directory.EnumerateFiles(
                    directory,
                    "*",
                    SearchOption.AllDirectories));
            }
        }

        sources.AddRange(Directory.EnumerateFiles(
            analysis.CodexHome,
            "state_*.sqlite*",
            SearchOption.TopDirectoryOnly));

        foreach (var fileName in new[] { "history.jsonl", "session_index.jsonl" })
        {
            var path = Path.Combine(analysis.CodexHome, fileName);
            if (File.Exists(path))
            {
                sources.Add(path);
            }
        }

        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            if (skippedJsonlFiles.Contains(source))
            {
                report?.Invoke(new MigrationProgress(
                    "跳过占用",
                    skippedJsonlFiles.Count,
                    skippedJsonlFiles.Count,
                    Path.GetFileName(source)));
                continue;
            }

            var relativePath = Path.GetRelativePath(analysis.CodexHome, source);
            var destination = Path.Combine(backupDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                File.Copy(source, destination, overwrite: true);
            }
            catch (IOException exception) when (
                string.Equals(Path.GetExtension(source), ".jsonl", StringComparison.OrdinalIgnoreCase) &&
                IsFileInUse(exception))
            {
                skippedJsonlFiles.Add(source);
                report?.Invoke(new MigrationProgress(
                    "跳过占用",
                    skippedJsonlFiles.Count,
                    skippedJsonlFiles.Count,
                    Path.GetFileName(source)));
                continue;
            }

            report?.Invoke(new MigrationProgress(
                "备份",
                index + 1,
                sources.Count,
                relativePath));
        }

        return backupDirectory;
    }

    private static void WriteManifest(
        string backupDirectory,
        MigrationAnalysis analysis,
        int sessionFilesChanged,
        int sessionFilesWithoutMetadata,
        int sqliteRowsChanged,
        IReadOnlyCollection<string> skippedJsonlFiles)
    {
        var manifest = new
        {
            migrated_at = DateTimeOffset.Now,
            codex_home = analysis.CodexHome,
            target_provider = analysis.TargetProvider,
            session_files_found = analysis.SessionFiles.Count,
            session_files_changed = sessionFilesChanged,
            session_files_without_meta = sessionFilesWithoutMetadata,
            jsonl_files_skipped_in_use = skippedJsonlFiles
                .Select(path => Path.GetRelativePath(analysis.CodexHome, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            sqlite_rows_changed = sqliteRowsChanged,
            previous_provider_counts = analysis.ProviderCounts
        };

        var manifestPath = Path.Combine(backupDirectory, "migration-manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, ManifestJsonOptions),
            new UTF8Encoding(false));
    }

    [GeneratedRegex("(?m)^\\s*model_provider\\s*=\\s*[\"'](?<value>[^\"']+)[\"']\\s*(?:#.*)?$")]
    private static partial Regex ModelProviderRegex();

    private sealed record SessionMetadata(bool HasMetadata, string? Provider);
    private sealed record SessionUpdateResult(bool Changed, bool HasMetadata);
}
