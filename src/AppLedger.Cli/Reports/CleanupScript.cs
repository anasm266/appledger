namespace AppLedger;

internal static class CleanupScript
{
    public static string Render(SessionReport session)
    {
        var candidates = session.TopFolders
            .Where(f => f.Category is "temp" or "app-data" && f.BytesAdded > 0)
            .Take(20)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine("# AppLedger conservative cleanup draft");
        builder.AppendLine("# Review every path before uncommenting. AppLedger does not delete anything automatically.");
        builder.AppendLine();

        foreach (var folder in candidates)
        {
            builder.AppendLine($"# {Format.Bytes(folder.BytesAdded)} observed under {folder.Path}");
            builder.AppendLine($"# Remove-Item -LiteralPath '{folder.Path.Replace("'", "''", StringComparison.Ordinal)}' -Recurse -Force");
            builder.AppendLine();
        }

        return builder.ToString();
    }
}
