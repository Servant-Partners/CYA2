using Cya2.Core.Enums;

namespace Cya2.Core.Services;

/// <summary>
/// Represents a single donation used for frequency classification.
/// Kept minimal so it can be populated from cache or DB without coupling.
/// </summary>
public sealed class DonorGiftRecord
{
    public DateTime Date { get; init; }
    public decimal Amount { get; init; }
}

/// <summary>
/// Result of classifying a single past gift.
/// Designed so it can also be persisted to the DB model in a future pipeline.
/// </summary>
public sealed class GiftClassificationResult
{
    public DonorFrequency Frequency { get; init; }
    public bool IsCatchUp { get; init; }
    public int CatchUpMonthsCovered { get; init; }
}

/// <summary>
/// Pure domain service for classifying donor giving frequency.
/// No infrastructure or framework dependencies.
/// </summary>
public class DonorFrequencyService
{
    // An established Monthly pattern allows up to 25% missed months.
    private const double MissedMonthTolerancePercent = 0.25;

    // Gifts are considered yearly if between 11 and 13 months apart.
    private const int YearlyMinMonths = 11;
    private const int YearlyMaxMonths = 13;

    // Gifts are considered quarterly if between 2 and 4 months apart.
    private const int QuarterlyMinMonths = 2;
    private const int QuarterlyMaxMonths = 4;

    // Evaluation window for Monthly classification.
    private const int MonthlyLookbackMonths = 12;

    /// <summary>
    /// Determines the current frequency classification for a donor.
    /// Uses cache-first: only the history provided is evaluated.
    /// Caller supplies one donor/fund's complete history, or replay context with a proven pattern boundary.
    /// A recent window alone cannot establish the preceding pattern state.
    /// </summary>
    public DonorFrequency ClassifyDonor(IReadOnlyList<DonorGiftRecord> history)
    {
        if (history == null || history.Count == 0)
            return DonorFrequency.None;

        var state = new FrequencySequence();
        foreach (var gift in history.OrderBy(g => g.Date)) state.Add(gift);
        return state.Current;
    }

    /// <summary>
    /// Classifies a single past gift in context of the donor's surrounding history.
    /// Designed to also be usable from a future DB-write pipeline (import/update).
    /// historyAroundGift includes this exact gift occurrence and surrounding donations.
    /// Use the indexed overload when object identity is not available; never infer identity from date/amount.
    /// </summary>
    public GiftClassificationResult ClassifyGift(DonorGiftRecord gift, IReadOnlyList<DonorGiftRecord> historyAroundGift)
    {
        if (gift == null)
            return new GiftClassificationResult { Frequency = DonorFrequency.None };

        var history = historyAroundGift ?? Array.Empty<DonorGiftRecord>();
        for (var index = 0; index < history.Count; index++)
            if (ReferenceEquals(history[index], gift)) return ClassifyGift(index, history);
        throw new ArgumentException("The gift must be an occurrence in history. Use the indexed overload to identify a gift explicitly.", nameof(gift));
    }

    /// <summary>Identifies a specific occurrence without collapsing same-date or split gifts.</summary>
    public GiftClassificationResult ClassifyGift(int giftIndex, IReadOnlyList<DonorGiftRecord> historyAroundGift)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(giftIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(giftIndex, historyAroundGift.Count);
        var sorted = Enumerable.Range(0, historyAroundGift.Count).OrderBy(index => historyAroundGift[index].Date).ToArray();
        var frequencies = ClassifySequence(sorted.Select(index => historyAroundGift[index]).ToArray());
        var donorStatus = frequencies[Array.IndexOf(sorted, giftIndex)];
        var gift = historyAroundGift[giftIndex];

        // Check catch-up only for monthly donors
        if (donorStatus == DonorFrequency.Monthly)
        {
            var catchUp = DetectCatchUp(gift, historyAroundGift ?? Array.Empty<DonorGiftRecord>());
            if (catchUp.IsCatchUp)
            {
                return new GiftClassificationResult
                {
                    Frequency = DonorFrequency.Monthly,
                    IsCatchUp = true,
                    CatchUpMonthsCovered = catchUp.CatchUpMonthsCovered
                };
            }
        }

        return new GiftClassificationResult { Frequency = donorStatus };
    }

    /// <summary>Classifies chronological gifts, revising only the preceding pattern boundary.</summary>
    public DonorFrequency[] ClassifySequence(IReadOnlyList<DonorGiftRecord> sorted)
    {
        var result = new DonorFrequency[sorted.Count];
        var state = new FrequencySequence();
        for (var index = 0; index < sorted.Count; index++)
        {
            result[index] = state.Add(sorted[index]);
            if (index > 0) result[index - 1] = state.Previous;
        }
        return result;
    }

    /// <summary>Bounded rolling context for one chronological donor/fund sequence.</summary>
    public sealed class FrequencySequence
    {
        private readonly Queue<DonorGiftRecord> _window = new();
        private readonly SortedDictionary<int, decimal> _months = [];
        private DonorGiftRecord? _last;
        public DonorFrequency Current { get; private set; } = DonorFrequency.None;
        public DonorFrequency Previous { get; private set; } = DonorFrequency.None;

        public DonorFrequency Add(DonorGiftRecord gift)
        {
            if (_last is not null && gift.Date < _last.Date)
                throw new ArgumentException("Gifts must be chronological.", nameof(gift));
            var start = gift.Date.AddMonths(-MonthlyLookbackMonths);
            while (_window.TryPeek(out var expired) && expired.Date < start)
            {
                _window.Dequeue();
                var key = MonthKey(expired.Date);
                _months[key] -= expired.Amount;
                if (!_window.TryPeek(out var next) || MonthKey(next.Date) != key) _months.Remove(key);
            }
            _window.Enqueue(gift);
            var month = MonthKey(gift.Date);
            _months[month] = _months.GetValueOrDefault(month) + gift.Amount;
            Previous = Current;
            if (_last is null) Current = DonorFrequency.OneTime;
            else
            {
                var months = MonthsBetween(_last.Date, gift.Date);
                Current = Current == DonorFrequency.Monthly && IsMonthly(_months)
                    ? DonorFrequency.Monthly
                    : months == 1 ? DonorFrequency.Monthly
                    : months is >= QuarterlyMinMonths and <= QuarterlyMaxMonths ? DonorFrequency.Quarterly
                    : months is >= YearlyMinMonths and <= YearlyMaxMonths ? DonorFrequency.Yearly
                    : DonorFrequency.Sporadic;
                if (Current != DonorFrequency.Sporadic || Previous == DonorFrequency.OneTime)
                    Previous = Current;
            }
            _last = gift;
            return Current;
        }
    }

    /// <summary>
    /// Detects whether a gift is a catch-up payment covering missed months.
    /// Uses Option B: divide gift amount by average monthly amount and round to nearest integer.
    /// </summary>
    public (bool IsCatchUp, int CatchUpMonthsCovered) DetectCatchUp(
        DonorGiftRecord gift,
        IReadOnlyList<DonorGiftRecord> history)
    {
        if (gift == null || history == null || history.Count < 2)
            return (false, 1);

        var priorGifts = history
            .Where(g => g.Date < gift.Date)
            .OrderBy(g => g.Date)
            .ToList();

        if (priorGifts.Count == 0)
            return (false, 1);

        var averageMonthlyAmount = priorGifts.Average(g => g.Amount);
        if (averageMonthlyAmount <= 0)
            return (false, 1);

        var monthsCovered = (int)Math.Round((double)(gift.Amount / averageMonthlyAmount));
        monthsCovered = Math.Max(1, monthsCovered);

        return (monthsCovered > 1, monthsCovered);
    }

    public (bool IsPrePayment, int MonthsCovered) DetectPrePayment(DonorGiftRecord gift, IReadOnlyList<DonorGiftRecord> history) { if (gift == null || history == null || history.Count < 2) return (false, 1); var sub = history.Where(g => g.Date > gift.Date).OrderBy(g => g.Date).ToList(); var refs = sub.Count > 0 ? sub : history.Where(g => g.Date < gift.Date).OrderBy(g => g.Date).ToList(); if (!refs.Any()) return (false, 1); var avg = refs.Average(g => g.Amount); if (avg <= 0) return (false, 1); var mc = (int)Math.Round((double)(gift.Amount / avg)); mc = Math.Max(1, mc); return (mc > 1, mc); }

    // ── Private classification helpers ────────────────────────────────────────

    private static bool IsMonthly(SortedDictionary<int, decimal> months)
    {
        if (months.Count < 2) return false;
        // At most thirteen calendar months intersect the date-relative twelve-month window.
        Span<int> keys = stackalloc int[MonthlyLookbackMonths + 1];
        Span<decimal> amounts = stackalloc decimal[MonthlyLookbackMonths + 1];
        var count = 0;
        decimal total = 0;
        foreach (var (key, amount) in months)
        {
            keys[count] = key;
            amounts[count++] = amount;
            total += amount;
        }
        var absorbed = 0;
        decimal priorTotal = 0;
        for (var index = 0; index < count - 1; index++)
        {
            priorTotal += amounts[index];
            var gap = keys[index + 1] - keys[index] - 1;
            if (gap <= 0) continue;
            var priorAverage = priorTotal / (index + 1);
            var laterAverage = (total - priorTotal) / (count - index - 1);
            absorbed += Math.Max(Coverage(amounts[index + 1], priorAverage, gap),
                Coverage(amounts[index], laterAverage, gap));
        }
        var span = keys[count - 1] - keys[0] + 1;
        return (double)Math.Max(0, span - count - absorbed) / span <= MissedMonthTolerancePercent;
    }

    private static int Coverage(decimal amount, decimal average, int gap) => average <= 0 ? 0
        : (int)Math.Clamp(Math.Round(amount / average) - 1, 0m, gap);

    private static int MonthKey(DateTime date) => date.Year * 12 + date.Month;

    private static int MonthsBetween(DateTime from, DateTime to)
    {
        return Math.Abs(((to.Year - from.Year) * 12) + to.Month - from.Month);
    }
}
