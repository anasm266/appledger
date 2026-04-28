namespace AppLedger;

internal static class SessionOutputs
{
    public static async Task<IReadOnlyList<string>> WriteAsync(SessionReport session, string outputDirectory, JsonSerializerOptions jsonOptions)
    {
        Directory.CreateDirectory(outputDirectory);
        session = SessionReport.RefreshDerivedData(session);

        var jsonPath = Path.Combine(outputDirectory, "session.json");
        var htmlPath = Path.Combine(outputDirectory, "report.html");
        var csvPath = Path.Combine(outputDirectory, "touched-files.csv");
        var commandsPath = Path.Combine(outputDirectory, "commands.json");
        var aiActivityPath = Path.Combine(outputDirectory, "ai-activity.json");
        var cleanupPath = Path.Combine(outputDirectory, "cleanup.ps1");

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(session, jsonOptions), Encoding.UTF8);
        await File.WriteAllTextAsync(htmlPath, HtmlReport.Render(session), Encoding.UTF8);
        await File.WriteAllTextAsync(csvPath, CsvReport.RenderFiles(session.FileEvents), Encoding.UTF8);
        await File.WriteAllTextAsync(commandsPath, JsonSerializer.Serialize(session.Processes.Where(p => !string.IsNullOrWhiteSpace(p.CommandLine)), jsonOptions), Encoding.UTF8);
        await File.WriteAllTextAsync(aiActivityPath, JsonSerializer.Serialize(session.AiActivity, jsonOptions), Encoding.UTF8);
        await File.WriteAllTextAsync(cleanupPath, CleanupScript.Render(session), Encoding.UTF8);

        return [htmlPath, jsonPath, csvPath, commandsPath, aiActivityPath, cleanupPath];
    }
}

internal static class SessionStore
{
    public static void Write(string path, SessionReport session)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction, """
            CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE processes (process_id INTEGER, parent_process_id INTEGER, name TEXT, executable_path TEXT, command_line TEXT, first_seen TEXT, last_seen TEXT);
            CREATE TABLE file_events (kind TEXT, path TEXT, category TEXT, source TEXT, observed_at TEXT, process_id INTEGER, process_name TEXT, size_delta INTEGER, is_sensitive INTEGER, related_path TEXT);
            CREATE TABLE network_events (process_id INTEGER, local_address TEXT, local_port INTEGER, remote_address TEXT, remote_port INTEGER, state TEXT, first_seen TEXT, remote_host TEXT);
            CREATE TABLE registry_events (kind TEXT, key TEXT, before_value TEXT, after_value TEXT);
            CREATE TABLE findings (severity TEXT, title TEXT, detail TEXT);
            """);

        Insert(connection, transaction, "INSERT INTO metadata(key, value) VALUES ($key, $value)", new()
        {
            ["$key"] = "session_json",
            ["$value"] = JsonSerializer.Serialize(session, ProgramJson.Options)
        });

        foreach (var process in session.Processes)
        {
            Insert(connection, transaction, "INSERT INTO processes VALUES ($pid, $parent, $name, $exe, $cmd, $first, $last)", new()
            {
                ["$pid"] = process.ProcessId,
                ["$parent"] = process.ParentProcessId,
                ["$name"] = process.Name,
                ["$exe"] = process.ExecutablePath,
                ["$cmd"] = process.CommandLine,
                ["$first"] = process.FirstSeen.ToString("O", CultureInfo.InvariantCulture),
                ["$last"] = process.LastSeen.ToString("O", CultureInfo.InvariantCulture)
            });
        }

        foreach (var file in session.FileEvents)
        {
            Insert(connection, transaction, "INSERT INTO file_events VALUES ($kind, $path, $category, $source, $observed, $pid, $pname, $delta, $sensitive, $related)", new()
            {
                ["$kind"] = file.Kind.ToString(),
                ["$path"] = file.Path,
                ["$category"] = file.Category,
                ["$source"] = file.Source,
                ["$observed"] = file.ObservedAt.ToString("O", CultureInfo.InvariantCulture),
                ["$pid"] = file.ProcessId,
                ["$pname"] = file.ProcessName,
                ["$delta"] = file.SizeDelta,
                ["$sensitive"] = file.IsSensitive ? 1 : 0,
                ["$related"] = file.RelatedPath
            });
        }

        foreach (var item in session.NetworkEvents)
        {
            Insert(connection, transaction, "INSERT INTO network_events VALUES ($pid, $local, $lport, $remote, $rport, $state, $first, $host)", new()
            {
                ["$pid"] = item.ProcessId,
                ["$local"] = item.LocalAddress,
                ["$lport"] = item.LocalPort,
                ["$remote"] = item.RemoteAddress,
                ["$rport"] = item.RemotePort,
                ["$state"] = item.State,
                ["$first"] = item.FirstSeen.ToString("O", CultureInfo.InvariantCulture),
                ["$host"] = item.RemoteHost
            });
        }

        foreach (var item in session.RegistryEvents)
        {
            Insert(connection, transaction, "INSERT INTO registry_events VALUES ($kind, $key, $before, $after)", new()
            {
                ["$kind"] = item.Kind.ToString(),
                ["$key"] = item.Key,
                ["$before"] = item.Before,
                ["$after"] = item.After
            });
        }

        foreach (var finding in session.Findings)
        {
            Insert(connection, transaction, "INSERT INTO findings VALUES ($severity, $title, $detail)", new()
            {
                ["$severity"] = finding.Severity,
                ["$title"] = finding.Title,
                ["$detail"] = finding.Detail
            });
        }

        transaction.Commit();
    }

    public static SessionReport? Read(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = 'session_json'";
        var value = command.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<SessionReport>(value, ProgramJson.Options);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, string sql, Dictionary<string, object?> values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (key, value) in values)
        {
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }
}
