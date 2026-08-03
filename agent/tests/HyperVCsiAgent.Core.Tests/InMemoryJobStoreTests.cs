using HyperVCsiAgent.Core.Jobs;

namespace HyperVCsiAgent.Core.Tests;

public class InMemoryJobStoreTests
{
    [Fact]
    public void GetOrCreate_SameIdempotencyKey_ReturnsSameJob()
    {
        var store = new InMemoryJobStore();

        var first = store.GetOrCreate("vol-1/CreateVolume", "CreateVolume", (_, _) => Task.CompletedTask);
        var second = store.GetOrCreate("vol-1/CreateVolume", "CreateVolume", (_, _) => Task.CompletedTask);

        Assert.Same(first, second);
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var store = new InMemoryJobStore();

        Assert.Null(store.Get("does-not-exist"));
    }
}
