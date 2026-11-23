using System.Collections.Immutable;
using cya2.Services.Imports;
using Xunit;

namespace Cya2.Web.Tests;

public sealed class ImportProgressSnapshotTests
{
    [Fact]
    public void SnapshotRemainsUnchangedAfterUpdates()
    {
        var service = new ImportProgressService();
        service.Start("job", "donations", "owner");
        service.Report("job", 10, 8, 2, "Processing");
        service.AddStep("job", "Prepare", "Running");
        service.AddErrors("job", ["First"]);
        var before = service.GetOwned("job", "owner")!;

        service.Report("job", 20, 18, 2, "Finishing");
        service.CompleteStep("job", "Prepare", "Done");
        service.AddErrors("job", ["Second"]);
        service.Complete("job");

        Assert.Equal(10, before.TotalRows);
        Assert.Equal("Processing", before.Status);
        Assert.False(before.IsComplete);
        Assert.Equal("Running", Assert.Single(before.Steps).Status);
        Assert.True(before.Steps[0].IsActive);
        Assert.Equal("First", Assert.Single(before.Errors));
        Assert.IsType<ImmutableArray<string>>(before.Errors);
        Assert.True(service.GetOwned("job", "owner")!.IsComplete);
    }

    [Fact]
    public void ReadsRespectOwnershipAndType()
    {
        var service = new ImportProgressService();
        service.Start("job", "donations", "owner");
        Assert.Null(service.GetOwned("job", "another"));
        Assert.Null(service.GetOwned("job", "owner", "accounting"));
        Assert.NotNull(service.GetOwned("job", "owner", "DONATIONS"));
    }

    [Fact]
    public async Task ConcurrentReportsProduceConsistentSnapshots()
    {
        var service = new ImportProgressService();
        service.Start("job", "donations", "owner");
        var writer = Task.Run(() =>
        {
            for (var i = 1; i <= 50000; i++) service.Report("job", i, i, i, i.ToString());
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 10000; i++)
            {
                var snapshot = service.GetOwned("job", "owner")!;
                Assert.Equal(snapshot.TotalRows, snapshot.InsertedRows);
                Assert.Equal(snapshot.TotalRows, snapshot.FailedRows);
                if (snapshot.TotalRows > 0) Assert.Equal(snapshot.TotalRows.ToString(), snapshot.Status);
            }
        }));
        await Task.WhenAll(readers.Append(writer));
    }

    [Fact]
    public void ExpirationDoesNotRemoveActiveProgress()
    {
        var service = new ImportProgressService();
        service.Start("active", "donations", "owner");
        service.Start("finished", "donations", "owner");
        service.Complete("finished");
        Assert.Equal(1, service.RemoveExpired(DateTime.UtcNow.AddHours(1), TimeSpan.FromMinutes(15)));
        Assert.NotNull(service.GetOwned("active", "owner"));
        Assert.Null(service.GetOwned("finished", "owner"));
    }
}
