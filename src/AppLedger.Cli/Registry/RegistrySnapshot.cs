namespace AppLedger;

internal static class RegistrySnapshot
{
    private const int MaxServiceKeys = 2_000;
    private const int MaxTaskKeys = 2_000;
    private const int MaxProtocolKeys = 2_000;
    private const int MaxFileAssociationKeys = 1_000;

    private static readonly RegistryLocation[] ValueLocations =
    [
        new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
        new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"),
        new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder"),
        new(RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        new(RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        new(RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
        new(RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"),
        new(RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder")
    ];

    public static Dictionary<string, string?> Capture()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        CaptureConfiguredValueLocations(values);
        CaptureServices(values);
        CaptureScheduledTasks(values);
        CaptureProtocolHandlers(values);
        CaptureFileAssociations(values);

        return values;
    }

    public static List<RegistryEvent> Compare(Dictionary<string, string?> before, Dictionary<string, string?> after)
    {
        var events = new List<RegistryEvent>();
        foreach (var (key, value) in after)
        {
            if (!before.TryGetValue(key, out var previous))
            {
                events.Add(new RegistryEvent(RegistryEventKind.Created, key, null, value));
            }
            else if (!string.Equals(previous, value, StringComparison.Ordinal))
            {
                events.Add(new RegistryEvent(RegistryEventKind.Modified, key, previous, value));
            }
        }

        foreach (var (key, value) in before)
        {
            if (!after.ContainsKey(key))
            {
                events.Add(new RegistryEvent(RegistryEventKind.Deleted, key, value, null));
            }
        }

        return events;
    }

    private static void CaptureConfiguredValueLocations(Dictionary<string, string?> values)
    {
        foreach (var location in ValueLocations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(location.Hive, RegistryView.Default);
                using var key = baseKey.OpenSubKey(location.Path);
                if (key is null)
                {
                    continue;
                }

                foreach (var name in key.GetValueNames())
                {
                    AddValue(values, location.Hive, location.Path, name, key.GetValue(name));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                values[$"{location.Hive}\\{location.Path}\\<error>"] = ex.Message;
            }
        }
    }

    private static void CaptureServices(Dictionary<string, string?> values)
    {
        const string servicesPath = @"SYSTEM\CurrentControlSet\Services";
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var services = baseKey.OpenSubKey(servicesPath);
            if (services is null)
            {
                return;
            }

            foreach (var serviceName in services.GetSubKeyNames().Take(MaxServiceKeys))
            {
                using var service = services.OpenSubKey(serviceName);
                if (service is null)
                {
                    continue;
                }

                foreach (var valueName in new[] { "ImagePath", "DisplayName", "Start", "Type", "ObjectName" })
                {
                    AddValue(values, RegistryHive.LocalMachine, $@"{servicesPath}\{serviceName}", valueName, service.GetValue(valueName));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            values[$"{RegistryHive.LocalMachine}\\{servicesPath}\\<error>"] = ex.Message;
        }
    }

    private static void CaptureScheduledTasks(Dictionary<string, string?> values)
    {
        const string taskTreePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree";
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var taskTree = baseKey.OpenSubKey(taskTreePath);
            if (taskTree is null)
            {
                return;
            }

            var captured = 0;
            CaptureTaskTree(values, RegistryHive.LocalMachine, taskTreePath, taskTree, ref captured);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            values[$"{RegistryHive.LocalMachine}\\{taskTreePath}\\<error>"] = ex.Message;
        }

        CaptureScheduledTaskFiles(values);
    }

    private static void CaptureTaskTree(Dictionary<string, string?> values, RegistryHive hive, string path, RegistryKey key, ref int captured)
    {
        if (captured >= MaxTaskKeys)
        {
            return;
        }

        AddValue(values, hive, path, "Id", key.GetValue("Id"));
        AddValue(values, hive, path, "Index", key.GetValue("Index"));
        captured++;

        foreach (var childName in key.GetSubKeyNames())
        {
            if (captured >= MaxTaskKeys)
            {
                return;
            }

            using var child = key.OpenSubKey(childName);
            if (child is not null)
            {
                CaptureTaskTree(values, hive, $@"{path}\{childName}", child, ref captured);
            }
        }
    }

    private static void CaptureScheduledTaskFiles(Dictionary<string, string?> values)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks");
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            var captured = 0;
            var options = new System.IO.EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            };

            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                if (captured >= MaxTaskKeys)
                {
                    return;
                }

                CaptureScheduledTaskFile(values, root, file);
                captured++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            values[$"{RegistryHive.LocalMachine}\\ScheduledTasks\\<file-error>"] = ex.Message;
        }
    }

    private static void CaptureScheduledTaskFile(Dictionary<string, string?> values, string root, string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var document = System.Xml.Linq.XDocument.Load(stream);
            var relative = Path.GetRelativePath(root, file).Replace('/', '\\');
            var taskPath = $@"ScheduledTasks\{relative}";

            var execs = document
                .Descendants()
                .Where(element => element.Name.LocalName.Equals("Exec", StringComparison.OrdinalIgnoreCase))
                .Take(10)
                .ToList();

            for (var index = 0; index < execs.Count; index++)
            {
                var exec = execs[index];
                var suffix = execs.Count == 1 ? "" : $".{index + 1}";
                AddValue(values, RegistryHive.LocalMachine, taskPath, $"Command{suffix}", ChildValue(exec, "Command"));
                AddValue(values, RegistryHive.LocalMachine, taskPath, $"Arguments{suffix}", ChildValue(exec, "Arguments"));
                AddValue(values, RegistryHive.LocalMachine, taskPath, $"WorkingDirectory{suffix}", ChildValue(exec, "WorkingDirectory"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or System.Xml.XmlException)
        {
            var relative = SafeRelativePath(root, file);
            values[$"{RegistryHive.LocalMachine}\\ScheduledTasks\\{relative}\\<read-error>"] = ex.Message;
        }
    }

    private static string? ChildValue(System.Xml.Linq.XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string SafeRelativePath(string root, string file)
    {
        try
        {
            return Path.GetRelativePath(root, file).Replace('/', '\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Path.GetFileName(file);
        }
    }

    private static void CaptureProtocolHandlers(Dictionary<string, string?> values)
    {
        CaptureProtocolHandlers(values, RegistryHive.CurrentUser, @"Software\Classes");
        CaptureProtocolHandlers(values, RegistryHive.LocalMachine, @"Software\Classes");
    }

    private static void CaptureProtocolHandlers(Dictionary<string, string?> values, RegistryHive hive, string classesPath)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var classes = baseKey.OpenSubKey(classesPath);
            if (classes is null)
            {
                return;
            }

            foreach (var name in classes.GetSubKeyNames().Take(MaxProtocolKeys))
            {
                if (name.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                using var handler = classes.OpenSubKey(name);
                if (handler?.GetValue("URL Protocol") is null)
                {
                    continue;
                }

                var handlerPath = $@"{classesPath}\{name}";
                AddValue(values, hive, handlerPath, "(default)", handler.GetValue(""));
                AddValue(values, hive, handlerPath, "URL Protocol", handler.GetValue("URL Protocol"));
                using var command = handler.OpenSubKey(@"shell\open\command");
                AddValue(values, hive, $@"{handlerPath}\shell\open\command", "(default)", command?.GetValue(""));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            values[$"{hive}\\{classesPath}\\<protocol-error>"] = ex.Message;
        }
    }

    private static void CaptureFileAssociations(Dictionary<string, string?> values)
    {
        CaptureClassesFileAssociations(values, RegistryHive.CurrentUser, @"Software\Classes");
        CaptureClassesFileAssociations(values, RegistryHive.LocalMachine, @"Software\Classes");
        CaptureExplorerFileAssociations(values);
    }

    private static void CaptureClassesFileAssociations(Dictionary<string, string?> values, RegistryHive hive, string classesPath)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var classes = baseKey.OpenSubKey(classesPath);
            if (classes is null)
            {
                return;
            }

            foreach (var name in classes.GetSubKeyNames().Where(name => name.StartsWith(".", StringComparison.Ordinal)).Take(MaxFileAssociationKeys))
            {
                using var extension = classes.OpenSubKey(name);
                AddValue(values, hive, $@"{classesPath}\{name}", "(default)", extension?.GetValue(""));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            values[$"{hive}\\{classesPath}\\<association-error>"] = ex.Message;
        }
    }

    private static void CaptureExplorerFileAssociations(Dictionary<string, string?> values)
    {
        const string fileExtsPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var fileExts = baseKey.OpenSubKey(fileExtsPath);
            if (fileExts is null)
            {
                return;
            }

            foreach (var extensionName in fileExts.GetSubKeyNames().Take(MaxFileAssociationKeys))
            {
                using var extension = fileExts.OpenSubKey(extensionName);
                using var userChoice = extension?.OpenSubKey("UserChoice");
                AddValue(values, RegistryHive.CurrentUser, $@"{fileExtsPath}\{extensionName}\UserChoice", "ProgId", userChoice?.GetValue("ProgId"));
                AddValue(values, RegistryHive.CurrentUser, $@"{fileExtsPath}\{extensionName}\UserChoice", "Hash", userChoice?.GetValue("Hash"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            values[$"{RegistryHive.CurrentUser}\\{fileExtsPath}\\<error>"] = ex.Message;
        }
    }

    private static void AddValue(Dictionary<string, string?> values, RegistryHive hive, string path, string name, object? value)
    {
        if (value is null)
        {
            return;
        }

        values[$"{hive}\\{path}\\{name}"] = FormatRegistryValue(value);
    }

    private static string? FormatRegistryValue(object? value) => value switch
    {
        null => null,
        byte[] bytes => Convert.ToHexString(bytes),
        string[] strings => string.Join(";", strings),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private sealed record RegistryLocation(RegistryHive Hive, string Path);
}

internal sealed record RegistryEvent(RegistryEventKind Kind, string Key, string? Before, string? After);

internal enum RegistryEventKind
{
    Created,
    Modified,
    Deleted
}
