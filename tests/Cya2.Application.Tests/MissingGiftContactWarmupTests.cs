using Cya2.Application.DTOs;
using Cya2.Application.Services;
using Cya2.Core.Enums;
using Cya2.Core.Interfaces;
using Cya2.Core.ReadModels;
using Cya2.Core.Services;
using Cya2.Core.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cya2.Application.Tests;

public sealed class MissingGiftContactWarmupTests
{
    private static readonly DateRange Range = new(new DateTime(2025, 1, 1), new DateTime(2025, 6, 30));
    private static readonly AccountOptionDto Account = new() { AccountId = 1, Fund = "Synthetic" };

    [Fact]
    public async Task Warmup_LoadsOnlyWarningContacts_AndPreservesFullScopeFreshness()
    {
        var fixture = new Fixture();
        var rows = History(1, 1, 2, 3, 4).Concat(History(2, 1, 2, 3, 4, 5, 6)).ToList();
        rows.Add(rows[0]);

        await fixture.Service.WarmMissingGiftDonorsFromDataAsync(Account, rows, Range);
        await fixture.Service.WarmMissingGiftDonorsFromDataAsync(Account, rows, Range);

        Assert.Equal(new long[] { 1 }, Assert.Single(fixture.Repository.Requests));
        Assert.True(fixture.Contacts.TryGet(1, 0, out _));
        Assert.False(fixture.Contacts.TryGet(2, 0, out _));
        Assert.All(rows.Where(row => row.DonorId == 2), row => Assert.Null(row.Email));
        var warning = Assert.Single(fixture.ReadWarnings());
        Assert.Equal(1, warning.DonorId);
        Assert.Single(warning.MissingMonths);
    }

    [Fact]
    public async Task Warmup_CachesEmptyWarnings_WithoutLoadingContacts()
    {
        var fixture = new Fixture();
        await fixture.Service.WarmMissingGiftDonorsFromDataAsync(Account, History(1, 1, 2, 3, 4, 5, 6), Range);
        Assert.Empty(fixture.ReadWarnings());

        await fixture.Service.WarmMissingGiftDonorsFromDataAsync(Account, History(2, 1, 2, 6), Range);

        Assert.Empty(fixture.Repository.Requests);
        Assert.Empty(fixture.ReadWarnings());
    }

    [Fact]
    public async Task Warmup_DoesNotLoadInvalidAnonymousOrOutOfRangeDonorContacts()
    {
        var fixture = new Fixture();
        var rows = History(0, 1, 2, 6).Concat(History(2, 1, 2, 6)).ToList();
        foreach (var row in rows.Where(row => row.DonorId == 2)) row.IsAnonymous = true;
        rows.Add(new DonationRecord { Id = 999, DonorId = 3, Date = new DateTime(2024, 12, 1) });

        await fixture.Service.WarmMissingGiftDonorsFromDataAsync(Account, rows, Range);

        Assert.Empty(fixture.Repository.Requests);
        Assert.Equal(0, Assert.Single(fixture.ReadWarnings()).DonorId);
    }

    [Fact]
    public async Task Warmup_ReusesWarningContactCacheAcrossAccounts_IncludingEmptyContacts()
    {
        var fixture = new Fixture();
        var rows = History(1, 1, 2, 6);
        await fixture.Service.WarmMissingGiftDonorsFromDataAsync(Account, rows, Range);
        await fixture.Service.WarmMissingGiftDonorsFromDataAsync(
            new AccountOptionDto { AccountId = 2, Fund = "OtherSynthetic" }, rows, Range);

        Assert.Single(fixture.Repository.Requests);
        Assert.True(fixture.Contacts.TryGet(1, 0, out var contacts));
        Assert.Empty(contacts);
    }

    [Fact]
    public async Task Warmup_PreservesPreviouslyUserLoadedContacts()
    {
        var fixture = new Fixture();
        fixture.Contacts.Set(2, 0, [new DonorContactRecord { DonorId = 2, ContactType = "Email", ContactValue = "synthetic@example.test" }]);

        await fixture.Service.WarmMissingGiftDonorsFromDataAsync(Account, History(2, 1, 2, 3, 4, 5, 6), Range);

        Assert.Empty(fixture.Repository.Requests);
        Assert.True(fixture.Contacts.TryGet(2, 0, out var contacts));
        Assert.Single(contacts);
    }

    [Fact]
    public async Task Warmup_ReloadsAfterContactInvalidation()
    {
        var fixture = new Fixture();
        var rows = History(1, 1, 2, 6);
        await fixture.Service.WarmMissingGiftDonorsFromDataAsync(Account, rows, Range);
        fixture.Versions.Invalidate(CacheDataDomains.DonorContacts);
        fixture.Contacts.InvalidateAll();

        await fixture.Service.WarmMissingGiftDonorsFromDataAsync(Account, rows, Range);

        Assert.Equal(2, fixture.Repository.Requests.Count);
        Assert.True(fixture.Contacts.TryGet(1, 1, out _));
        Assert.Single(fixture.ReadWarnings());
    }

    [Fact]
    public async Task Warmup_CancellationDoesNotPublishWarnings()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Repository.OnLoad = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.WarmMissingGiftDonorsFromDataAsync(
            Account, History(1, 1, 2, 6), Range, cancellation.Token));

        Assert.False(fixture.Warnings.TryGet(Account.AccountId, Account.Fund, Range.StartDate, Range.EndDate, 0, 0, 0, out _));
    }

    private static List<DonationRecord> History(long donorId, params int[] months)
        => months.Select(month => new DonationRecord
        {
            Id = (int)donorId * 10 + month, AccountId = 1, DonorId = donorId,
            DonorDisplayName = "Synthetic " + donorId, Fund = "Synthetic",
            Date = new DateTime(2025, month, 5), Amount = 25, Frequency = DonorFrequency.Monthly
        }).ToList();

    private sealed class Fixture
    {
        public RecordingContacts Repository { get; } = new();
        public DonorContactCache Contacts { get; } = new();
        public MissingGiftCache Warnings { get; } = new();
        public CacheInvalidationVersion Versions { get; } = new();
        public DonorService Service { get; }

        public Fixture()
        {
            var resolver = new DonorIdentityResolver();
            Service = new DonorService(null!, new DonorContactHydrationService(Repository, Contacts, Versions, resolver),
                null!, NullLogger<DonorService>.Instance, new DonorSummaryCache(), Warnings, Versions,
                new DonorFrequencyService(), new DonorMissingGiftService(), resolver);
        }

        public List<DonorSummaryDto> ReadWarnings()
        {
            Assert.True(Warnings.TryGet(Account.AccountId, Account.Fund, Range.StartDate, Range.EndDate,
                Versions.Donations, Versions.DonorContacts, Versions.Authorization, out var warnings));
            return warnings;
        }
    }

    private sealed class RecordingContacts : IDonorContactReadRepository
    {
        public List<long[]> Requests { get; } = [];
        public Action? OnLoad { get; set; }

        public Task<List<DonorContactRecord>> GetByDonorIdsAsync(IEnumerable<long> donorIds, CancellationToken cancellationToken = default)
        {
            Requests.Add(donorIds.Order().ToArray());
            OnLoad?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new List<DonorContactRecord>());
        }
    }
}
