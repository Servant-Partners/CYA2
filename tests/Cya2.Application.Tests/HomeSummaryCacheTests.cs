using Cya2.Application.DTOs;
using Cya2.Application.Interfaces;
using Cya2.Application.Models;
using Cya2.Application.Services;
using Cya2.Core.Entities;
using Cya2.Core.Interfaces;
using Cya2.Core.ReadModels;
using Cya2.Core.Services;
using Cya2.Core.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cya2.Application.Tests;

public sealed class HomeSummaryCacheTests
{
    [Theory]
    [InlineData(false, 1000)]
    [InlineData(true, 1000)]
    [InlineData(false, -1000)]
    [InlineData(true, -1000)]
    public async Task StandardSummary_PreservesHistoricalBalancesAndInvalidates(bool warm, double historicalAmount)
    {
        var now = DateTime.Now;
        var priorYearStart = new DateTime(now.Year - 1, 1, 1);
        var account = new UserAccountContextAccount { AccountId = 1, Fund = "General", AccountingClass = "Class", AccountNumber = "100", BalanceAdjustment = 50m };
        var rows = CreateAccountingHistory(now, historicalAmount);
        var loader = new AccountingLoader(rows);
        var versions = new CacheInvalidationVersion();
        var service = CreateStandardDashboard(account, loader, versions, new Facts(rows));
        if (warm)
        {
            await service.WarmHomeSummaryFromDataAsync(
                new Account { AccountId = 1, Fund = "General", AccountingClass = "Class", AccountNumber = "100", BalanceAdjustment = 50m },
                new CacheRebuildData { AccountingByAccountId = new Dictionary<int, List<AccountingRecord>> { [1] = rows } });
        }

        var dashboard = await service.GetDashboardSummaryDataAsync(account.Fund, "1");

        var openingBalance = 50m + (decimal)historicalAmount - 200m;
        Assert.True(dashboard.HasAccountData);
        Assert.Equal(openingBalance, dashboard.PriorYear.StartingBalance);
        Assert.Equal(openingBalance + 80m, dashboard.PriorYear.Balance);
        Assert.Equal(dashboard.PriorYear.Balance, dashboard.CurrentYear.StartingBalance);
        Assert.Equal(openingBalance + 10m, dashboard.CurrentYear.Balance);
        var processor = new AccountingTransactionProcessor(new ExpenseClassificationService());
        var currentMonthStart = new DateTime(now.Year, now.Month, 1);
        Assert.Equal(processor.Process(account, rows, null, currentMonthStart.AddDays(-1)).TotalBalance, dashboard.CurrentMonth.StartingBalance);
        Assert.Equal(processor.Process(account, rows, null, currentMonthStart.AddDays(-1)).TotalBalance, dashboard.PriorMonth.Balance);
        Assert.Equal(20m, dashboard.PriorYear.TotalExpenses);
        Assert.Equal(40m, dashboard.CurrentYear.TotalExpenses);
        Assert.Equal(-30m, dashboard.CurrentYear.InternalTransfers);

        var loads = loader.Calls;
        await service.GetDashboardSummaryDataAsync(account.Fund, "1");
        Assert.Equal(loads, loader.Calls);
        if (!warm)
            Assert.Contains(loader.Ranges, range => range.StartDate < priorYearStart.AddYears(-1));

        rows[0].Amount += 500;
        versions.Invalidate(CacheDataDomains.Accounting);
        var refreshed = await service.GetDashboardSummaryDataAsync(account.Fund, "1");
        Assert.Equal(openingBalance + 500m, refreshed.PriorYear.StartingBalance);
        Assert.Equal(openingBalance + 510m, refreshed.CurrentYear.Balance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomSummary_ReusesMatchingHomeAnchorInEitherDirection(bool beforeAnchor)
    {
        var now = DateTime.Now;
        var rows = CreateAccountingHistory(now, 1000);
        var facts = new Facts(rows);
        var account = new UserAccountContextAccount { AccountId = 1, Fund = "General", AccountingClass = "Class", AccountNumber = "100", BalanceAdjustment = 50m };
        var service = CreateStandardDashboard(account, new AccountingLoader(rows), new CacheInvalidationVersion(), facts);
        await service.GetDashboardSummaryDataAsync(account.Fund, "1");
        var start = beforeAnchor ? new DateTime(now.Year - 3, 1, 1) : new DateTime(now.Year - 1, 1, 1);
        var end = beforeAnchor ? new DateTime(now.Year - 2, 1, 1) : new DateTime(now.Year - 1, 12, 31);

        var custom = await service.GetCustomSummaryAsync(account.Fund, start, end, "1");

        var processor = new AccountingTransactionProcessor(new ExpenseClassificationService());
        Assert.Equal(processor.Process(account, rows, null, start.AddDays(-1)).TotalBalance, custom.StartingBalance);
        Assert.Equal(processor.Process(account, rows, null, end).TotalBalance, custom.Balance);
        Assert.DoesNotContain(facts.AccountingRanges, range => range.StartDate == DateTime.MinValue);
    }

    [Fact]
    public async Task Visualization_ExpensesRespectPartialFirstAndLastMonths()
    {
        var start = new DateTime(DateTime.Today.Year - 1, 3, 15);
        var end = new DateTime(start.Year, 4, 10);
        var account = new UserAccountContextAccount { AccountId = 1, Fund = "General", AccountingClass = "Class", AccountNumber = "100" };
        var rows = new List<AccountingRecord>
        {
            new() { Id = 1, AccountingClass = "Class", Date = start.AddDays(-1), Type = "Expense", Amount = 100 },
            new() { Id = 2, AccountingClass = "Class", Date = start, Type = "Expense", Amount = 10 },
            new() { Id = 3, AccountingClass = "Class", Date = end, Type = "Expense", Amount = 20 },
            new() { Id = 4, AccountingClass = "Class", Date = end.AddDays(1), Type = "Expense", Amount = 100 }
        };
        var service = CreateStandardDashboard(account, new AccountingLoader(rows), new CacheInvalidationVersion(), new Facts(rows));

        var points = await service.GetMonthlyVisualizationAsync(account.Fund, start, end, "1");

        Assert.Equal(2, points.Count);
        Assert.Equal(new DateTime(start.Year, 3, 1), points[0].MonthStart);
        Assert.Equal(10m, points[0].ExpenseTotal);
        Assert.Equal(20m, points[1].ExpenseTotal);
    }

    private static List<AccountingRecord> CreateAccountingHistory(DateTime now, double historicalAmount) =>
    [
        new() { Id = 1, AccountingClass = "Class", Date = new DateTime(now.Year - 3, 6, 1), Amount = historicalAmount, Account = "Income" },
        new() { Id = 2, AccountNumber = "100", Date = new DateTime(now.Year - 2, 12, 31), Amount = 200, Type = "Expense" },
        new() { Id = 3, AccountingClass = "Class", Date = new DateTime(now.Year - 2, 12, 31), Amount = 9999, Account = "Prepaids" },
        new() { Id = 4, AccountingClass = "Other", Date = new DateTime(now.Year - 2, 12, 31), Amount = 9999, Account = "Income" },
        new() { Id = 5, AccountingClass = "Class", Date = new DateTime(now.Year - 1, 1, 1), Amount = 100, Account = "Income" },
        new() { Id = 6, AccountingClass = "Class", Date = new DateTime(now.Year - 1, 12, 31), Amount = 20, Type = "Expense" },
        new() { Id = 7, AccountingClass = "Class", Date = new DateTime(now.Year, 1, 1), Amount = -30, Account = "Transfer" },
        new() { Id = 8, AccountingClass = "Class", Date = now.Date, Amount = 40, Type = "Expense" }
    ];

    private static FinancialDashboardService CreateStandardDashboard(UserAccountContextAccount account, AccountingLoader loader,
        CacheInvalidationVersion versions, Facts facts)
    {
        var classification = new ExpenseClassificationService();
        var calculations = new AccountCalculationService(classification, new AccountingTransactionProcessor(classification), facts);
        return new FinancialDashboardService(NullLogger<FinancialDashboardService>.Instance,
            calculations, new ReverseBalanceCalculationService(classification),
            new DonationDataCache(NullLogger<DonationDataCache>.Instance), new EmptyDonationLoader(),
            new AccountingDataCache(NullLogger<AccountingDataCache>.Instance), loader,
            new DashboardContext(account), null!, new AllowDashboardAccess(), new AccountBalanceCache(), versions,
            new HomeSummaryCache(NullLogger<HomeSummaryCache>.Instance), facts);
    }

    private sealed class AccountingLoader(List<AccountingRecord> rows) : IAccountingDataCacheLoader
    {
        public int Calls { get; private set; }
        public List<DateRange> Ranges { get; } = [];
        public Task<AccountingDataCacheEntry> LoadAsync(UserAccountContextAccount account, DateRange queryRange, AccountingDataCacheKey key, CancellationToken cancellationToken = default)
        {
            Calls++;
            Ranges.Add(queryRange);
            return Task.FromResult(new AccountingDataCacheEntry(key,
                rows.Where(row => queryRange.Contains(row.Date)).Select(row => new AccountingCacheRecord(row.Id, row.Date, row.Num, row.Amount, row.Account, row.Type, row.AccountingClass, row.AccountNumber)), DateTime.UtcNow));
        }
    }

    private sealed class EmptyDonationLoader : IDonationDataCacheLoader
    {
        public Task<DonationDataCacheEntry> LoadAsync(UserAccountContextAccount account, DateRange queryRange, DonationDataCacheKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DonationDataCacheEntry(key, [], [], DateTime.UtcNow));
    }

    private sealed class Facts(List<AccountingRecord> rows) : IAccountFactDataService
    {
        public List<DateRange> AccountingRanges { get; } = [];
        public Task<DonationCalculationData> GetDonationDataAsync(UserAccountContextAccount account, string userId, DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DonationCalculationData());
        public Task<List<AccountingRecord>> GetAccountingDataAsync(UserAccountContextAccount account, string userId, DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
        {
            var range = new DateRange(startDate, endDate);
            AccountingRanges.Add(range);
            return Task.FromResult(rows.Where(row => range.Contains(row.Date)).ToList());
        }
    }

    [Fact]
    public async Task WarmedInternSummary_IsReusedByDashboardWithAccountOverheadAndGeneration()
    {
        var now = DateTime.Now;
        var account = new Account { AccountId = 12, Fund = "Intern: Jane Doe", AccountKind = "Intern", InternSourceId = "Jane Doe", DisplayName = "Jane Doe", Overhead = 12m };
        var contextAccount = new UserAccountContextAccount { AccountId = 12, Fund = account.Fund, InternName = "Jane Doe", Overhead = 12m };
        var context = new DashboardContext(contextAccount);
        var cache = new HomeSummaryCache(NullLogger<HomeSummaryCache>.Instance);
        var versions = new CacheInvalidationVersion();
        var calculations = new AccountCalculationService(new ExpenseClassificationService(), null!, null!);
        var service = new FinancialDashboardService(NullLogger<FinancialDashboardService>.Instance,
            calculations, null!, null!, null!, null!, null!, context, null!, new AllowDashboardAccess(), null!, versions, cache, null!);
        var data = new CacheRebuildData
        {
            DonationsByInternName = new Dictionary<string, List<DonationRecord>>(StringComparer.Ordinal)
            {
                ["Jane Doe"] = [new DonationRecord { Date = now.Date, Amount = 100d, Fund = "Shared", InternName = "Jane Doe" }]
            }
        };

        await service.WarmInternSummaryFromDataAsync(account, data);
        var dashboard = await service.GetDashboardSummaryDataAsync("Jane Doe", "12");

        Assert.True(dashboard.HasAccountData);
        Assert.Equal(100m, dashboard.CurrentMonth.TotalDonations);
        Assert.Equal(12m, dashboard.CurrentMonth.TotalOverhead);
        Assert.Equal(1, cache.Count);
        versions.Invalidate(Cya2.Core.Interfaces.CacheDataDomains.Accounting);
        dashboard = await service.GetDashboardSummaryDataAsync("Jane Doe", "12");
        Assert.Equal(100m, dashboard.CurrentMonth.TotalDonations);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task GetOrCreateAsync_ReusesEntryForSameAccountGenerationAndPeriod()
    {
        var cache = new HomeSummaryCache(NullLogger<HomeSummaryCache>.Instance);
        var key = new HomeSummaryCacheKey(1, "Fund", 3, 2026, 2);
        var calls = 0;

        var first = await cache.GetOrCreateAsync(key, _ =>
        {
            calls++;
            return Task.FromResult(new HomeSummaryCacheEntry
            {
                CurrentMonth = new FinancialSummaryDto { TotalDonations = 10 }
            });
        });
        var second = await cache.GetOrCreateAsync(key, _ =>
        {
            calls++;
            return Task.FromResult(new HomeSummaryCacheEntry());
        });

        Assert.Same(first, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrCreateAsync_ReloadsAfterGenerationChanges()
    {
        var cache = new HomeSummaryCache(NullLogger<HomeSummaryCache>.Instance);
        var calls = 0;

        await cache.GetOrCreateAsync(new HomeSummaryCacheKey(1, "Fund", 3, 2026, 2), _ =>
        {
            calls++;
            return Task.FromResult(new HomeSummaryCacheEntry());
        });
        await cache.GetOrCreateAsync(new HomeSummaryCacheKey(1, "Fund", 4, 2026, 2), _ =>
        {
            calls++;
            return Task.FromResult(new HomeSummaryCacheEntry());
        });

        Assert.Equal(2, calls);
        Assert.Equal(2, cache.Count);
        cache.RemoveStaleGenerations(4, 4);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task RemoveStaleGenerations_PreservesCurrentInternEntriesSeparatelyFromNormalEntries()
    {
        var cache = new HomeSummaryCache(NullLogger<HomeSummaryCache>.Instance);
        var normal = new HomeSummaryCacheKey(1, "Fund", "", 7, 2026, 2);
        var intern = new HomeSummaryCacheKey(2, "", "Jane Doe", 3, 2026, 2);
        var staleIntern = new HomeSummaryCacheKey(3, "", "Other", 2, 2026, 2);
        foreach (var key in new[] { normal, intern, staleIntern })
            await cache.GetOrCreateAsync(key, _ => Task.FromResult(new HomeSummaryCacheEntry()));

        cache.RemoveStaleGenerations(7, 3);

        Assert.True(cache.TryGet(normal, out _));
        Assert.True(cache.TryGet(intern, out _));
        Assert.False(cache.TryGet(staleIntern, out _));
    }

    private sealed class DashboardContext(UserAccountContextAccount account) : IUserAccountContextService
    {
        public Task<UserAccountContext?> GetContextAsync(string userId, bool isAdminOrViewerHint = false) =>
            Task.FromResult<UserAccountContext?>(new UserAccountContext { IsIntern = !string.IsNullOrWhiteSpace(account.InternName), InternName = account.InternName, Accounts = [account] });
        public UserAccountContextAccount? ResolveSelectedAccount(UserAccountContext context, string? preferredIdentity) => account;
        public void Invalidate(string userId) { }
    }

    private sealed class AllowDashboardAccess : IUserAuthorizationService
    {
        public Task<UserAuthorizationResult> ValidateUserAsync(string userIdentifier) => Allowed();
        public Task<UserAuthorizationResult> ValidateAccountAccessAsync(string userIdentifier, int accountId) => Allowed();
        public Task<UserAuthorizationResult> ValidateAccountAccessAsync(string userIdentifier, string accountFund) => Allowed();
        private static Task<UserAuthorizationResult> Allowed() => Task.FromResult(new UserAuthorizationResult(true, true, false, false, 12, string.Empty));
    }
}
