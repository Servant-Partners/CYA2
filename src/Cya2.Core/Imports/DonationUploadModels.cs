using System.Collections.Immutable;

namespace Cya2.Core.Imports;

public sealed class DonationSourceRow
{
    public int RowNumber { get; init; }
    public DateTime? Date { get; init; }
    public decimal? Amount { get; init; }
    public string Fund { get; init; } = string.Empty;
    public string? GiftImportId { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public string? PrimaryAddressee { get; init; }
    public string? SoftCreditName { get; init; }
    public string? ConstituentId { get; init; }
    public string? SoftCreditRecipientId { get; init; }
    public string? InternSourceId { get; init; }
    public string? InternDisplayName { get; init; }
    public string PaymentMethod { get; init; } = string.Empty;
    public string GiftType { get; init; } = string.Empty;
    public bool IsAnonymous { get; init; }
    public DonationAddress? Address { get; init; }
    public string? Email { get; init; }
    public string? HomePhone { get; init; }
    public string? MobilePhone { get; init; }
}

public sealed record DonationAddress(string? Line1, string? City, string? State, string? PostalCode, string? Country)
{
    public bool IsBlank => string.IsNullOrWhiteSpace(Line1) && string.IsNullOrWhiteSpace(City)
        && string.IsNullOrWhiteSpace(State) && string.IsNullOrWhiteSpace(PostalCode) && string.IsNullOrWhiteSpace(Country);

    public override string ToString() => nameof(DonationAddress);
}

public enum DonationContactKind
{
    Email,
    HomePhone,
    MobilePhone
}

public sealed record DonationContactObservation(DonationContactKind Kind, string Value, string NormalizedValue)
{
    public override string ToString() => nameof(DonationContactObservation);
}

public sealed record NormalizedDonation
{
    public required ImmutableArray<int> SourceRows { get; init; }
    public required DateTime Date { get; init; }
    public required decimal Amount { get; init; }
    public required string Fund { get; init; }
    public string? GiftImportId { get; init; }
    public string? ConstituentId { get; init; }
    public string? SoftCreditRecipientId { get; init; }
    public string? CreditedDonorSourceId { get; init; }
    public string? InternSourceId { get; init; }
    public string? InternDisplayName { get; init; }
    public required string AccountName { get; init; }
    public string? PrimaryAddressee { get; init; }
    public string? SoftCreditName { get; init; }
    public required string DonorDisplayName { get; init; }
    public required string PaymentMethod { get; init; }
    public required string GiftType { get; init; }
    public bool IsAnonymous { get; init; }
    public bool IsSoftCredit { get; init; }
    public bool IsThirdPartySoftCredit { get; init; }
    public ImmutableArray<DonationAddress> Addresses { get; init; } = [];
    public ImmutableArray<DonationContactObservation> Contacts { get; init; } = [];

    public override string ToString() => nameof(NormalizedDonation);
}

public sealed record DonationUploadError(int? RowNumber, string Column, string Message);

public sealed class DonationUploadValidationResult
{
    public required ImmutableArray<NormalizedDonation> Donations { get; init; }
    public required ImmutableArray<DonationUploadError> Errors { get; init; }
    public int SourceRowCount { get; init; }
    public int DuplicateRowsMerged { get; init; }
    public bool IsValid => Errors.IsEmpty && !Donations.IsEmpty;
    public DateOnly? MinimumDate => Donations.IsEmpty ? null : DateOnly.FromDateTime(Donations.Min(row => row.Date));
    public DateOnly? MaximumDate => Donations.IsEmpty ? null : DateOnly.FromDateTime(Donations.Max(row => row.Date));
}

public sealed class DonationContactProjection
{
    public DonationAddress? Address { get; init; }
    public ImmutableArray<DonationContactObservation> Contacts { get; init; } = [];
    public bool HasAmbiguousAddress { get; init; }
}
