﻿using WatermarkRemover.Core.Processing;

namespace WatermarkRemover.Tests.Core;

/// <summary>
/// 输出事务的语义：临时文件与目标同目录、提交才落到最终位置、没提交就释放等于回滚，
/// 而且回滚绝不碰已经存在的旧文件。
/// </summary>
public class OutputTransactionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-tx-" + Guid.NewGuid().ToString("N"));

    public OutputTransactionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Commit_moves_the_temp_file_into_place_and_removes_the_scratch_directory()
    {
        string final = Path.Combine(_dir, "结果.txt");
        string temp;

        using (var tx = OutputTransaction.Begin(final))
        {
            temp = tx.TempPath;
            File.WriteAllText(temp, "内容");
            tx.Commit();
        }

        Assert.False(File.Exists(temp));
        Assert.Equal("内容", File.ReadAllText(final));
        // 临时目录随事务一起消失，输出目录里不残留任何脚手架。
        Assert.Empty(Directory.GetDirectories(_dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Dispose_without_commit_rolls_back_and_leaves_no_output()
    {
        string final = Path.Combine(_dir, "结果.txt");
        string temp;

        using (var tx = OutputTransaction.Begin(final))
        {
            temp = tx.TempPath;
            File.WriteAllText(temp, "半截内容");
        }

        Assert.False(File.Exists(temp));
        Assert.False(File.Exists(final));
        Assert.Empty(Directory.GetDirectories(_dir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Commit_refuses_to_overwrite_a_file_that_already_exists()
    {
        string final = Path.Combine(_dir, "结果.txt");
        File.WriteAllText(final, "原有内容");

        using var tx = OutputTransaction.Begin(final);
        File.WriteAllText(tx.TempPath, "新内容");

        Assert.ThrowsAny<IOException>(() => tx.Commit());

        // 目标保持原样，半截的临时文件由 Dispose 收走。
        Assert.Equal("原有内容", File.ReadAllText(final));
    }

    [Fact]
    public void The_temp_file_lives_beside_the_target_so_the_commit_is_a_same_volume_rename()
    {
        string final = Path.Combine(_dir, "结果.txt");

        using var tx = OutputTransaction.Begin(final);

        Assert.StartsWith(_dir, tx.TempPath);
        Assert.Equal(".txt", Path.GetExtension(tx.TempPath));
    }

    [Fact]
    public void An_empty_output_path_is_rejected_up_front()
    {
        Assert.Throws<ArgumentException>(() => OutputTransaction.Begin("  "));
    }
}
