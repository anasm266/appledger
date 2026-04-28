namespace AppLedger;

internal static class CsvReport
{
    public static string RenderFiles(IReadOnlyList<FileEvent> events)
    {
        var builder = new StringBuilder();
        builder.AppendLine("kind,source,observed_at,process_id,process_name,process_parent_id,process_creation_time,process_instance_key,process_exe_path,process_command_line_hash,process_first_seen,process_last_seen,attribution_confidence,attribution_reason,category,size_before,size_after,size_delta,is_sensitive,path,related_path");
        foreach (var item in events)
        {
            builder.AppendLine(string.Join(",", [
                Csv(item.Kind.ToString()),
                Csv(item.Source),
                Csv(item.ObservedAt.ToString("O", CultureInfo.InvariantCulture)),
                Csv(item.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? ""),
                Csv(item.ProcessName ?? ""),
                Csv(item.Process?.ParentPid.ToString(CultureInfo.InvariantCulture) ?? ""),
                Csv(item.Process?.CreationTime?.ToString("O", CultureInfo.InvariantCulture) ?? ""),
                Csv(item.Process?.ProcessInstanceKey ?? ""),
                Csv(item.Process?.ExePath ?? ""),
                Csv(item.Process?.CommandLineHash ?? ""),
                Csv(item.Process?.FirstSeen.ToString("O", CultureInfo.InvariantCulture) ?? ""),
                Csv(item.Process?.LastSeen.ToString("O", CultureInfo.InvariantCulture) ?? ""),
                Csv(item.Attribution?.Confidence.ToString() ?? ""),
                Csv(item.Attribution?.Reason ?? ""),
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
