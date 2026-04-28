namespace AppLedger;

internal static class Format
{
    public static string Bytes(long bytes)
    {
        var sign = bytes < 0 ? "-" : "";
        double value = Math.Abs(bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{sign}{value:0.#} {units[unit]}";
    }
}
