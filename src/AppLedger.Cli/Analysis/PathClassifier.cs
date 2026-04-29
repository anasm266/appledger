namespace AppLedger;

internal static class PathClassifier
{
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string CommonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private static readonly string Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private static readonly string Downloads = Path.Combine(UserProfile, "Downloads");

    public static string Classify(string path)
    {
        if (IsSystemRuntimeNoise(path)) return "system-runtime";
        if (IsSensitive(path)) return "sensitive";
        if (IsUnder(path, Path.GetTempPath())) return "temp";
        if (IsUnder(path, AppData) || IsUnder(path, LocalAppData)) return "app-data";
        if (IsUnder(path, Downloads)) return "downloads";
        if (IsUnder(path, Documents)) return "documents";
        if (IsUnder(path, Desktop)) return "desktop";
        if (IsUnder(path, Windows)) return "system";
        if (IsUnder(path, ProgramFiles) || IsUnder(path, ProgramFilesX86)) return "program-files";
        if (path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) return "dependencies";
        if (path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) return "git";
        return "project-or-user";
    }

    public static bool IsSensitive(string path)
    {
        if (IsSystemRuntimeNoise(path))
        {
            return false;
        }

        var normalized = path.Replace('/', '\\');
        var fileName = Path.GetFileName(normalized);
        return normalized.Contains("\\.ssh\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\.aws\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\.azure\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\.gnupg\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\.kube\\", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".npmrc", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".pypirc", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".gitconfig", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("id_rsa", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("credentials", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("token", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSystemRuntimeNoise(string path)
    {
        var normalized = path.Replace('/', '\\');
        return normalized.Contains("\\ProgramData\\Microsoft\\NetFramework\\BreadcrumbStore\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\Microsoft\\CLR_v4.0\\UsageLogs\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\assembly\\NativeImages_", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\AppData\\Local\\Microsoft\\CLR_v4.0\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\AppData\\Local\\Temp\\etilqs_", StringComparison.OrdinalIgnoreCase)
            || (IsUnder(normalized, CommonApplicationData) && normalized.Contains("\\Microsoft\\NetFramework\\", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsUsuallyNoise(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUnderAnyWatchRoot(string path, IReadOnlyList<string> roots)
    {
        if (path.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var root in roots)
        {
            if (IsUnder(path, root))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd('\\') + "\\";
            var fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
