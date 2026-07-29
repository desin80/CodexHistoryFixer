using System.Text.Json.Serialization;

namespace CodexHistoryFixer;

internal sealed record MigrationAnalysis(
    string CodexHome,
    string TargetProvider,
    IReadOnlyList<string> SessionFiles,
    IReadOnlyList<string> LockedSessionFiles,
    IReadOnlyList<string> StateDatabases,
    IReadOnlyDictionary<string, int> ProviderCounts,
    int SessionFilesNeedingChange,
    int SessionFilesWithoutMetadata,
    int SqliteRowsNeedingChange);

internal sealed record MigrationResult(
    string BackupDirectory,
    int SessionFilesChanged,
    int SessionFilesWithoutMetadata,
    int JsonlFilesSkippedAsLocked,
    int SqliteRowsChanged);

internal sealed record MigrationProgress(
    string Stage,
    int Current,
    int Total,
    string Message);

internal sealed record MigrationManifest(
    DateTimeOffset MigratedAt,
    string CodexHome,
    string TargetProvider,
    int SessionFilesFound,
    int SessionFilesChanged,
    int SessionFilesWithoutMeta,
    string[] JsonlFilesSkippedInUse,
    int SqliteRowsChanged,
    Dictionary<string, int> PreviousProviderCounts);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(MigrationManifest))]
internal sealed partial class MigrationJsonContext : JsonSerializerContext
{
}
