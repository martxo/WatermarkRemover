using WatermarkRemover.App.Updates;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// The startup auto-apply guard: a package that failed to install once must not turn every
/// launch into another apply-and-restart cycle. Same target + same window = refuse; a different
/// target or an expired window = allow.
/// </summary>
public class UpdateApplyGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-apply-guard-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void The_same_target_is_attempted_once_per_cooldown_then_allowed_again()
    {
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        const string exe = @"C:\Users\dx\AppData\Local\WatermarkRemover\current\WatermarkRemover.exe";

        Assert.True(UpdateApplyGuard.ShouldAttempt(_dir, "1.16.27", "1.16.24", exe, now),
            "the first launch after a download applies it");

        UpdateApplyAttempt? attempt = UpdateApplyGuard.Last(_dir);
        Assert.NotNull(attempt);
        Assert.Equal("1.16.27", attempt!.TargetVersion);
        Assert.Equal("1.16.24", attempt.RunningVersion);
        Assert.Equal(exe, attempt.ExePath);

        Assert.False(UpdateApplyGuard.ShouldAttempt(_dir, "1.16.27", "1.16.24", exe, now.AddMinutes(1)),
            "a relaunch caused by the failed apply must not apply again");
        Assert.False(UpdateApplyGuard.ShouldAttempt(_dir, "1.16.27", "1.16.24", exe, now.AddMinutes(9)),
            "still inside the cooldown");
        Assert.True(
            UpdateApplyGuard.ShouldAttempt(_dir, "1.16.27", "1.16.24", exe, now.Add(UpdateApplyGuard.Cooldown)),
            "after the cooldown a genuine retry is allowed");
    }

    [Fact]
    public void A_different_target_version_is_a_fresh_decision()
    {
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdateApplyGuard.ShouldAttempt(_dir, "1.16.27", "1.16.24", "a.exe", now));

        Assert.True(UpdateApplyGuard.ShouldAttempt(_dir, "1.16.28", "1.16.24", "a.exe", now.AddMinutes(1)),
            "a newer pending package must not be blocked by an older attempt");
        Assert.Equal("1.16.28", UpdateApplyGuard.Last(_dir)!.TargetVersion);
    }
}
