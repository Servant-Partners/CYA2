using Cya2.Application.Interfaces;
using Cya2.Application.Services;
using cya2.Services.Imports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cya2.Web.Tests;

public sealed class ImportWorkQueueAdmissionTests
{
    [Fact]
    public async Task RejectsThirdAdmissionWithoutWaiting()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var queue = new ImportWorkQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ImportWorkQueue>.Instance);
        await queue.EnqueueAsync(Item("one"));
        await queue.EnqueueAsync(Item("two"));
        await Assert.ThrowsAsync<ImportUploadCapacityException>(() => queue.EnqueueAsync(Item("three")).AsTask());
    }

    [Fact]
    public async Task CancellationRequiresMatchingOwnerAndAdmittedProgressId()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var queue = new ImportWorkQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ImportWorkQueue>.Instance);
        await queue.EnqueueAsync(Item("one"));
        Assert.False(queue.RequestCancellation("one", 2));
        Assert.False(queue.RequestCancellation("missing", 1));
        Assert.True(queue.RequestCancellation("one", 1));
    }

    [Fact]
    public async Task OversizedAdmissionDoesNotConsumeCapacity()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var queue = new ImportWorkQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ImportWorkQueue>.Instance);
        await Assert.ThrowsAsync<ImportUploadCapacityException>(() => queue.EnqueueAsync(Item("large") with { Data = new byte[2 * ImportUploadOptions.DefaultMaximumUploadBytes + 1] }).AsTask());
        await queue.EnqueueAsync(Item("one"));
        await queue.EnqueueAsync(Item("two"));
    }

    private static ImportWorkItem Item(string progressId) => new("donations", "preview", progressId, 1, new byte[10]);
}
