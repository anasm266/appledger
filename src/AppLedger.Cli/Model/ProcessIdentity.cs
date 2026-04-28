namespace AppLedger;

internal sealed record ProcessIdentity(
    int Pid,
    int ParentPid,
    DateTimeOffset? CreationTime,
    string? ExePath,
    string? CommandLineHash,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen)
{
    public static ProcessIdentity From(ProcessRecord process) =>
        new(
            process.ProcessId,
            process.ParentProcessId,
            process.CreationDate,
            process.ExecutablePath,
            HashCommandLine(process.CommandLine),
            process.FirstSeen,
            process.LastSeen);

    public static string? HashCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(commandLine));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
