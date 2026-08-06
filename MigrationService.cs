using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodexHistoryFixer;

internal sealed partial class MigrationService
{
    private const string DefaultProvider = "openai";

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
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

        var configPath = Path.Combine(resolvedHome, "config.toml");
        var targetProvider = string.IsNullOrWhiteSpace(providerOverride)
            ? ReadConfiguredProvider(configPath)
            : ValidateProvider(providerOverride);
        var targetModel = ReadConfiguredModel(configPath);

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
                metadata = ReadSessionMetadata(sessionFile, targetModel);
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
            else if (!string.Equals(metadata.Provider, targetProvider, StringComparison.Ordinal) ||
                     metadata.ModelNeedsChange)
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
            targetModel,
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
                update = UpdateSessionFile(
                    file,
                    analysis.TargetProvider,
                    analysis.TargetModel);
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
            return DefaultProvider;
        }

        var match = ModelProviderRegex().Match(ReadRootConfig(configPath));
        if (!match.Success)
        {
            return DefaultProvider;
        }

        return ValidateProvider(match.Groups["value"].Value);
    }

    private static string? ReadConfiguredModel(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        var match = ModelRegex().Match(ReadRootConfig(configPath));
        return match.Success
            ? ValidateModel(match.Groups["value"].Value)
            : null;
    }

    private static string ReadRootConfig(string configPath)
    {
        return string.Join(
            '\n',
            File.ReadLines(configPath)
                .TakeWhile(line => !line.TrimStart().StartsWith("[", StringComparison.Ordinal)));
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

    private static string ValidateModel(string model)
    {
        var value = model.Trim();
        if (value.Length == 0 || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("全局默认模型为空或包含非法控制字符。");
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

    private static SessionMetadata ReadSessionMetadata(string path, string? targetModel)
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

        var hasMetadata = false;
        string? provider = null;
        var modelNeedsChange = false;

        while (reader.ReadLine() is { } line)
        {
            var record = ReadSessionRecord(line);
            if (!hasMetadata && record.Type == "session_meta")
            {
                hasMetadata = true;
                provider = record.Provider;
            }

            if (targetModel is not null &&
                record.Type == "turn_context" &&
                !string.Equals(record.Model, targetModel, StringComparison.Ordinal))
            {
                modelNeedsChange = true;
            }

            if (hasMetadata && (targetModel is null || modelNeedsChange))
            {
                break;
            }
        }

        return new SessionMetadata(hasMetadata, provider, modelNeedsChange);
    }

    private static SessionRecord ReadSessionRecord(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var type = typeElement.GetString();
            if (string.Equals(type, "session_meta", StringComparison.Ordinal))
            {
                var provider = payload.TryGetProperty("model_provider", out var modelProvider) &&
                               modelProvider.ValueKind == JsonValueKind.String
                    ? modelProvider.GetString()
                    : null;
                return new SessionRecord(type, provider, null);
            }

            if (string.Equals(type, "turn_context", StringComparison.Ordinal))
            {
                var model = payload.TryGetProperty("model", out var modelElement) &&
                            modelElement.ValueKind == JsonValueKind.String
                    ? modelElement.GetString()
                    : null;
                return new SessionRecord(type, null, model);
            }

            return default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static SessionUpdateResult UpdateSessionFile(
        string path,
        string targetProvider,
        string? targetModel)
    {
        var metadata = ReadSessionMetadata(path, targetModel);
        if (!metadata.HasMetadata)
        {
            return new SessionUpdateResult(false, false);
        }

        if (string.Equals(metadata.Provider, targetProvider, StringComparison.Ordinal) &&
            !metadata.ModelNeedsChange)
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
                    line = UpdateSessionLine(
                        line,
                        targetProvider,
                        targetModel,
                        out var lineUpdated);
                    updated |= lineUpdated;

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

    private static string UpdateSessionLine(
        string line,
        string targetProvider,
        string? targetModel,
        out bool updated)
    {
        updated = false;
        try
        {
            if (JsonNode.Parse(line) is not JsonObject root ||
                root["payload"] is not JsonObject payload)
            {
                return line;
            }

            var type = root["type"]?.GetValue<string>();
            if (type == "session_meta" &&
                ReadString(payload, "model_provider") != targetProvider)
            {
                payload["model_provider"] = targetProvider;
                updated = true;
            }
            else if (type == "turn_context" &&
                     targetModel is not null &&
                     ReadString(payload, "model") != targetModel)
            {
                payload["model"] = targetModel;
                updated = true;
            }

            if (!updated)
            {
                return line;
            }

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

    private static string? ReadString(JsonObject value, string propertyName)
    {
        return value[propertyName] is JsonValue property &&
               property.TryGetValue<string>(out var result)
            ? result
            : null;
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
        var manifest = new MigrationManifest(
            DateTimeOffset.Now,
            analysis.CodexHome,
            analysis.TargetProvider,
            analysis.TargetModel,
            analysis.SessionFiles.Count,
            sessionFilesChanged,
            sessionFilesWithoutMetadata,
            skippedJsonlFiles
                .Select(path => Path.GetRelativePath(analysis.CodexHome, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            sqliteRowsChanged,
            analysis.ProviderCounts.ToDictionary());

        var manifestPath = Path.Combine(backupDirectory, "migration-manifest.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, MigrationJsonContext.Default.MigrationManifest),
            new UTF8Encoding(false));
    }

    [GeneratedRegex("(?m)^\\s*model_provider\\s*=\\s*[\"'](?<value>[^\"']+)[\"']\\s*(?:#.*)?$")]
    private static partial Regex ModelProviderRegex();

    [GeneratedRegex("(?m)^\\s*model\\s*=\\s*[\"'](?<value>[^\"']+)[\"']\\s*(?:#.*)?$")]
    private static partial Regex ModelRegex();

    private readonly record struct SessionRecord(string? Type, string? Provider, string? Model);
    private sealed record SessionMetadata(
        bool HasMetadata,
        string? Provider,
        bool ModelNeedsChange);
    private sealed record SessionUpdateResult(bool Changed, bool HasMetadata);
}
