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
        if (string.IsNullOrWhiteSpace(text)) return null;

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

        return best is not null && bestScore >= MinScore && bestScore - runnerUp >= MinMargin
            ? best
            : null;
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
