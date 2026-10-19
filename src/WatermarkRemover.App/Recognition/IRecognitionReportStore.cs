using WatermarkRemover.Core.Recognition;

namespace WatermarkRemover.App.Recognition;

/// <summary>
/// Persistence seam for recognition reports: the job queue appends after every run and the
/// settings page reads back a preview. Kept as an interface so tests inject temp directories
/// or fakes, and a future remote store would only implement this.
/// </summary>
public interface IRecognitionReportStore
{
    /// <summary>Persists one report; returns the file it was written to ("" when persistence failed).</summary>
    string Append(RecognitionReport report);

    /// <summary>The most recent reports, newest first, capped at <paramref name="maxLines"/>.</summary>
    IReadOnlyList<RecognitionReport> ReadRecent(int maxLines);
}
