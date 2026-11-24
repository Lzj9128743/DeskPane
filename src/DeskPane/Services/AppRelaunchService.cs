using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace DeskPane.Services;

public static class AppRelaunchService
{
    // Store builds do not ship DeskPane.Updater (excluded from Store packaging),
    // so the relaunch is delegated to the WinAppSDK restart agent from the
    // graceful-shutdown tail instead of a detached helper process.
    public static bool PendingOsRestartOnShutdown { get; set; }

    public static AppRelaunchScheduleResult ScheduleAfterCurrentProcessExit()
    {
        if (AppDistributionService.Current.IsMicrosoftStore)
        {
            PendingOsRestartOnShutdown = true;
            return AppRelaunchScheduleResult.StartedSuccessfully;
        }

        string appPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DeskPane.exe");
        if (!File.Exists(appPath))
        {
            return AppRelaunchScheduleResult.Failed("DeskPane.exe was not found.");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = appPath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(appPath) ?? AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("--await-parent-exit");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Process.Start(startInfo);
            return AppRelaunchScheduleResult.StartedSuccessfully;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       InvalidOperationException or Win32Exception)
        {
            App.Log($"[Relaunch] Failed to schedule app restart: {ex}");
            return AppRelaunchScheduleResult.Failed(ex.Message);
        }
    }
}

public sealed record AppRelaunchScheduleResult(bool Started, string? ErrorMessage)
{
    public static AppRelaunchScheduleResult StartedSuccessfully { get; } = new(true, null);

    public static AppRelaunchScheduleResult Failed(string errorMessage) => new(false, errorMessage);
}
