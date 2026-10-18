using System.IO;

namespace PowerControlProWpf;

internal sealed record UpdateRecoveryResult(bool Restored, string? Warning);

internal static class UpdateRecovery
{
    // Callbacks keep process control testable without replacing an installed application.
    internal static async Task<UpdateRecoveryResult> InstallAsync(string target, string staged, string backup,
        Func<Task<bool>> startAndConfirm, Func<Task> stopFailedProcess, Action startRestored, bool retainBackup = false)
    {
        if (!retainBackup && File.Exists(backup)) throw new IOException("Vorhandene Sicherung wird nicht überschrieben: " + backup);
        if (retainBackup && !File.Exists(backup)) throw new IOException("Geprüfte Sicherung fehlt.");
        var replaced = false;
        try
        {
            await RetryAsync(() => File.Replace(staged, target, retainBackup ? null : backup));
            replaced = true;
            if (!await startAndConfirm())
                throw new InvalidOperationException("Die neue Version hat keinen erfolgreichen Fensterstart bestätigt.");
        }
        catch (Exception failure)
        {
            // Windows can leave the backup behind even if File.Replace reports a partial failure.
            if (!replaced && (retainBackup || !File.Exists(backup))) throw;
            // Never overwrite a still-running candidate. Keep backup if termination/restoration fails.
            await stopFailedProcess();
            await RetryAsync(() => { if (retainBackup) File.Copy(backup, target, true); else File.Move(backup, target, true); });
            startRestored();
            return new(true, failure.Message);
        }
        // A cleanup failure must never roll back a healthy new version.
        try { if (!retainBackup) await RetryAsync(() => File.Delete(backup)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new(false, "Update erfolgreich; Sicherung konnte noch nicht gelöscht werden: " + backup + "\n" + e.Message); }
        return new(false, null);
    }

    private static async Task RetryAsync(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (Exception e) when (attempt < 19 && e is IOException or UnauthorizedAccessException)
            { await Task.Delay(250); }
        }
    }
}
