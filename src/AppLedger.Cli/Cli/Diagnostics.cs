namespace AppLedger;

internal static class Diagnostics
{
    public static AppLedgerVersionInfo GetVersionInfo()
    {
        var assembly = typeof(Diagnostics).Assembly;
        var informationalVersion = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()
            ?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";

        var version = informationalVersion;
        var commit = "";
        var plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex >= 0)
        {
            version = informationalVersion[..plusIndex];
            commit = informationalVersion[(plusIndex + 1)..];
        }

        var executablePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        var baseDirectory = AppContext.BaseDirectory.TrimEnd('\\');
        var buildTime = GetBuildTime(executablePath, baseDirectory);

        return new AppLedgerVersionInfo(
            Product: "AppLedger",
            Version: version,
            InformationalVersion: informationalVersion,
            Commit: commit,
            BuildTime: buildTime,
            Runtime: RuntimeInformation.FrameworkDescription,
            OS: RuntimeInformation.OSDescription,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            ExecutablePath: executablePath,
            BaseDirectory: baseDirectory);
    }

    public static DoctorReport BuildDoctorReport(string workingDirectory)
    {
        var version = GetVersionInfo();
        var pathCommand = FindOnPath("appledger.exe") ?? FindOnPath("appledger");
        var elevated = IsElevated();
        var nativeFiles = GetNativeEtwFiles(version.BaseDirectory);
        var missingNativeFiles = nativeFiles.Where(file => !File.Exists(file)).ToArray();
        var releaseExe = FindRepoReleaseExecutable(workingDirectory);
        var releaseVersion = GetFileVersion(releaseExe);
        var runningVersion = GetFileVersion(version.ExecutablePath);
        var pathVersion = GetFileVersion(pathCommand);

        var checks = new List<DoctorCheck>
        {
            new(
                "Current binary",
                File.Exists(version.ExecutablePath) ? DoctorStatus.Ok : DoctorStatus.Warn,
                string.IsNullOrWhiteSpace(version.ExecutablePath) ? "Could not resolve the running executable path." : version.ExecutablePath),
            new(
                "PATH command",
                string.IsNullOrWhiteSpace(pathCommand)
                    ? DoctorStatus.Warn
                    : SamePath(pathCommand, version.ExecutablePath) ? DoctorStatus.Ok : DoctorStatus.Warn,
                string.IsNullOrWhiteSpace(pathCommand)
                    ? "appledger was not found on PATH."
                    : SamePath(pathCommand, version.ExecutablePath)
                        ? $"{pathCommand} ({pathVersion ?? "version unknown"})"
                        : $"PATH resolves to {pathCommand} ({pathVersion ?? "version unknown"}), but this process is {version.ExecutablePath} ({runningVersion ?? version.Version})."),
            new(
                "Elevation",
                elevated ? DoctorStatus.Ok : DoctorStatus.Warn,
                elevated
                    ? "Running elevated. ETW file/process fidelity should be highest."
                    : "Not elevated. Live ETW capture may be unavailable or lower fidelity."),
            new(
                "Native ETW files",
                missingNativeFiles.Length == 0 ? DoctorStatus.Ok : DoctorStatus.Fail,
                missingNativeFiles.Length == 0
                    ? $"Found {nativeFiles.Length:N0} native support file(s) under {version.BaseDirectory}."
                    : $"Missing: {string.Join(", ", missingNativeFiles.Select(Path.GetFileName))}. Keep the release folder together; do not copy only appledger.exe."),
            new(
                "Repo release artifact",
                string.IsNullOrWhiteSpace(releaseExe)
                    ? DoctorStatus.Info
                    : IsNewerVersion(releaseVersion, pathVersion ?? runningVersion) ? DoctorStatus.Warn : DoctorStatus.Ok,
                string.IsNullOrWhiteSpace(releaseExe)
                    ? "No local artifacts\\release\\appledger-win-x64\\appledger.exe found from this working directory."
                    : IsNewerVersion(releaseVersion, pathVersion ?? runningVersion)
                        ? $"Repo release artifact is newer ({releaseVersion}) than installed/running binary ({pathVersion ?? runningVersion ?? "unknown"}). Run: appledger install --from \"{Path.GetDirectoryName(releaseExe)}\""
                        : $"Found repo release artifact {releaseExe} ({releaseVersion ?? "version unknown"}).")
        };

        return new DoctorReport(version, pathCommand, releaseExe, checks);
    }

    public static InstallResult Install(string? sourceDirectory, string? targetDirectory, bool addPath)
    {
        var version = GetVersionInfo();
        var source = string.IsNullOrWhiteSpace(sourceDirectory)
            ? version.BaseDirectory
            : Path.GetFullPath(sourceDirectory);
        var target = string.IsNullOrWhiteSpace(targetDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppLedger")
            : Path.GetFullPath(targetDirectory);

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Install source does not exist: {source}");
        }

        var sourceExe = Path.Combine(source, "appledger.exe");
        if (!File.Exists(sourceExe))
        {
            throw new FileNotFoundException("Install source must contain appledger.exe.", sourceExe);
        }

        if (SamePath(source, target))
        {
            var samePathUpdated = addPath && EnsureUserPathContains(target);
            return new InstallResult(source, target, CopiedFiles: 0, PathUpdated: samePathUpdated, AlreadyInstalled: true);
        }

        Directory.CreateDirectory(target);
        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            copied++;
        }

        var pathUpdated = false;
        if (addPath)
        {
            pathUpdated = EnsureUserPathContains(target);
        }

        return new InstallResult(source, target, copied, pathUpdated, AlreadyInstalled: false);
    }

    public static void PrintVersion(AppLedgerVersionInfo info)
    {
        Console.WriteLine($"{info.Product} {info.Version}");
        Console.WriteLine($"  Informational version: {info.InformationalVersion}");
        Console.WriteLine($"  Commit:                {(string.IsNullOrWhiteSpace(info.Commit) ? "unknown" : info.Commit)}");
        Console.WriteLine($"  Build time:            {(info.BuildTime is null ? "unknown" : info.BuildTime.Value.ToString("O", CultureInfo.InvariantCulture))}");
        Console.WriteLine($"  Runtime:               {info.Runtime}");
        Console.WriteLine($"  OS:                    {info.OS}");
        Console.WriteLine($"  Architecture:          {info.Architecture}");
        Console.WriteLine($"  Executable:            {info.ExecutablePath}");
        Console.WriteLine($"  Base directory:        {info.BaseDirectory}");
    }

    public static void PrintDoctor(DoctorReport report)
    {
        PrintVersion(report.Version);
        Console.WriteLine();
        Console.WriteLine("Doctor:");
        foreach (var check in report.Checks)
        {
            Console.WriteLine($"  [{StatusLabel(check.Status)}] {check.Name}: {check.Detail}");
        }
    }

    private static string StatusLabel(DoctorStatus status) => status switch
    {
        DoctorStatus.Ok => "OK",
        DoctorStatus.Info => "INFO",
        DoctorStatus.Warn => "WARN",
        DoctorStatus.Fail => "FAIL",
        _ => status.ToString().ToUpperInvariant()
    };

    private static string[] GetNativeEtwFiles(string baseDirectory) =>
    [
        Path.Combine(baseDirectory, "amd64", "KernelTraceControl.dll"),
        Path.Combine(baseDirectory, "amd64", "msdia140.dll")
    ];

    private static string? FindRepoReleaseExecutable(string workingDirectory)
    {
        try
        {
            var candidate = Path.Combine(Path.GetFullPath(workingDirectory), "artifacts", "release", "appledger-win-x64", "appledger.exe");
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static DateTimeOffset? GetBuildTime(string executablePath, string baseDirectory)
    {
        var candidate = !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath)
            ? executablePath
            : Path.Combine(baseDirectory, "appledger.dll");

        return File.Exists(candidate) ? File.GetLastWriteTime(candidate) : null;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
        }

        return null;
    }

    private static string? GetFileVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(path).ProductVersion
                ?? FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool IsNewerVersion(string? candidate, string? baseline)
    {
        if (!TryParseVersion(candidate, out var candidateVersion) || !TryParseVersion(baseline, out var baselineVersion))
        {
            return false;
        }

        return candidateVersion.CompareTo(baselineVersion) > 0;
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Split('+')[0];
        return Version.TryParse(normalized, out version!);
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return Path.GetFullPath(left).TrimEnd('\\').Equals(Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool EnsureUserPathContains(string target)
    {
        var existing = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
        var entries = existing.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Any(entry => SamePath(entry.Trim('"'), target)))
        {
            return false;
        }

        var updated = string.IsNullOrWhiteSpace(existing)
            ? target
            : $"{existing.TrimEnd(Path.PathSeparator)}{Path.PathSeparator}{target}";
        Environment.SetEnvironmentVariable("Path", updated, EnvironmentVariableTarget.User);
        return true;
    }
}

internal sealed record AppLedgerVersionInfo(
    string Product,
    string Version,
    string InformationalVersion,
    string Commit,
    DateTimeOffset? BuildTime,
    string Runtime,
    string OS,
    string Architecture,
    string ExecutablePath,
    string BaseDirectory);

internal sealed record DoctorReport(
    AppLedgerVersionInfo Version,
    string? PathCommand,
    string? RepoReleaseExecutable,
    IReadOnlyList<DoctorCheck> Checks);

internal sealed record DoctorCheck(string Name, DoctorStatus Status, string Detail);

internal sealed record InstallResult(string SourceDirectory, string TargetDirectory, int CopiedFiles, bool PathUpdated, bool AlreadyInstalled);

internal enum DoctorStatus
{
    Ok,
    Info,
    Warn,
    Fail
}
