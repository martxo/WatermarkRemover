namespace WatermarkRemover.Core.Recognition.Review;

/// <summary>
/// Second-pass checker over a finished <see cref="RecognitionReport"/> (Issues not yet
/// assigned). The rule-based implementation ships today; an LLM reviewer — "人工智能再识别
/// 一遍" — plugs in later by implementing this interface only.
/// </summary>
public interface IRecognitionReportReviewer
{
    IReadOnlyList<ReviewIssue> Review(RecognitionReport report);
}
