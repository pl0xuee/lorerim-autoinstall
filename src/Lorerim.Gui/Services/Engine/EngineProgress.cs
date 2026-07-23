using System;
using System.Collections.Generic;

namespace Lorerim.Gui.Services.Engine;

public enum EngineOperation
{
    Downloading,
    Extracting,
    Validating,
    Installing,
    Converting,
    Building,
    Writing,
    Verifying,
    CheckingExisting,
    Completed,
    Unknown,
}

public sealed record EngineFileProgress(
    EngineOperation Operation,
    string FileName,
    double Percent,
    string? Speed,
    int? Index,
    int? Total
);

public sealed record ManualDownload(string Name, string? Url, string? Reason);

/// <summary>
/// Process-wide hub for progress events from the running jackify-engine subprocess.
/// The runner publishes from its stdout reader thread; UI consumers throttle with Rx
/// and marshal to the UI thread themselves.
/// </summary>
public class EngineProgress
{
    public event Action<EngineFileProgress>? FileProgressChanged;

    /// <summary>Fired when the engine asks for manual downloads (list is complete and it is waiting).</summary>
    public event Action<IReadOnlyList<ManualDownload>>? ManualDownloadsRequested;

    public void PublishFile(EngineFileProgress p) => FileProgressChanged?.Invoke(p);

    public void PublishManualDownloads(IReadOnlyList<ManualDownload> downloads) =>
        ManualDownloadsRequested?.Invoke(downloads);
}
