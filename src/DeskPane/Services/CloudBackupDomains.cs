namespace DeskPane.Services;

[Flags]
public enum CloudBackupDomain
{
    None = 0,
    WidgetStyle = 4
}

internal static class CloudBackupDomains
{
    internal const string BackupKind = "cloud-backup";
    internal const string WidgetStyleEntryName = "widget-style.json";

    internal static readonly CloudBackupDomain[] FileDomains = [];

    internal static bool IsInDomain(CloudBackupDomain domain, string relativePath) => false;

    internal static bool IsInScope(CloudBackupDomain scope, string relativePath) => false;

    internal static string ToManifestName(CloudBackupDomain domain) => domain switch
    {
        CloudBackupDomain.WidgetStyle => "widget-style",
        _ => throw new ArgumentOutOfRangeException(nameof(domain), domain, null)
    };

    internal static bool TryFromManifestName(string name, out CloudBackupDomain domain)
    {
        domain = name switch
        {
            "widget-style" => CloudBackupDomain.WidgetStyle,
            _ => CloudBackupDomain.None
        };
        return domain != CloudBackupDomain.None;
    }

    internal static string[] ToManifestNames(CloudBackupDomain scope)
    {
        return scope.HasFlag(CloudBackupDomain.WidgetStyle)
            ? [ToManifestName(CloudBackupDomain.WidgetStyle)]
            : [];
    }

    internal static CloudBackupDomain FromManifestNames(IEnumerable<string>? names)
    {
        var scope = CloudBackupDomain.None;
        if (names is null)
        {
            return scope;
        }

        foreach (string name in names)
        {
            if (TryFromManifestName(name, out CloudBackupDomain domain))
            {
                scope |= domain;
            }
        }

        return scope;
    }
}
