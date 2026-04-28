namespace AppLedger;

internal static class CsvReport
{
    public static string RenderFiles(IReadOnlyList<FileEvent> events)
    {
        var builder = new StringBuilder();
        builder.AppendLine("kind,source,observed_at,process_id,process_name,category,size_before,size_after,size_delta,is_sensitive,path,related_path");
        foreach (var item in events)
        {
            builder.AppendLine(string.Join(",", [
                Csv(item.Kind.ToString()),
                Csv(item.Source),
                Csv(item.ObservedAt.ToString("O", CultureInfo.InvariantCulture)),
                Csv(item.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? ""),
                Csv(item.ProcessName ?? ""),
                Csv(item.Category),
                Csv(item.SizeBefore?.ToString(CultureInfo.InvariantCulture) ?? ""),
                Csv(item.SizeAfter?.ToString(CultureInfo.InvariantCulture) ?? ""),
                Csv(item.SizeDelta.ToString(CultureInfo.InvariantCulture)),
                Csv(item.IsSensitive.ToString(CultureInfo.InvariantCulture)),
                Csv(item.Path),
                Csv(item.RelatedPath ?? "")
            ]));
        }

        return builder.ToString();
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
