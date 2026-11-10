using Velopack;
using Velopack.Sources;

namespace Island.Lab;

/// <summary>
/// Keeps an installed island up to date, by itself. Half a minute after it starts (and every six hours) it
/// asks the project's GitHub releases whether there is a newer version, downloads it in the background and
/// arranges for it to be installed the next time the island closes: nothing restarts under your hands.
/// Only the installed app does this (the one from Setup.exe); a build from source or the portable exe never
/// checks, and <c>ISLAND_NO_UPDATE=1</c> turns it off.
/// </summary>
public static class Updater
{
    const string Repo = "https://github.com/UsoguiH/dynamic-island-windows";
    static string? _notice;

    /// <summary>The installed version ("0.2.0"), or null when this isn't an installed copy.</summary>
    public static string? Version { get; private set; }

    public static void Start()
    {
        if (Island.Showcase.On || Environment.GetEnvironmentVariable("ISLAND_NO_UPDATE") == "1") return;
        new Thread(Run) { IsBackground = true, Name = "Updater", Priority = ThreadPriority.Lowest }.Start();
    }

    /// <summary>One line to tell the user (once), when a new version has been downloaded.</summary>
    public static string? TakeNotice() => Interlocked.Exchange(ref _notice, null);

    static void Run()
    {
        try
        {
            var mgr = new UpdateManager(new GithubSource(Repo, null, false));
            if (!mgr.IsInstalled) return;
            Version = mgr.CurrentVersion?.ToString();
            Thread.Sleep(30_000);
            while (true)
            {
                try
                {
                    var update = mgr.CheckForUpdatesAsync().GetAwaiter().GetResult();
                    if (update != null)
                    {
                        mgr.DownloadUpdatesAsync(update).GetAwaiter().GetResult();
                        // installed when the island next closes (sign-out, shutdown, Quit); it is not restarted for it
                        mgr.WaitExitThenApplyUpdates(update, silent: true, restart: false);
                        _notice = update.TargetFullRelease.Version.ToString();
                        Diag.Log($"update {_notice} downloaded; it installs when the island closes");
                        return;
                    }
                }
                catch (Exception ex) { Diag.Log("update check: " + ex.Message); }
                Thread.Sleep(TimeSpan.FromHours(6));
            }
        }
        catch (Exception ex) { Diag.Log("updater: " + ex.Message); }
    }
}
