using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class SystemPerformanceProfileTests
{
    [Fact]
    public void LargerMachineReceivesMoreBoundedParallelismAndCache()
    {
        var small = SystemPerformanceProfile.ForHardware(2, 2L * 1024 * 1024 * 1024);
        var large = SystemPerformanceProfile.ForHardware(32, 64L * 1024 * 1024 * 1024);

        Assert.True(large.ScanWorkerCount > small.ScanWorkerCount);
        Assert.True(large.DirectoryWorkerCount > small.DirectoryWorkerCount);
        Assert.True(large.DuplicateHashWorkerCount > small.DuplicateHashWorkerCount);
        Assert.True(large.DirectoryCacheBudgetBytes > small.DirectoryCacheBudgetBytes);
        Assert.InRange(large.ScanWorkerCount, 2, 32);
        Assert.InRange(large.DuplicateHashWorkerCount, 2, 8);
        Assert.InRange(large.DirectoryCacheBudgetBytes, 32L * 1024 * 1024, 512L * 1024 * 1024);
    }

    [Fact]
    public void ScanOptionsUseProfileUnlessExplicitlyOverridden()
    {
        var profile = SystemPerformanceProfile.ForHardware(8, 16L * 1024 * 1024 * 1024);
        var adaptive = new ScanPipelineOptions(PerformanceProfile: profile);
        var explicitOptions = new ScanPipelineOptions(3, 77, 9, profile);

        Assert.Equal(profile.ScanWorkerCount, adaptive.EffectiveWorkerCount);
        Assert.Equal(profile.ScanQueueCapacity, adaptive.EffectiveQueueCapacity);
        Assert.Equal(profile.ScanBatchSize, adaptive.EffectiveBatchSize);
        Assert.Equal(3, explicitOptions.EffectiveWorkerCount);
        Assert.Equal(77, explicitOptions.EffectiveQueueCapacity);
        Assert.Equal(9, explicitOptions.EffectiveBatchSize);
    }
}
