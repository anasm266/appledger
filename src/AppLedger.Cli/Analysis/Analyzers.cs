namespace AppLedger;

internal static class Analyzer
{
    public static List<Finding> Find(
        IReadOnlyList<string> watchRoots,
        bool watchAll,
        IReadOnlyList<FileEvent> files,
        IReadOnlyList<ProcessRecord> processes,
        IReadOnlyList<NetworkEvent> network,
        IReadOnlyList<RegistryEvent> registry,
        IReadOnlyList<FolderImpact> topFolders)
    {
        var findings = new List<Finding>();

        AddSensitivePathFindings(findings, files);
        AddProcessFindings(findings, processes);
        findings.AddRange(PersistenceAnalyzer.BuildFindings(PersistenceAnalyzer.Build(files, registry)));
        AddFileScopeFindings(findings, watchRoots, watchAll, files);
        AddStorageFindings(findings, topFolders);
        AddNetworkFindings(findings, network);

        return findings;
    }

    private static void AddSensitivePathFindings(List<Finding> findings, IReadOnlyList<FileEvent> files)
    {
        foreach (var group in files
            .Where(f => f.IsSensitive && !PathClassifier.IsSystemRuntimeNoise(f.Path))
            .GroupBy(f => NormalizePath(f.Path), StringComparer.OrdinalIgnoreCase)
            .Take(20))
        {
            var first = group.OrderBy(file => file.ObservedAt).First();
            var actions = string.Join(", ", group
                .Select(file => file.Kind.ToString().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(action => action, StringComparer.OrdinalIgnoreCase));
            var severity = IsHighValueSecretPath(first.Path) || group.Any(file => file.Kind is FileEventKind.Modified or FileEventKind.Deleted or FileEventKind.Renamed)
                ? "high"
                : "medium";
            AddFinding(findings, severity, SensitiveTitle(first.Path), $"{actions} {first.Path}");
        }
    }

    private static void AddProcessFindings(List<Finding> findings, IReadOnlyList<ProcessRecord> processes)
    {
        foreach (var process in processes.Where(IsPowerShellBypass).Take(10))
        {
            AddFinding(findings, "high", "PowerShell policy bypass", process.CommandLine ?? process.Name);
        }

        foreach (var process in processes.Where(IsEncodedPowerShell).Take(10))
        {
            AddFinding(findings, "medium", "Encoded PowerShell command", SanitizeCommandLine(process.CommandLine) ?? process.Name);
        }

        foreach (var process in processes.Where(IsShellProcess).Take(10))
        {
            AddFinding(findings, "medium", "Shell process spawned", SanitizeCommandLine(process.CommandLine) ?? process.Name);
        }

        foreach (var process in processes.Where(p => IsPackageInstall(p.CommandLine)).Take(10))
        {
            AddFinding(findings, "medium", "Package install command", SanitizeCommandLine(process.CommandLine) ?? process.Name);
        }

        foreach (var process in processes.Where(IsNetworkToolCommand).Take(10))
        {
            AddFinding(findings, "medium", "Network transfer tool used", SanitizeCommandLine(process.CommandLine) ?? process.Name);
        }
    }

    private static void AddFileScopeFindings(List<Finding> findings, IReadOnlyList<string> watchRoots, bool watchAll, IReadOnlyList<FileEvent> files)
    {
        if (!watchAll || watchRoots.Count == 0)
        {
            return;
        }

        var outsideWrites = files
            .Where(file => file.Kind is FileEventKind.Created or FileEventKind.Modified or FileEventKind.Deleted or FileEventKind.Renamed)
            .Where(file => IsUserFacingPath(file.Path) || (!string.IsNullOrWhiteSpace(file.RelatedPath) && IsUserFacingPath(file.RelatedPath)))
            .Where(file => !PathClassifier.IsUnderAnyWatchRoot(file.Path, watchRoots)
                && (string.IsNullOrWhiteSpace(file.RelatedPath) || !PathClassifier.IsUnderAnyWatchRoot(file.RelatedPath, watchRoots)))
            .Where(file => !IsLowSignalWrite(file.Path))
            .GroupBy(file => NormalizePath(file.RelatedPath ?? file.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(file => file.ObservedAt).First())
            .Take(10)
            .ToList();

        if (outsideWrites.Count == 0)
        {
            return;
        }

        var examples = string.Join("; ", outsideWrites.Take(4).Select(file => file.RelatedPath ?? file.Path));
        AddFinding(
            findings,
            "medium",
            "User-facing write outside watched root",
            $"{outsideWrites.Count:N0} write(s) outside watched roots. Examples: {examples}");
    }

    private static void AddStorageFindings(List<Finding> findings, IReadOnlyList<FolderImpact> topFolders)
    {
        foreach (var folder in topFolders.Where(f => f.Category is "temp" or "app-data" && f.BytesAdded >= 100 * 1024 * 1024).Take(10))
        {
            AddFinding(findings, "info", "Large cache or temp growth", $"{Format.Bytes(folder.BytesAdded)} added under {folder.Path}");
        }
    }

    private static void AddNetworkFindings(List<Finding> findings, IReadOnlyList<NetworkEvent> network)
    {
        var external = network
            .Where(item => IsExternalAddress(item.RemoteAddress))
            .GroupBy(item => NetworkResolver.DisplayHost(item), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.FirstSeen).First())
            .OrderBy(item => NetworkResolver.DisplayHost(item), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (external.Count > 0)
        {
            var examples = string.Join(", ", external.Take(5).Select(NetworkResolver.DisplayHostLabel));
            AddFinding(findings, "info", "External network destinations", $"{external.Count:N0} external destination group(s): {examples}");
        }

        if (network.Count > 25)
        {
            AddFinding(findings, "info", "Many network endpoints", $"{network.Count:N0} distinct IPv4 TCP endpoints were observed.");
        }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool IsHighValueSecretPath(string path)
    {
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
            || fileName.Contains("id_rsa", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("credentials", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("token", StringComparison.OrdinalIgnoreCase);
    }

    private static string SensitiveTitle(string path)
    {
        var normalized = path.Replace('/', '\\');
        var fileName = Path.GetFileName(normalized);
        if (normalized.Contains("\\.ssh\\", StringComparison.OrdinalIgnoreCase) || fileName.Contains("id_rsa", StringComparison.OrdinalIgnoreCase))
        {
            return "SSH material touched";
        }

        if (fileName.Equals(".env", StringComparison.OrdinalIgnoreCase))
        {
            return ".env touched";
        }

        if (fileName.Equals(".npmrc", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".pypirc", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("token", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("credentials", StringComparison.OrdinalIgnoreCase))
        {
            return "Credential file touched";
        }

        return "Sensitive path touched";
    }

    private static bool IsPowerShellBypass(ProcessRecord process) =>
        process.CommandLine?.Contains("ExecutionPolicy Bypass", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsEncodedPowerShell(ProcessRecord process) =>
        IsPowerShellName(process.Name)
        && process.CommandLine?.Contains("-EncodedCommand", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsShellProcess(ProcessRecord process)
    {
        if (!IsShellName(process.Name))
        {
            return false;
        }

        var command = process.CommandLine ?? "";
        return !command.Contains("Win32_Process | Select-Object", StringComparison.OrdinalIgnoreCase)
            && !command.Contains("Long-lived PowerShell AST parser", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNetworkToolCommand(ProcessRecord process)
    {
        var command = process.CommandLine ?? "";
        var name = Path.GetFileNameWithoutExtension(process.Name);
        return name.Equals("curl", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wget", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ssh", StringComparison.OrdinalIgnoreCase)
            || name.Equals("scp", StringComparison.OrdinalIgnoreCase)
            || name.Equals("sftp", StringComparison.OrdinalIgnoreCase)
            || name.Equals("rsync", StringComparison.OrdinalIgnoreCase)
            || name.Equals("nc", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ncat", StringComparison.OrdinalIgnoreCase)
            || command.Contains("Invoke-WebRequest", StringComparison.OrdinalIgnoreCase)
            || command.Contains("Invoke-RestMethod", StringComparison.OrdinalIgnoreCase)
            || command.Contains(" iwr ", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("iwr ", StringComparison.OrdinalIgnoreCase)
            || command.Contains(" curl ", StringComparison.OrdinalIgnoreCase)
            || command.Contains(" wget ", StringComparison.OrdinalIgnoreCase)
            || command.Contains(" ssh ", StringComparison.OrdinalIgnoreCase)
            || command.Contains(" scp ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUserFacingPath(string path)
    {
        var category = PathClassifier.Classify(path);
        return category is "documents" or "desktop" or "downloads" or "project-or-user" or "sensitive";
    }

    private static bool IsLowSignalWrite(string path)
    {
        var normalized = path.Replace('/', '\\');
        return PathClassifier.IsSystemRuntimeNoise(path)
            || normalized.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\node_modules\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\AppData\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\Temp\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExternalAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var ip))
        {
            return false;
        }

        if (IPAddress.IsLoopback(ip))
        {
            return false;
        }

        var bytes = ip.GetAddressBytes();
        return bytes.Length != 4
            || !(bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254));
    }

    private static bool IsShellName(string name) =>
        IsPowerShellName(name) || name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsPowerShellName(string name) =>
        name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
        || name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);

    private static string? SanitizeCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return commandLine;
        }

        var encodedIndex = commandLine.IndexOf("-EncodedCommand", StringComparison.OrdinalIgnoreCase);
        if (encodedIndex >= 0)
        {
            return commandLine[..encodedIndex].Trim() + " -EncodedCommand <base64>";
        }

        return commandLine.Length <= 320 ? commandLine : commandLine[..317] + "...";
    }

    private static void AddFinding(List<Finding> findings, string severity, string title, string detail)
    {
        if (findings.Any(finding =>
            finding.Severity.Equals(severity, StringComparison.OrdinalIgnoreCase)
            && finding.Title.Equals(title, StringComparison.OrdinalIgnoreCase)
            && finding.Detail.Equals(detail, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        findings.Add(new Finding(severity, title, detail));
    }

    private static bool IsPackageInstall(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        return commandLine.Contains("npm install", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("pnpm add", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("yarn add", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("pip install", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("uv add", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class AiCodingAnalyzer
{
    private static readonly string[] ProjectNoiseSegments =
    [
        "\\node_modules\\",
        "\\.git\\",
        "\\bin\\",
        "\\obj\\",
        "\\dist\\",
        "\\build\\",
        "\\.next\\",
        "\\.turbo\\",
        "\\coverage\\"
    ];

    public static AiCodingActivity Build(
        IReadOnlyList<string> watchRoots,
        IReadOnlyList<FileEvent> files,
        IReadOnlyList<ProcessRecord> processes)
    {
        var changedProjectFiles = files
            .Where(file => file.Kind is FileEventKind.Created or FileEventKind.Modified or FileEventKind.Deleted or FileEventKind.Renamed)
            .Where(file => IsProjectFile(file.Path, watchRoots) || (!string.IsNullOrWhiteSpace(file.RelatedPath) && IsProjectFile(file.RelatedPath, watchRoots)))
            .GroupBy(file => $"{file.Kind}|{Normalize(file.Path)}|{NormalizeMaybe(file.RelatedPath)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(file => SourceRank(file.Source)).ThenByDescending(file => file.ObservedAt).First())
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(file => new ProjectFileChange(
                file.Kind,
                file.Path,
                RelativeToWatchRoot(file.Path, watchRoots),
                file.RelatedPath,
                file.RelatedPath is null ? null : RelativeToWatchRoot(file.RelatedPath, watchRoots),
                file.Category,
                file.Source,
                file.ObservedAt))
            .ToList();

        var projectSummary = new ProjectChangeSummary(
            changedProjectFiles.Count(file => file.Kind == FileEventKind.Created),
            changedProjectFiles.Count(file => file.Kind == FileEventKind.Modified),
            changedProjectFiles.Count(file => file.Kind == FileEventKind.Deleted),
            changedProjectFiles.Count(file => file.Kind == FileEventKind.Renamed),
            changedProjectFiles.Select(file => Normalize(file.RelatedPath ?? file.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var rawDeveloperCommands = processes
            .Where(process => !string.IsNullOrWhiteSpace(process.CommandLine))
            .SelectMany(ClassifyCommands)
            .OrderBy(command => command.FirstSeen)
            .ThenBy(command => command.Kind, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var developerCommands = rawDeveloperCommands
            .Where(command => !IsLowSignalCommand(command))
            .GroupBy(command => $"{command.Kind}|{CommandFingerprint(command.CommandLine)}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.OrderBy(command => command.FirstSeen).First();
                return first with { Occurrences = group.Count() };
            })
            .OrderBy(command => command.FirstSeen)
            .ThenBy(command => command.Kind, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var commandSummary = new CommandSummary(
            developerCommands.Count,
            developerCommands.Count(command => command.Kind == "package-install"),
            developerCommands.Count(command => command.Kind == "git"),
            developerCommands.Count(command => command.Kind == "test"),
            developerCommands.Count(command => command.Kind == "shell"),
            developerCommands.Count(command => command.Kind == "script"));

        var sensitiveAccesses = files
            .Where(file => file.IsSensitive)
            .GroupBy(file => $"{file.Kind}|{Normalize(file.Path)}|{file.ProcessId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(file => file.ObservedAt).First())
            .OrderBy(file => file.ObservedAt)
            .Select(file => new SensitiveAccess(
                file.Kind,
                file.Path,
                RelativeToWatchRoot(file.Path, watchRoots),
                file.Source,
                file.ProcessId,
                file.ProcessName,
                file.ObservedAt))
            .Take(50)
            .ToList();

        var processGroups = processes
            .GroupBy(process => NormalizeProcessName(process.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProcessGroupSummary(
                group.Key,
                group.Count(),
                group.Count(process => !string.IsNullOrWhiteSpace(process.CommandLine)),
                group.Min(process => process.FirstSeen),
                group.Max(process => process.LastSeen)))
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();

        var processTimeline = processes
            .Where(IsTimelineProcess)
            .OrderBy(process => process.FirstSeen)
            .ThenBy(process => process.ProcessId)
            .Select(process => new ProcessTimelineItem(
                process.ProcessId,
                process.ParentProcessId,
                process.Name,
                SanitizeCommandLine(process.CommandLine),
                process.FirstSeen,
                process.LastSeen,
                Math.Max(0, (process.LastSeen - process.FirstSeen).TotalSeconds)))
            .Take(100)
            .ToList();

        return new AiCodingActivity(
            projectSummary,
            changedProjectFiles.Take(200).ToList(),
            commandSummary,
            developerCommands.Take(100).ToList(),
            sensitiveAccesses,
            processGroups,
            processTimeline);
    }

    private static bool IsProjectFile(string path, IReadOnlyList<string> watchRoots)
    {
        if (!PathClassifier.IsUnderAnyWatchRoot(path, watchRoots))
        {
            return false;
        }

        var normalized = path.Replace('/', '\\');
        if (ProjectNoiseSegments.Any(segment => normalized.Contains(segment, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var fileName = Path.GetFileName(normalized);
        return !fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".cache", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<CommandActivity> ClassifyCommands(ProcessRecord process)
    {
        var command = process.CommandLine ?? "";
        var name = process.Name;
        var kinds = new List<string>();

        if (IsPackageInstall(command)) kinds.Add("package-install");
        if (IsGitCommand(command, name)) kinds.Add("git");
        if (IsTestCommand(command)) kinds.Add("test");
        if (IsScriptCommand(command)) kinds.Add("script");
        if (IsShell(name, command)) kinds.Add("shell");

        foreach (var kind in kinds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return new CommandActivity(
                kind,
                process.ProcessId,
                process.ParentProcessId,
                process.Name,
                SanitizeCommandLine(process.CommandLine) ?? process.Name,
                process.FirstSeen,
                1);
        }
    }

    private static bool IsPackageInstall(string command) =>
        command.Contains("npm install", StringComparison.OrdinalIgnoreCase)
        || command.Contains("npm i ", StringComparison.OrdinalIgnoreCase)
        || command.EndsWith("npm i", StringComparison.OrdinalIgnoreCase)
        || command.Contains("pnpm add", StringComparison.OrdinalIgnoreCase)
        || command.Contains("pnpm install", StringComparison.OrdinalIgnoreCase)
        || command.Contains("yarn add", StringComparison.OrdinalIgnoreCase)
        || command.Contains("yarn install", StringComparison.OrdinalIgnoreCase)
        || (command.Contains("npm-cli.js", StringComparison.OrdinalIgnoreCase) && (command.Contains(" install", StringComparison.OrdinalIgnoreCase) || command.Contains(" add", StringComparison.OrdinalIgnoreCase)))
        || command.Contains("pip install", StringComparison.OrdinalIgnoreCase)
        || command.Contains("uv add", StringComparison.OrdinalIgnoreCase)
        || command.Contains("cargo add", StringComparison.OrdinalIgnoreCase)
        || command.Contains("dotnet add package", StringComparison.OrdinalIgnoreCase);

    private static bool IsTestCommand(string command) =>
        command.Contains("npm test", StringComparison.OrdinalIgnoreCase)
        || command.Contains("npm run test", StringComparison.OrdinalIgnoreCase)
        || command.Contains("pnpm test", StringComparison.OrdinalIgnoreCase)
        || command.Contains("yarn test", StringComparison.OrdinalIgnoreCase)
        || command.Contains("dotnet test", StringComparison.OrdinalIgnoreCase)
        || (command.Contains("npm-cli.js", StringComparison.OrdinalIgnoreCase) && command.Contains(" test", StringComparison.OrdinalIgnoreCase))
        || command.Contains("pytest", StringComparison.OrdinalIgnoreCase)
        || command.Contains("cargo test", StringComparison.OrdinalIgnoreCase)
        || command.Contains("go test", StringComparison.OrdinalIgnoreCase)
        || command.Contains("mvn test", StringComparison.OrdinalIgnoreCase)
        || command.Contains("gradle test", StringComparison.OrdinalIgnoreCase);

    private static bool IsScriptCommand(string command) =>
        command.Contains(".py", StringComparison.OrdinalIgnoreCase)
        || command.Contains(".ps1", StringComparison.OrdinalIgnoreCase)
        || command.Contains(".sh", StringComparison.OrdinalIgnoreCase)
        || command.Contains("node ", StringComparison.OrdinalIgnoreCase)
        || command.Contains("python ", StringComparison.OrdinalIgnoreCase)
        || command.Contains("pwsh ", StringComparison.OrdinalIgnoreCase);

    private static bool IsShell(string name, string command) =>
        name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
        || name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)
        || name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
        || command.Contains("ExecutionPolicy", StringComparison.OrdinalIgnoreCase);

    private static bool IsGitCommand(string command, string processName)
    {
        if (command.Contains("credential-manager", StringComparison.OrdinalIgnoreCase)
            || command.Contains("remote-https", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("credential-manager", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("git-remote-https.exe", StringComparison.OrdinalIgnoreCase)
            || command.Contains("git-remote-https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return processName.Equals("git.exe", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("git ", StringComparison.OrdinalIgnoreCase)
            || command.StartsWith("\"git\" ", StringComparison.OrdinalIgnoreCase)
            || command.Contains("\\git.exe\"", StringComparison.OrdinalIgnoreCase)
            || command.Contains("\\git.exe ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLowSignalCommand(CommandActivity command)
    {
        var name = NormalizeProcessName(command.ProcessName);
        var line = command.CommandLine;

        if (name is "conhost" or "git-credential-manager" or "git-remote-https")
        {
            return true;
        }

        return line.Contains("git credential-manager", StringComparison.OrdinalIgnoreCase)
            || line.Contains("git-remote-https", StringComparison.OrdinalIgnoreCase)
            || line.Contains("git remote-https", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Win32_Process | Select-Object", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Long-lived PowerShell AST parser", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTimelineProcess(ProcessRecord process)
    {
        var name = NormalizeProcessName(process.Name);
        if (name is "conhost" or "git-credential-manager" or "git-remote-https")
        {
            return false;
        }

        var command = process.CommandLine ?? "";
        if (command.Contains("credential-manager", StringComparison.OrdinalIgnoreCase)
            || command.Contains("git-remote-https", StringComparison.OrdinalIgnoreCase)
            || command.Contains("git remote-https", StringComparison.OrdinalIgnoreCase)
            || command.Contains("Win32_Process | Select-Object", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return process.ParentProcessId == 0
            || !string.IsNullOrWhiteSpace(process.CommandLine)
            || name is "codex" or "code" or "cursor" or "node" or "node_repl" or "git" or "powershell" or "pwsh" or "cmd" or "dotnet" or "python" or "npm" or "pnpm";
    }

    private static string NormalizeProcessName(string name)
    {
        var file = Path.GetFileNameWithoutExtension(name);
        return string.IsNullOrWhiteSpace(file) ? name : file;
    }

    private static string CommandFingerprint(string command)
    {
        var normalized = command
            .Replace("\"", "", StringComparison.Ordinal)
            .Replace("C:\\Program Files\\Git\\cmd\\git.exe", "git", StringComparison.OrdinalIgnoreCase)
            .Replace("C:\\Program Files\\Git\\mingw64\\bin\\git.exe", "git", StringComparison.OrdinalIgnoreCase)
            .Replace("C:/Program Files/Git/mingw64/libexec/git-core\\git.exe", "git", StringComparison.OrdinalIgnoreCase)
            .Replace("C:\\windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", "powershell", StringComparison.OrdinalIgnoreCase)
            .Replace("C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", "powershell", StringComparison.OrdinalIgnoreCase);

        return string.Join(" ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string? SanitizeCommandLine(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return command;
        }

        var compact = command
            .Replace("\"C:\\Program Files\\Git\\cmd\\git.exe\"", "git", StringComparison.OrdinalIgnoreCase)
            .Replace("\"C:\\Program Files\\Git\\mingw64\\bin\\git.exe\"", "git", StringComparison.OrdinalIgnoreCase)
            .Replace("\"C:/Program Files/Git/mingw64/libexec/git-core\\git.exe\"", "git", StringComparison.OrdinalIgnoreCase)
            .Replace("\"C:\\windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\"", "powershell", StringComparison.OrdinalIgnoreCase)
            .Replace("\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\"", "powershell", StringComparison.OrdinalIgnoreCase)
            .Replace("git.exe ", "git ", StringComparison.OrdinalIgnoreCase);

        var encodedIndex = compact.IndexOf("-EncodedCommand", StringComparison.OrdinalIgnoreCase);
        if (encodedIndex >= 0)
        {
            return compact[..encodedIndex].Trim() + " -EncodedCommand <base64>";
        }

        return compact.Length <= 320 ? compact : compact[..317] + "...";
    }

    private static int SourceRank(string source) => source.Equals("etw", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\');

    private static string NormalizeMaybe(string? path) => string.IsNullOrWhiteSpace(path) ? "" : Normalize(path);

    private static string RelativeToWatchRoot(string path, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            try
            {
                var relative = Path.GetRelativePath(root, path);
                if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                {
                    return relative;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                continue;
            }
        }

        return path;
    }
}

internal static class SessionActivityAnalyzer
{
    public static SessionActivityOverview Build(
        IReadOnlyList<string> watchRoots,
        bool watchAll,
        IReadOnlyList<FileEvent> files,
        IReadOnlyList<NetworkEvent> networkEvents,
        AiCodingActivity ai,
        IReadOnlyList<Finding> findings)
    {
        var writes = files
            .Where(file => file.Kind is FileEventKind.Created or FileEventKind.Modified or FileEventKind.Deleted or FileEventKind.Renamed)
            .ToList();

        var buckets = new List<ActivityBucketSummary>();

        var appDataBucket = BuildFileBucket(
            "app-data-cache",
            "App Data / Cache",
            "Writes under roaming/local app data.",
            writes.Where(file => file.Category == "app-data"));
        AddBucket(buckets, appDataBucket);

        var tempBucket = BuildFileBucket(
            "temp-churn",
            "Temp Churn",
            "Writes under temp folders.",
            writes.Where(file => file.Category == "temp"));
        AddBucket(buckets, tempBucket);

        var workspaceBucket = BuildFileBucket(
            "workspace",
            "Watched Root / User Files",
            watchRoots.Count > 0
                ? "Writes under watched roots and user-facing folders; not all are source/project files."
                : "Writes under documents, desktop, and downloads.",
            writes.Where(file => IsWorkspaceFile(file, watchRoots)));
        AddBucket(buckets, workspaceBucket);

        var gitBucket = BuildFileBucket(
            "git-metadata",
            "Git Metadata",
            "Internal .git activity summarized separately from project files.",
            writes.Where(file => IsGitPath(file.Path) || (!string.IsNullOrWhiteSpace(file.RelatedPath) && IsGitPath(file.RelatedPath))));
        AddBucket(buckets, gitBucket);

        var runtimeBucket = BuildFileBucket(
            "system-runtime",
            "System / Runtime",
            "Framework and runtime bookkeeping activity.",
            writes.Where(file => PathClassifier.IsSystemRuntimeNoise(file.Path) || (!string.IsNullOrWhiteSpace(file.RelatedPath) && PathClassifier.IsSystemRuntimeNoise(file.RelatedPath))));
        AddBucket(buckets, runtimeBucket);

        var sensitiveBucket = BuildSensitiveBucket(files);
        AddBucket(buckets, sensitiveBucket);

        var networkBucket = BuildNetworkBucket(networkEvents);
        AddBucket(buckets, networkBucket);

        buckets = buckets
            .OrderByDescending(bucket => BucketRank(bucket.Key))
            .ThenByDescending(bucket => bucket.EventCount)
            .ThenBy(bucket => bucket.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var highlights = BuildHighlights(watchAll, ai, findings, buckets);
        var headline = BuildHeadline(ai, findings, buckets, networkEvents.Count);

        return new SessionActivityOverview(headline, highlights, buckets);
    }

    private static ActivityBucketSummary? BuildFileBucket(string key, string label, string description, IEnumerable<FileEvent> source)
    {
        var entries = source.ToList();
        if (entries.Count == 0)
        {
            return null;
        }

        var examples = entries
            .Select(file => DescribePathForSummary(EffectivePath(file)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        return new ActivityBucketSummary(
            key,
            label,
            description,
            entries.Count,
            entries.Select(file => NormalizeSummaryPath(EffectivePath(file))).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            entries.Where(file => file.Kind != FileEventKind.Deleted).Sum(file => Math.Max(0, file.SizeDelta)),
            examples);
    }

    private static ActivityBucketSummary? BuildSensitiveBucket(IReadOnlyList<FileEvent> files)
    {
        var entries = files
            .Where(file => file.IsSensitive)
            .ToList();

        if (entries.Count == 0)
        {
            return null;
        }

        var examples = entries
            .Select(file => DescribePathForSummary(file.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        return new ActivityBucketSummary(
            "sensitive-paths",
            "Sensitive Paths",
            "Reads or writes touching sensitive config or secret-like paths.",
            entries.Count,
            entries.Select(file => NormalizeSummaryPath(file.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            0,
            examples);
    }

    private static ActivityBucketSummary? BuildNetworkBucket(IReadOnlyList<NetworkEvent> networkEvents)
    {
        if (networkEvents.Count == 0)
        {
            return null;
        }

        var examples = networkEvents
            .Select(NetworkResolver.DisplayHost)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        return new ActivityBucketSummary(
            "network",
            "Network Destinations",
            "Distinct remote endpoints contacted by the session process tree.",
            networkEvents.Count,
            networkEvents.Select(item => $"{NetworkResolver.DisplayHost(item)}:{item.RemotePort}").Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            0,
            examples);
    }

    private static List<string> BuildHighlights(bool watchAll, AiCodingActivity ai, IReadOnlyList<Finding> findings, IReadOnlyList<ActivityBucketSummary> buckets)
    {
        var highlights = new List<string>();

        if (watchAll)
        {
            var topBucket = buckets
                .Where(bucket => bucket.Key is not "network" and not "sensitive-paths")
                .OrderByDescending(bucket => bucket.EventCount)
                .ThenByDescending(bucket => bucket.UniquePathCount)
                .FirstOrDefault();

            if (topBucket is not null)
            {
                highlights.Add($"Most activity was {topBucket.Label.ToLowerInvariant()} ({topBucket.EventCount:N0} events).");
            }
        }

        if (ai.ProjectChanges.TotalChanged > 0)
        {
            highlights.Add($"Changed {ai.ProjectChanges.TotalChanged:N0} watched-root path(s).");
        }

        if (ai.Commands.GitCommands > 0)
        {
            highlights.Add($"Ran {ai.Commands.GitCommands:N0} git commands.");
        }

        if (ai.Commands.PackageInstalls > 0)
        {
            highlights.Add($"Installed packages {ai.Commands.PackageInstalls:N0} time(s).");
        }

        if (ai.SensitiveAccesses.Count > 0)
        {
            highlights.Add($"Touched {ai.SensitiveAccesses.Count:N0} sensitive path event(s).");
        }

        if (findings.Count(f => f.Severity is "high" or "medium") > 0)
        {
            highlights.Add($"Raised {findings.Count(f => f.Severity is "high" or "medium"):N0} medium/high finding(s).");
        }

        if (buckets.Any(bucket => bucket.Key == "network"))
        {
            var network = buckets.First(bucket => bucket.Key == "network");
            highlights.Add($"Contacted {network.UniquePathCount:N0} network endpoint(s).");
        }

        return highlights.Take(5).ToList();
    }

    private static string BuildHeadline(AiCodingActivity ai, IReadOnlyList<Finding> findings, IReadOnlyList<ActivityBucketSummary> buckets, int networkCount)
    {
        var topBucket = buckets
            .Where(bucket => bucket.Key is not "network" and not "sensitive-paths")
            .OrderByDescending(bucket => bucket.EventCount)
            .ThenByDescending(bucket => bucket.UniquePathCount)
            .FirstOrDefault();

        var parts = new List<string>();

        if (topBucket is not null)
        {
            parts.Add($"Mostly {topBucket.Label.ToLowerInvariant()}");
        }

        if (ai.ProjectChanges.TotalChanged > 0)
        {
            parts.Add($"changed {ai.ProjectChanges.TotalChanged:N0} watched-root path(s)");
        }

        if (ai.Commands.GitCommands > 0)
        {
            parts.Add($"ran {ai.Commands.GitCommands:N0} git commands");
        }
        else if (ai.Commands.Total > 0)
        {
            parts.Add($"ran {ai.Commands.Total:N0} developer commands");
        }

        if (ai.SensitiveAccesses.Count > 0 || findings.Any(f => f.Title.Contains("Sensitive path", StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add("touched sensitive paths");
        }

        if (networkCount > 0)
        {
            parts.Add($"contacted {networkCount:N0} network endpoint(s)");
        }

        if (parts.Count == 0)
        {
            return "No high-signal activity summary was derived from this session.";
        }

        return string.Join(", ", parts) + ".";
    }

    private static bool IsWorkspaceFile(FileEvent file, IReadOnlyList<string> watchRoots)
    {
        var effectivePath = EffectivePath(file);
        if (string.IsNullOrWhiteSpace(effectivePath))
        {
            return false;
        }

        if (IsGitPath(effectivePath) || PathClassifier.IsSystemRuntimeNoise(effectivePath))
        {
            return false;
        }

        if (watchRoots.Count > 0 && PathClassifier.IsUnderAnyWatchRoot(effectivePath, watchRoots))
        {
            return true;
        }

        var category = PathClassifier.Classify(effectivePath);
        return category is "documents" or "desktop" or "downloads";
    }

    private static string EffectivePath(FileEvent file) =>
        file.Kind == FileEventKind.Renamed && !string.IsNullOrWhiteSpace(file.RelatedPath)
            ? file.RelatedPath!
            : file.Path;

    private static void AddBucket(List<ActivityBucketSummary> buckets, ActivityBucketSummary? bucket)
    {
        if (bucket is not null)
        {
            buckets.Add(bucket);
        }
    }

    private static int BucketRank(string key) => key switch
    {
        "workspace" => 100,
        "sensitive-paths" => 95,
        "app-data-cache" => 90,
        "temp-churn" => 80,
        "git-metadata" => 70,
        "system-runtime" => 60,
        "network" => 50,
        _ => 0
    };

    private static bool IsGitPath(string path) =>
        path.Replace('/', '\\').Contains("\\.git\\", StringComparison.OrdinalIgnoreCase);

    private static string DescribePathForSummary(string path)
    {
        var normalized = path.Replace('/', '\\');
        if (normalized.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase))
        {
            return HtmlReport.DescribeGitInternalPath(path);
        }

        if (PathClassifier.IsSystemRuntimeNoise(path))
        {
            return HtmlReport.DescribeSystemRuntimePath(path);
        }

        return normalized;
    }

    private static string NormalizeSummaryPath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}

internal static class NetworkSummaryAnalyzer
{
    public static SessionNetworkOverview Build(
        IReadOnlyList<NetworkEvent> networkEvents,
        IReadOnlyList<ProcessRecord> processes)
    {
        if (networkEvents.Count == 0)
        {
            return new SessionNetworkOverview([], []);
        }

        var processLookup = processes
            .GroupBy(process => process.ProcessId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.LastSeen).First(), EqualityComparer<int>.Default);

        var destinations = networkEvents
            .GroupBy(item => DestinationKey(item), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.OrderBy(item => item.FirstSeen).ToList();
                var first = items[0];
                var hostLabel = NetworkResolver.DisplayHostLabel(first);
                var ports = items.Select(item => item.RemotePort).Distinct().OrderBy(port => port).ToList();
                var addresses = items.Select(item => item.RemoteAddress).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
                var processNames = items
                    .Select(item => ResolveProcessName(item.ProcessId, processLookup))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new NetworkDestinationSummary(
                    hostLabel,
                    first.RemoteAddress,
                    items.Count,
                    processNames.Count,
                    processNames,
                    ports,
                    addresses);
            })
            .OrderByDescending(item => item.ConnectionCount)
            .ThenByDescending(item => item.ProcessCount)
            .ThenBy(item => item.HostLabel, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToList();

        var processSummaries = networkEvents
            .GroupBy(item => ResolveProcessName(item.ProcessId, processLookup), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.ToList();
                var destinations = items
                    .Select(NetworkResolver.DisplayHostLabel)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Take(6)
                    .ToList();

                return new NetworkProcessSummary(
                    group.Key,
                    items.Count,
                    items.Select(DestinationKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    destinations);
            })
            .OrderByDescending(item => item.ConnectionCount)
            .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        return new SessionNetworkOverview(destinations, processSummaries);
    }

    private static string DestinationKey(NetworkEvent item) =>
        string.IsNullOrWhiteSpace(item.RemoteHost)
            ? item.RemoteAddress
            : item.RemoteHost;

    private static string ResolveProcessName(int processId, IReadOnlyDictionary<int, ProcessRecord> processes)
    {
        if (processes.TryGetValue(processId, out var process))
        {
            return process.Name;
        }

        return $"PID {processId}";
    }
}
