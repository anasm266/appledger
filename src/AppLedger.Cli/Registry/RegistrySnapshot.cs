namespace AppLedger;

internal static class RegistrySnapshot
{
    private static readonly RegistryLocation[] Locations =
    [
        new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        new(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        new(RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        new(RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce")
    ];

    public static Dictionary<string, string?> Capture()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in Locations)
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
                    values[$"{location.Hive}\\{location.Path}\\{name}"] = Convert.ToString(key.GetValue(name), CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                values[$"{location.Hive}\\{location.Path}\\<error>"] = ex.Message;
            }
        }

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

    private sealed record RegistryLocation(RegistryHive Hive, string Path);
}

internal sealed record RegistryEvent(RegistryEventKind Kind, string Key, string? Before, string? After);

internal enum RegistryEventKind
{
    Created,
    Modified,
    Deleted
}
