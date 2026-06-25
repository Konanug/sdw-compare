using Microsoft.Data.Sqlite;
var dbPath = args[0];
using var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();

foreach (var table in new[]{"scan_runs","scanned_files","fingerprints","clusters","cluster_members","pair_comparisons"}) {
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
    Console.WriteLine($"{table}: {cmd.ExecuteScalar()} rows");
}
