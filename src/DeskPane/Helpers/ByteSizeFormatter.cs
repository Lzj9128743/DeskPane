namespace DeskPane.Helpers;

public static class ByteSizeFormatter
{
    public static string FormatSize(long bytes)
    {
        if (bytes < 0)
        {
            return "0 B";
        }

        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double kb = bytes / 1024.0;
        if (kb < 1024)
        {
            return $"{kb:F1} KB";
        }

        double mb = kb / 1024.0;
        if (mb < 1024)
        {
            return $"{mb:F1} MB";
        }

        return $"{mb / 1024.0:F2} GB";
    }
}
