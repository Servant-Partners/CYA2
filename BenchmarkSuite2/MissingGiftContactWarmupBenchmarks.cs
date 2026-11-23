using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Cya2.Application.DTOs;
using Cya2.Application.Services;
using Cya2.Core.Enums;
using Cya2.Core.Interfaces;
using Cya2.Core.ReadModels;
using Cya2.Core.Services;
using Cya2.Core.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VSDiagnostics;

[ShortRunJob]
[CPUUsageDiagnoser]
public class MissingGiftContactWarmupBenchmarks
{
    private List<DonationRecord> _donations = null!;
    private readonly DateRange _range = new(new DateTime(2025, 1, 1), new DateTime(2025, 6, 30));
    private readonly AccountOptionDto _account = new()
    {
        AccountId = 1,
        Fund = "Synthetic"
    };
    [Params(0, 100)]
    public int MissingDonors { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _donations = new List<DonationRecord>();
        for (var donor = 1; donor <= 1000; donor++)
        {
            for (var month = 1; month <= 6; month++)
            {
                if (donor <= MissingDonors && month == 5)
                    continue;
                _donations.Add(new DonationRecord { Id = donor * 10 + month, AccountId = 1, DonorId = donor, DonorDisplayName = "Synthetic " + donor, AccountName = "Synthetic " + donor, Addressee = "Synthetic " + donor, Fund = "Synthetic", Date = new DateTime(2025, month, 5), Amount = 25, Frequency = DonorFrequency.Monthly });
            }
        }
    }

    [Benchmark]
    public async Task<int> WarmMissingGiftContacts()
    {
        var repository = new SyntheticContacts();
        var versions = new CacheInvalidationVersion();
        var resolver = new DonorIdentityResolver();
        var service = new DonorService(null!, new DonorContactHydrationService(repository, new DonorContactCache(), versions, resolver), null!, NullLogger<DonorService>.Instance, new DonorSummaryCache(), new MissingGiftCache(), versions, new DonorFrequencyService(), new DonorMissingGiftService(), resolver);
        await service.WarmMissingGiftDonorsFromDataAsync(_account, _donations, _range);
        return repository.RequestedDonors;
    }

    private sealed class SyntheticContacts : IDonorContactReadRepository
    {
        public int RequestedDonors { get; private set; }

        public Task<List<DonorContactRecord>> GetByDonorIdsAsync(IEnumerable<long> donorIds, CancellationToken cancellationToken = default)
        {
            var contacts = new List<DonorContactRecord>();
            foreach (var donorId in donorIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequestedDonors++;
                contacts.Add(new DonorContactRecord { DonorId = donorId, ContactType = "Email", ContactValue = "donor-" + donorId + "@example.test" });
                contacts.Add(new DonorContactRecord { DonorId = donorId, ContactType = "MobilePhone", ContactValue = "555-" + donorId });
                contacts.Add(new DonorContactRecord { DonorId = donorId, ContactType = "AddressLine1", ContactValue = donorId + " Synthetic Street" });
                contacts.Add(new DonorContactRecord { DonorId = donorId, ContactType = "City", ContactValue = "Synthetic City" });
            }

            return Task.FromResult(contacts);
        }
    }
}