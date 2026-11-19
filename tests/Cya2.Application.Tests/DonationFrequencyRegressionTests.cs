using Cya2.Core.Enums;
using Cya2.Core.Services;
using Xunit;

namespace Cya2.Application.Tests;

public sealed class DonationFrequencyRegressionTests
{
    private readonly DonorFrequencyService _service = new();

    [Fact]
    public void EmptyAndSingleGiftHaveNoInventedPattern()
    {
        Assert.Equal(DonorFrequency.None, _service.ClassifyDonor([]));
        Assert.Empty(_service.ClassifySequence([]));
        Assert.Equal([DonorFrequency.OneTime], _service.ClassifySequence([Gift(0)]));
    }

    [Theory]
    [InlineData(0, DonorFrequency.Sporadic)]
    [InlineData(1, DonorFrequency.Monthly)]
    [InlineData(2, DonorFrequency.Quarterly)]
    [InlineData(4, DonorFrequency.Quarterly)]
    [InlineData(5, DonorFrequency.Sporadic)]
    [InlineData(11, DonorFrequency.Yearly)]
    [InlineData(13, DonorFrequency.Yearly)]
    [InlineData(14, DonorFrequency.Sporadic)]
    [InlineData(36, DonorFrequency.Sporadic)]
    public void TwoGiftPatternAppliesToBothOccurrences(int gap, DonorFrequency expected)
    {
        Assert.Equal([expected, expected], _service.ClassifySequence([Gift(0), Gift(gap)]));
    }

    [Fact]
    public void AnnualHistoryKeepsItsPatternExceptForTheNewMonthlyBoundary()
    {
        DonorGiftRecord[] gifts = [Gift(0), Gift(12), Gift(24), Gift(36), Gift(37), Gift(38)];
        Assert.Equal([DonorFrequency.Yearly, DonorFrequency.Yearly, DonorFrequency.Yearly,
            DonorFrequency.Monthly, DonorFrequency.Monthly, DonorFrequency.Monthly], _service.ClassifySequence(gifts));
        Assert.Equal(DonorFrequency.Monthly, _service.ClassifyDonor(gifts.Reverse().ToArray()));
        for (var index = 0; index < gifts.Length; index++)
            Assert.Equal(_service.ClassifySequence(gifts)[index], _service.ClassifyGift(index, gifts).Frequency);
    }

    [Fact]
    public void SameTimestampGiftsAreDistinctAndDetachedTargetsCannotDuplicateHistory()
    {
        DonorGiftRecord[] gifts = [Gift(0), Gift(0)];
        Assert.Equal(DonorFrequency.Sporadic, _service.ClassifyGift(gifts[0], gifts).Frequency);
        Assert.Equal(DonorFrequency.Sporadic, _service.ClassifyGift(1, gifts).Frequency);
        Assert.Throws<ArgumentException>(() => _service.ClassifyGift(Gift(0), gifts));
        Assert.Throws<ArgumentOutOfRangeException>(() => _service.ClassifyGift(2, gifts));
    }

    [Fact]
    public void EstablishedMonthlyPatternSurvivesEarlyLateAndSplitGifts()
    {
        DonorGiftRecord[] gifts = [Gift(0), Gift(1), Gift(2, 50), Gift(2, 50), Gift(3), Gift(5, 50), Gift(5, 150)];
        Assert.All(_service.ClassifySequence(gifts), frequency => Assert.Equal(DonorFrequency.Monthly, frequency));
    }

    [Theory]
    [InlineData(100, DonorFrequency.Quarterly)]
    [InlineData(200, DonorFrequency.Monthly)]
    public void CatchupUsesMonthlyTotalsRatherThanIndividualSplitAmounts(decimal payment, DonorFrequency expected)
    {
        // Three missed months out of eight exceed 25%; a double gift absorbs one.
        DonorGiftRecord[] gifts = [Gift(0, 50), Gift(0, 50), Gift(1), Gift(2), Gift(4), Gift(7, payment)];
        Assert.Equal(expected, _service.ClassifyDonor(gifts));
    }

    [Fact]
    public void PrepaymentCanMaintainAnEstablishedMonthlyPattern()
    {
        DonorGiftRecord[] gifts = [Gift(0), Gift(1, 200), Gift(4)];
        Assert.Equal(DonorFrequency.Monthly, _service.ClassifyDonor(gifts));
    }

    [Fact]
    public void ProcessingAcrossMonthAndYearEndsDoesNotCollapseSeparateGifts()
    {
        DonorGiftRecord[] gifts =
        [
            new() { Date = new DateTime(2025, 11, 30), Amount = 100 },
            new() { Date = new DateTime(2025, 12, 31), Amount = 100 },
            new() { Date = new DateTime(2026, 2, 1), Amount = 100 },
            new() { Date = new DateTime(2026, 2, 28), Amount = 100 }
        ];
        Assert.All(_service.ClassifySequence(gifts), frequency => Assert.Equal(DonorFrequency.Monthly, frequency));
    }

    [Fact]
    public void DeletingAllButOneGiftRestoresOneTime()
    {
        DonorGiftRecord[] original = [Gift(0), Gift(1), Gift(2)];
        Assert.All(_service.ClassifySequence(original), frequency => Assert.Equal(DonorFrequency.Monthly, frequency));
        Assert.Equal([DonorFrequency.OneTime], _service.ClassifySequence(original[..1]));
    }

    [Fact]
    public void ZeroAndNegativeMonthlyTotalsDoNotInventCatchupCoverage()
    {
        DonorGiftRecord[] gifts = [Gift(0, 0), Gift(1, -100), Gift(4, 0)];
        Assert.Equal(DonorFrequency.Quarterly, _service.ClassifyDonor(gifts));
    }

    [Theory]
    [InlineData(3, DonorFrequency.Monthly)]
    [InlineData(4, DonorFrequency.Quarterly)]
    public void MonthlyPersistenceIncludesExactlyTwentyFivePercentMissed(int lastMonth, DonorFrequency expected)
    {
        Assert.Equal(expected, _service.ClassifyDonor([Gift(0), Gift(1), Gift(lastMonth)]));
    }

    [Fact]
    public void DeletingOrReplacingThePatternBoundaryRevisesOnlyParticipatingGifts()
    {
        DonorGiftRecord[] original = [Gift(0), Gift(12), Gift(24), Gift(36), Gift(37)];
        Assert.Equal(DonorFrequency.Monthly, _service.ClassifySequence(original)[3]);
        Assert.All(_service.ClassifySequence(original[..^1]), frequency => Assert.Equal(DonorFrequency.Yearly, frequency));
        Assert.Equal([DonorFrequency.Yearly, DonorFrequency.Yearly, DonorFrequency.Yearly,
            DonorFrequency.Sporadic], _service.ClassifySequence([Gift(0), Gift(12), Gift(24), Gift(60)]));
        Assert.Equal([DonorFrequency.Sporadic, DonorFrequency.Sporadic], _service.ClassifySequence([Gift(0), Gift(60)]));
    }

    [Fact]
    public void SequenceRejectsDescendingHistory()
    {
        Assert.Throws<ArgumentException>(() => _service.ClassifySequence([Gift(1), Gift(0)]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(500)]
    public void PagedCheckpointReplayMatchesCompleteReplayIncludingCorrections(int pageSize)
    {
        var random = new Random(7301);
        for (var sample = 0; sample < 100; sample++)
        {
            var month = 0;
            var gifts = new List<DonorGiftRecord>();
            for (var index = 0; index < 150; index++)
            {
                month += random.Next(0, 5);
                gifts.Add(Gift(month, random.Next(1, 5) * 25));
            }
            var changedFrom = gifts[100].Date;
            // Replace/delete a suffix, including same-date rows; untouched prefix supplies any checkpoint.
            gifts.RemoveAll(gift => gift.Date >= changedFrom);
            gifts.AddRange([new() { Date = changedFrom, Amount = 75 },
                new() { Date = changedFrom, Amount = 25 },
                new() { Date = changedFrom.AddMonths(1), Amount = 100 },
                new() { Date = changedFrom.AddMonths(14), Amount = 100 }]);
            var full = _service.ClassifySequence(gifts);
            var anchor = FindAnchor(gifts, changedFrom);
            var contextStart = anchor < 0 ? 0 : gifts.FindIndex(gift => gift.Date >= gifts[anchor].Date.AddMonths(-12));
            var replay = ReplayPages(gifts.Skip(contextStart).ToArray(), pageSize);
            var outputStart = Math.Max(0, anchor);
            Assert.Equal(full.Skip(outputStart), replay.Skip(outputStart - contextStart));
        }
    }

    [Fact]
    public void TwelveMonthContextBeforeCheckpointIsNecessaryForMonthlyPersistence()
    {
        var gifts = Enumerable.Range(0, 12).Select(month => Gift(month)).ToList();
        gifts.Add(Gift(15));
        var full = _service.ClassifySequence(gifts);
        Assert.Equal(DonorFrequency.Monthly, full[^1]);
        Assert.Equal(DonorFrequency.Quarterly, _service.ClassifyDonor(gifts.Skip(10).ToArray()));
        Assert.Equal(full, ReplayPages(gifts.ToArray(), 2));
    }

    private static int FindAnchor(List<DonorGiftRecord> gifts, DateTime changedFrom)
    {
        foreach (var months in new[] { 4, 13, 26 })
        {
            var start = gifts.FindIndex(gift => gift.Date >= changedFrom.AddMonths(-months));
            var anchor = -1;
            for (var index = Math.Max(1, start + 1); index < gifts.Count && gifts[index].Date < changedFrom; index++)
            {
                var previous = gifts[index - 1].Date;
                var current = gifts[index].Date;
                if ((current.Year - previous.Year) * 12 + current.Month - previous.Month == 1) anchor = index;
            }
            if (anchor >= 0) return anchor;
        }
        return -1;
    }

    private static DonorFrequency[] ReplayPages(DonorGiftRecord[] gifts, int pageSize)
    {
        var state = new DonorFrequencyService.FrequencySequence();
        var result = new List<DonorFrequency>();
        var pending = false;
        foreach (var page in gifts.Chunk(pageSize))
        foreach (var gift in page)
        {
            state.Add(gift);
            if (pending) result.Add(state.Previous);
            pending = true;
        }
        if (pending) result.Add(state.Current);
        return result.ToArray();
    }

    private static DonorGiftRecord Gift(int month, decimal amount = 100) => new()
    {
        Date = new DateTime(2000, 1, 15).AddMonths(month), Amount = amount
    };
}
