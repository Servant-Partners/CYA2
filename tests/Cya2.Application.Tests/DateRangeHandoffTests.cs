using Cya2.Application.DTOs;
using Cya2.Application.Interfaces;
using Cya2.Application.Services;
using Cya2.Core.Entities;
using Cya2.Core.ReadModels;
using Cya2.Core.Services;
using Cya2.Core.ValueObjects;
using Xunit;

namespace Cya2.Application.Tests;

public sealed class DateRangeHandoffTests
{
    [Fact]
    public void DonationPresentation_IncludesEveryTimeOnSelectedDates()
    {
        var date = new DateTime(2025, 6, 30);
        var rows = new List<DonationRowDto>
        {
            new() { Date = date.AddDays(-1).AddHours(23), Amount = 10 },
            new() { Date = date.AddHours(12), Amount = 20 },
            new() { Date = date.AddHours(23).AddMinutes(59), Amount = 30 },
            new() { Date = date.AddDays(1), Amount = 40 }
        };

        var selected = new DonationPresentationService().FilterByDateRange(rows, date, date);

        Assert.Equal(2, selected.Count);
        Assert.Equal(50d, selected.Sum(row => row.Amount));
    }

    [Fact]
    public void AccountingCalculation_IncludesEveryTimeOnStartAndEndDays()
    {
        var start = new DateTime(2025, 6, 1);
        var end = new DateTime(2025, 6, 30);
        var account = new UserAccountContextAccount { AccountingClass = "Class", AccountNumber = "100" };
        var rows = new[]
        {
            new AccountingRecord { AccountingClass = "Class", Date = start.AddDays(-1).AddHours(23), Type = "Expense", Amount = 100 },
            new AccountingRecord { AccountingClass = "Class", Date = start.AddHours(9), Type = "Expense", Amount = 10 },
            new AccountingRecord { AccountingClass = "Class", Date = end.AddHours(23), Type = "Expense", Amount = 20 },
            new AccountingRecord { AccountingClass = "Class", Date = end.AddDays(1), Type = "Expense", Amount = 100 }
        };

        var result = new AccountingTransactionProcessor(new ExpenseClassificationService()).Process(account, rows, start, end);

        Assert.Equal(30m, result.ExpenseTotal);
        Assert.Equal(-30m, result.TotalBalance);
        Assert.Equal(DateTime.MaxValue, DateRange.EndOfDay(DateTime.MaxValue));
        Assert.Equal(end.AddDays(1).AddTicks(-1), DateRange.EndOfDay(end));
    }
}
