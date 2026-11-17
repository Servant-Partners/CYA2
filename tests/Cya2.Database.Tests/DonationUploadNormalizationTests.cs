using Cya2.Core.Imports;
using Xunit;

namespace Cya2.Database.Tests;

public sealed class DonationUploadNormalizationTests
{
    private readonly DonationUploadNormalizer _normalizer = new();

    [Fact]
    public void OpaqueIdsPreserveZerosAndCaseWithoutNameFallback()
    {
        var result = _normalizer.Normalize([
            Row(id: "000abc"), Row(id: "000ABC"), Row(id: "abc")
        ]);
        Assert.True(result.IsValid);
        Assert.Equal(3, result.Donations.Length);
        Assert.Equal(new[] { "000abc", "000ABC", "abc" }, result.Donations.Select(row => row.CreditedDonorSourceId));
    }

    [Fact]
    public void MissingDirectIdRejectsEntireUploadWithoutExposingValues()
    {
        var result = _normalizer.Normalize([Row(), Row(id: null)]);
        Assert.False(result.IsValid);
        Assert.Empty(result.Donations);
        Assert.Contains(result.Errors, error => error.Column == "Constituent ID");
        Assert.DoesNotContain("Synthetic Person", string.Join(' ', result.Errors));
    }

    [Fact]
    public void ThirdPartyUsesRecipientIdAndDoesNotPersistIntermediaryContacts()
    {
        var result = _normalizer.Normalize([Row(thirdParty: true, recipientId: "000recipient")]);
        var gift = Assert.Single(result.Donations);
        Assert.True(gift.IsThirdPartySoftCredit);
        Assert.Equal("000recipient", gift.CreditedDonorSourceId);
        Assert.Empty(gift.Contacts);
        Assert.Empty(gift.Addresses);
    }

    [Fact]
    public void ThirdPartyDoesNotFallBackToIntermediaryId()
    {
        var result = _normalizer.Normalize([Row(thirdParty: true)]);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Column == "Soft Credit Recipient ID");
    }

    [Theory]
    [InlineData(false, "000direct", null, "000direct")]
    [InlineData(true, "000intermediary", "000recipient", "000recipient")]
    [InlineData(false, null, null, null)]
    public void AnonymousRetainsOnlyApplicableIdAndInternAssociation(bool thirdParty, string? id, string? recipientId, string? expected)
    {
        var result = _normalizer.Normalize([Row(id: id, thirdParty: thirdParty, recipientId: recipientId, anonymous: true, intern: "000intern")]);
        var gift = Assert.Single(result.Donations);
        Assert.Equal(expected, gift.CreditedDonorSourceId);
        Assert.Equal(thirdParty ? null : expected, gift.ConstituentId);
        Assert.Equal(thirdParty ? expected : null, gift.SoftCreditRecipientId);
        Assert.Equal("Anonymous", gift.DonorDisplayName);
        Assert.Empty(gift.AccountName);
        Assert.Null(gift.PrimaryAddressee);
        Assert.Null(gift.SoftCreditName);
        Assert.Empty(gift.Contacts);
        Assert.Empty(gift.Addresses);
        Assert.Equal("000intern", gift.InternSourceId);
        Assert.Equal("NormalizedDonation", gift.ToString());
    }

    [Fact]
    public void DuplicateContactRowsDoNotDuplicateMoney()
    {
        var result = _normalizer.Normalize([Row(email: "one@example.invalid"), Row(email: "two@example.invalid")]);
        var gift = Assert.Single(result.Donations);
        Assert.Equal(25.50m, gift.Amount);
        Assert.Equal(2, gift.Contacts.Count(contact => contact.Kind == DonationContactKind.Email));
        Assert.Equal(1, result.DuplicateRowsMerged);
        Assert.Equal(2, gift.SourceRows.Length);
    }

    [Fact]
    public void DifferentInternRecipientsNeverCollapse()
    {
        var result = _normalizer.Normalize([Row(intern: "01"), Row(intern: "02"), Row(intern: null)]);
        Assert.Equal(3, result.Donations.Length);
        Assert.Equal(0, result.DuplicateRowsMerged);
    }

    [Fact]
    public void BlankGiftIdsNeverCollapse()
    {
        var result = _normalizer.Normalize([Row(giftId: null), Row(giftId: null)]);
        Assert.Equal(2, result.Donations.Length);
    }

    [Fact]
    public void InvalidAmountsRejectWholeUploadIncludingExtremeDecimal()
    {
        var result = _normalizer.Normalize([Row(), Row(amount: decimal.MinValue)]);
        Assert.False(result.IsValid);
        Assert.Empty(result.Donations);
        Assert.Contains(result.Errors, error => error.Column == "Fund Split Amount");
    }

    [Theory]
    [InlineData("9999999999999999.995")]
    [InlineData("-9999999999999999.995")]
    public void AmountRoundingCannotOverflowDatabasePrecision(string text)
    {
        var amount = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(_normalizer.Normalize([Row(amount: amount)]).IsValid);
    }

    [Fact]
    public void DatesAreFoundAcrossUnsortedRowsAndMoneyUsesExistingRounding()
    {
        var result = _normalizer.Normalize([
            Row(date: new DateTime(2025, 12, 1), amount: 25.505m),
            Row(date: new DateTime(2025, 1, 1), amount: -25.505m)
        ]);
        Assert.Equal(new DateOnly(2025, 1, 1), result.MinimumDate);
        Assert.Equal(new DateOnly(2025, 12, 1), result.MaximumDate);
        Assert.Equal(25.51m, result.Donations[0].Amount);
        Assert.Equal(-25.51m, result.Donations[1].Amount);
    }

    [Fact]
    public void EmptyInputIsRejectedAndCancellationIsObserved()
    {
        Assert.False(_normalizer.Normalize([]).IsValid);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => _normalizer.Normalize([Row()], cancellation.Token));
    }

    [Fact]
    public void ContactProjectionFallsBackAfterLatestSupplyingDonationIsRemoved()
    {
        var original = Assert.Single(_normalizer.Normalize([Row(date: new DateTime(2024, 1, 1), email: "old@example.invalid", street: "Old Street")]).Donations);
        var recent = Assert.Single(_normalizer.Normalize([Row(date: new DateTime(2025, 1, 1), email: "new@example.invalid", street: "New Street")]).Donations);
        var projector = new DonationContactProjector();
        Assert.Equal("new@example.invalid", Email(projector.Project("000direct", [original, recent])));
        var fallback = projector.Project("000direct", [original]);
        Assert.Equal("old@example.invalid", Email(fallback));
        Assert.Equal("Old Street", fallback.Address?.Line1);
        Assert.Empty(projector.Project("000direct", []).Contacts);
    }

    [Fact]
    public void BlankLaterContactsDoNotClearEarlierEvidence()
    {
        var original = Assert.Single(_normalizer.Normalize([Row(date: new DateTime(2024, 1, 1))]).Donations);
        var later = Assert.Single(_normalizer.Normalize([Row(date: new DateTime(2025, 1, 1), email: null, street: null)]).Donations);
        var projection = new DonationContactProjector().Project("000direct", [original, later]);
        Assert.Equal("person@example.invalid", Email(projection));
        Assert.Equal("Test Street", projection.Address?.Line1);
    }

    [Fact]
    public void ProjectionDefensivelyIgnoresAnonymousAndSoftCreditEvidence()
    {
        var original = Assert.Single(_normalizer.Normalize([Row()]).Donations);
        var hostile = original with { Date = original.Date.AddYears(1), IsAnonymous = true };
        var soft = hostile with { IsAnonymous = false, IsSoftCredit = true };
        var projector = new DonationContactProjector();
        Assert.Empty(projector.Project("000direct", [hostile, soft]).Contacts);
        Assert.Null(projector.Project("000direct", [hostile, soft]).Address);
        Assert.Empty(projector.Project("000DIRECT", [original]).Contacts);
    }

    [Fact]
    public void ConflictingSameDayAddressesAreNotCombinedOrArbitrarilySelected()
    {
        var first = Assert.Single(_normalizer.Normalize([Row(street: "First Street")]).Donations);
        var second = Assert.Single(_normalizer.Normalize([Row(street: "Second Street")]).Donations);
        var projection = new DonationContactProjector().Project("000direct", [first, second]);
        Assert.True(projection.HasAmbiguousAddress);
        Assert.Null(projection.Address);
    }

    [Theory]
    [InlineData(379, false)]
    [InlineData(380, false)]
    [InlineData(381, true)]
    public void ConfirmationUsesStrict380DayBoundary(int days, bool expected)
    {
        var today = new DateOnly(2026, 6, 1);
        Assert.Equal(expected, DonationReplacementPolicy.RequiresOldDateConfirmation(today.AddDays(-days), today));
    }

    [Fact]
    public void ReplacementHasNoUpperDateBoundary()
    {
        var boundary = new DateOnly(2025, 1, 1);
        Assert.False(DonationReplacementPolicy.Replaces(new DateTime(2024, 12, 31, 23, 59, 59), boundary));
        Assert.True(DonationReplacementPolicy.Replaces(new DateTime(2025, 1, 1), boundary));
        Assert.True(DonationReplacementPolicy.Replaces(new DateTime(2030, 1, 1), boundary));
    }

    private static string? Email(DonationContactProjection projection) =>
        projection.Contacts.SingleOrDefault(contact => contact.Kind == DonationContactKind.Email)?.Value;

    private static DonationSourceRow Row(string? id = "000direct", string? recipientId = null, bool thirdParty = false,
        bool anonymous = false, string? intern = null, string? giftId = "gift-001", decimal amount = 25.50m,
        DateTime? date = null, string? email = "person@example.invalid", string? street = "Test Street") => new()
    {
        RowNumber = 2,
        Date = date ?? new DateTime(2025, 1, 1),
        Amount = amount,
        Fund = "Synthetic Fund",
        GiftImportId = giftId,
        AccountName = thirdParty ? "Synthetic Intermediary" : "Synthetic Person",
        PrimaryAddressee = thirdParty ? "Synthetic Intermediary" : "Synthetic Person",
        SoftCreditName = thirdParty ? "Synthetic Person" : null,
        ConstituentId = id,
        SoftCreditRecipientId = recipientId,
        InternSourceId = intern,
        InternDisplayName = intern is null ? null : "Synthetic Intern",
        PaymentMethod = "Check",
        GiftType = "Cash",
        IsAnonymous = anonymous,
        Address = street is null ? null : new DonationAddress(street, "Test City", "TS", "00000", "US"),
        Email = email
    };
}
