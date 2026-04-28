namespace AppLedger;

internal static class ProcessCatalog
{
    public static ProcessRecord? Resolve(string query)
    {
        if (int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
        {
            return Find(null).FirstOrDefault(process => process.ProcessId == pid) ?? TryReadByPid(pid);
        }

        return SelectBestRoot(query, Find(query).ToList());
    }

    public static IEnumerable<ProcessRecord> Find(string? query)
    {
        var processes = WmiProcess.ReadAll()
            .Select(process => process.ToRecord())
            .OrderBy(process => Score(process, query))
            .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(process => process.ProcessId)
            .AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            processes = processes.Where(process =>
                process.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || process.ExecutablePath?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                || process.CommandLine?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                || process.ProcessId.ToString(CultureInfo.InvariantCulture).Equals(query, StringComparison.OrdinalIgnoreCase));
        }

        return processes;
    }

    private static int Score(ProcessRecord process, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 10;
        }

        if (process.ProcessId.ToString(CultureInfo.InvariantCulture).Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (process.Name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (Path.GetFileNameWithoutExtension(process.Name).Equals(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (process.ExecutablePath?.EndsWith(query + ".exe", StringComparison.OrdinalIgnoreCase) == true) return 3;
        if (process.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }

    internal static ProcessRecord? SelectBestRoot(string query, IReadOnlyList<ProcessRecord> matches)
    {
        if (matches.Count == 0)
        {
            return null;
        }

        var matchedPids = matches
            .Select(process => process.ProcessId)
            .ToHashSet();

        return matches
            .OrderBy(process => matchedPids.Contains(process.ParentProcessId) ? 1 : 0)
            .ThenBy(process => Score(process, query))
            .ThenByDescending(process => CountDescendants(process.ProcessId, matches))
            .ThenBy(process => process.ProcessId)
            .FirstOrDefault();
    }

    private static int CountDescendants(int rootPid, IReadOnlyList<ProcessRecord> processes)
    {
        var childrenByParent = processes
            .GroupBy(process => process.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.Select(process => process.ProcessId).ToList());
        var count = 0;
        var pending = new Stack<int>();
        pending.Push(rootPid);

        while (pending.Count > 0)
        {
            var parent = pending.Pop();
            if (!childrenByParent.TryGetValue(parent, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                count++;
                pending.Push(child);
            }
        }

        return count;
    }

    private static ProcessRecord? TryReadByPid(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new ProcessRecord(
                process.Id,
                0,
                process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process.ProcessName : process.ProcessName + ".exe",
                TryGet(() => process.MainModule?.FileName),
                null,
                TryGet(() => new DateTimeOffset(process.StartTime)),
                DateTimeOffset.Now,
                DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static T? TryGet<T>(Func<T?> get)
    {
        try
        {
            return get();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return default;
        }
    }
}

internal static class AppCatalog
{
    private static readonly string[] ExecutableExtensions = [".exe", ".cmd", ".bat"];

    public static string? Resolve(string query)
    {
        query = query.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        if (File.Exists(query))
        {
            return Path.GetFullPath(query);
        }

        if (Path.GetExtension(query).Length == 0)
        {
            foreach (var extension in ExecutableExtensions)
            {
                var withExtension = query + extension;
                if (File.Exists(withExtension))
                {
                    return Path.GetFullPath(withExtension);
                }
            }
        }

        var exact = Find(query)
            .FirstOrDefault(app =>
                string.Equals(app.Name, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(app.Path), query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(app.Path), query, StringComparison.OrdinalIgnoreCase));

        return exact?.Path ?? Find(query).FirstOrDefault()?.Path;
    }

    public static IEnumerable<AppEntry> Find(string? query = null)
    {
        var entries = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in ReadKnownApps().Concat(ReadAppPaths()).Concat(ReadPathExecutables()).Concat(ReadStartMenuShortcuts()))
        {
            if (string.IsNullOrWhiteSpace(app.Path) || !File.Exists(app.Path))
            {
                continue;
            }

            entries.TryAdd(app.Path, app);
        }

        IEnumerable<AppEntry> filtered = entries.Values;
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(app =>
                app.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(app.Path).Contains(query, StringComparison.OrdinalIgnoreCase)
                || app.Path.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return filtered
            .OrderBy(app => Score(app, query))
            .ThenBy(app => app.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static int Score(AppEntry app, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return app.Source switch
            {
                "known" => 0,
                "app-paths" => 1,
                "start-menu" => 2,
                "path" => 3,
                _ => 4
            };
        }

        if (string.Equals(app.Name, query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(Path.GetFileNameWithoutExtension(app.Path), query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (app.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (Path.GetFileNameWithoutExtension(app.Path).StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 3;
        return 4;
    }

    private static IEnumerable<AppEntry> ReadKnownApps()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var candidates = new[]
        {
            new AppEntry("code", Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe"), "known"),
            new AppEntry("cursor", Path.Combine(localAppData, "Programs", "Cursor", "Cursor.exe"), "known"),
            new AppEntry("chrome", Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"), "known"),
            new AppEntry("notepad", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe"), "known")
        };

        foreach (var candidate in candidates)
        {
            yield return candidate;
        }
    }

    private static IEnumerable<AppEntry> ReadAppPaths()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var appPaths = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
                if (appPaths is null)
                {
                    continue;
                }

                foreach (var subKeyName in appPaths.GetSubKeyNames())
                {
                    using var subKey = appPaths.OpenSubKey(subKeyName);
                    var path = Convert.ToString(subKey?.GetValue(null), CultureInfo.InvariantCulture);
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    yield return new AppEntry(Path.GetFileNameWithoutExtension(subKeyName), path.Trim('"'), "app-paths");
                }
            }
        }
    }

    private static IEnumerable<AppEntry> ReadPathExecutables()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var folder in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.exe");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return new AppEntry(Path.GetFileNameWithoutExtension(file), file, "path");
            }
        }
    }

    private static IEnumerable<AppEntry> ReadStartMenuShortcuts()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> shortcuts;
            try
            {
                shortcuts = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                continue;
            }

            foreach (var shortcut in shortcuts)
            {
                var target = ShortcutResolver.GetTarget(shortcut);
                if (string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                yield return new AppEntry(Path.GetFileNameWithoutExtension(shortcut), target, "start-menu");
            }
        }
    }
}

internal sealed record AppEntry(string Name, string Path, string Source);

internal static class ShortcutResolver
{
    public static string? GetTarget(string shortcutPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            var shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            var shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            var target = shortcut?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null);
            return Convert.ToString(target, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            return null;
        }
    }
}
