using HD.AMR.App.Service.Vision;

namespace HD.AMR.Tests;

/// <summary>히트맵 이어 학습 — 이전 best.pt 백업과 오래된 백업 정리.</summary>
public class HeatmapBackupTests
{
    [Fact]
    public void best_pt가_없으면_백업하지_않는다()
    {
        var dir = Directory.CreateTempSubdirectory("hm").FullName;
        try { Assert.Null(WeldTrainingService.BackupHeatmapBest(dir)); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 백업은_prev에_복사되고_최근_3개만_남는다()
    {
        var dir = Directory.CreateTempSubdirectory("hm").FullName;
        try
        {
            var prev = Path.Combine(dir, "prev");
            Directory.CreateDirectory(prev);
            foreach (var old in new[] { "best_20260101_000000.pt", "best_20260102_000000.pt", "best_20260103_000000.pt" })
                File.WriteAllText(Path.Combine(prev, old), "old");
            File.WriteAllText(Path.Combine(dir, "best.pt"), "current");

            var backup = WeldTrainingService.BackupHeatmapBest(dir);

            Assert.NotNull(backup);
            Assert.Equal("current", File.ReadAllText(backup!));
            Assert.True(File.Exists(Path.Combine(dir, "best.pt")));   // 원본은 그대로(이어 학습 시작점)
            var left = Directory.GetFiles(prev, "best_*.pt").Select(Path.GetFileName).OrderBy(n => n).ToList();
            Assert.Equal(WeldTrainingService.HeatmapBackupKeep, left.Count);
            Assert.DoesNotContain("best_20260101_000000.pt", left);
            Assert.Contains(Path.GetFileName(backup), left);
        }
        finally { Directory.Delete(dir, true); }
    }
}
