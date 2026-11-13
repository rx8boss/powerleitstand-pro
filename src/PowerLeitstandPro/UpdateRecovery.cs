using System.IO;

namespace PowerControlProWpf;

internal sealed record UpdateRecoveryResult(bool Restored, string? Warning);

internal static class UpdateRecovery
{
    // Callbacks keep process control testable without replacing an installed application.
    internal static async Task<UpdateRecoveryResult> InstallAsync(string target, string staged, string backup,
        Func<Task<bool>> startAndConfirm, Func<Task> stopFailedProcess, Action startRestored, bool retainBackup = false,
        Action? replaceFile = null)
    {
        if (!retainBackup && File.Exists(backup)) throw new IOException("Vorhandene Sicherung wird nicht überschrieben: " + backup);
        if (retainBackup && !File.Exists(backup)) throw new IOException("Geprüfte Sicherung fehlt.");
        var replaced = false;
        try
        {
            await RetryAsync(replaceFile ?? (() => File.Replace(staged, target, retainBackup ? null : backup)), stopOnPartialReplace: true);
            replaced = true;
            if (!await startAndConfirm())
                throw new InvalidOperationException("Die neue Version hat keinen erfolgreichen Fensterstart bestätigt.");
        }
        catch (Exception failure)
        {
            // Windows can leave the backup behind even if File.Replace reports a partial failure.
            if (!replaced && (retainBackup ? !IsPartialReplace(failure) || File.Exists(target) : !File.Exists(backup))) throw;
            // Never overwrite a still-running candidate. Keep backup if termination/restoration fails.
            await stopFailedProcess();
            if (retainBackup)
            {
                var restore = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".restore");
                try
                {
                    File.Copy(backup, restore, overwrite: false);
                    await RetryAsync(() => File.Move(restore, target, overwrite: replaced));
                }
                finally { if (File.Exists(restore)) File.Delete(restore); }
            }
            else await RetryAsync(() => File.Move(backup, target, true));
            startRestored();
            return new(true, failure.Message);
        }
        // A cleanup failure must never roll back a healthy new version.
        try { if (!retainBackup) await RetryAsync(() => File.Delete(backup)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new(false, "Update erfolgreich; Sicherung konnte noch nicht gelöscht werden: " + backup + "\n" + e.Message); }
        return new(false, null);
    }

    private static bool IsPartialReplace(Exception error)
        => error is IOException && (error.HResult & 0xffff) is 1176 or 1177;

    private static async Task RetryAsync(Action action, bool stopOnPartialReplace = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (Exception e) when (attempt < 19 && (e is IOException or UnauthorizedAccessException)
                && (!stopOnPartialReplace || !IsPartialReplace(e)))
            { await Task.Delay(250); }
        }
    }
}
