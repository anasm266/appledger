namespace AppLedger;

internal static class CleanupScript
{
    public static string Render(SessionReport session)
    {
        var plan = CleanupPlanner.Build(session);

        var builder = new StringBuilder();
        builder.AppendLine("# AppLedger conservative cleanup draft");
        builder.AppendLine("# Review every path before uncommenting. AppLedger does not delete anything automatically.");
        builder.AppendLine("# Run this only after the recorded app is fully closed.");
        builder.AppendLine($"# Safe candidates total: {Format.Bytes(plan.SafeBytes)}");
        builder.AppendLine($"# Review candidates total: {Format.Bytes(plan.ReviewBytes)}");
        builder.AppendLine();

        AppendBucket(builder, "Safe cleanup candidates", plan.Safe);
        AppendBucket(builder, "Review before deleting", plan.Review);

        builder.AppendLine("# Not listed: watched roots, Documents/Desktop/Downloads, .git internals, dependencies, secrets, and system runtime folders.");

        return builder.ToString();
    }

    private static void AppendBucket(StringBuilder builder, string title, IReadOnlyList<CleanupCandidate> candidates)
    {
        builder.AppendLine($"# {title}");
        if (candidates.Count == 0)
        {
            builder.AppendLine("#   None detected.");
            builder.AppendLine();
            return;
        }

        foreach (var candidate in candidates)
        {
            builder.AppendLine($"# {Format.Bytes(candidate.BytesAdded)} across {candidate.FileCount:N0} event(s) under {candidate.Path}");
            builder.AppendLine($"# Reason: {candidate.Reason}");
            builder.AppendLine($"# Remove-Item -LiteralPath '{EscapePowerShellSingleQuoted(candidate.Path)}' -Recurse -Force");
            builder.AppendLine();
        }
    }

    private static string EscapePowerShellSingleQuoted(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
