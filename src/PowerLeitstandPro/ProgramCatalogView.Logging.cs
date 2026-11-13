namespace PowerControlProWpf;

internal sealed partial class ProgramCatalogView
{
    private void ReportProgramError(Exception error, string operation)
    {
        var record = new AppLogError(L("Programme", "Programs"), operation, UiText.Error(error, _english), "0x" + error.HResult.ToString("X8"));
        if (WriteErrorLog is not null) WriteErrorLog(record);
        else WriteLog?.Invoke(record.Message);
    }
}
