namespace WatermarkRemover.Core.Features.Naming;

/// <summary>
/// Scores the recognisable text of a document against every known
/// <see cref="DocumentProfile"/> and picks the best match. Anchors count once each, and the
/// winner must clear both an absolute score and a margin over the runner-up — weak matches
/// stay unclassified rather than being misnamed.
/// </summary>
public static class DocumentClassifier
{
    /// <summary>Minimum winning score; profiles sum to ~15-20 on a typical match.</summary>
    private const int MinScore = 6;

    /// <summary>The winner must beat the runner-up by this much.</summary>
    private const int MinMargin = 3;

    /// <summary>Returns the best profile or null when nothing matches convincingly.</summary>
    public static DocumentProfile? Classify(string text)
    {
        (DocumentProfile? profile, int score, int runnerUp) = ClassifyDetailed(text);
        return profile is not null && score - runnerUp >= MinMargin ? profile : null;
    }

    /// <summary>
    /// Scores every profile and exposes the winner with both raw scores. The profile is
    /// non-null once the absolute score clears <see cref="MinScore"/>; the margin gate stays
    /// with <see cref="Classify"/> — callers that need to see borderline winners (the
    /// recognition report's classify_low_margin rule) judge the margin themselves.
    /// </summary>
    public static (DocumentProfile? Profile, int Score, int RunnerUp) ClassifyDetailed(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, 0, 0);

        DocumentProfile? best = null;
        int bestScore = 0;
        int runnerUp = 0;

        foreach (var profile in DocumentProfileCatalog.All)
        {
            int score = Score(profile, text);
            if (score > bestScore)
            {
                runnerUp = bestScore;
                bestScore = score;
                best = profile;
            }
            else if (score > runnerUp)
            {
                runnerUp = score;
            }
        }

        return (best is not null && bestScore >= MinScore ? best : null, bestScore, runnerUp);
    }

    private static int Score(DocumentProfile profile, string text)
    {
        int total = 0;
        foreach (var anchor in profile.Anchors)
        {
            if (anchor.Regex.IsMatch(text)) total += anchor.Weight;
        }
        return total;
    }
}
