using System.Collections.Immutable;
using Cya2.Core.Services;

namespace Cya2.Core.Imports;

public sealed class DonationUploadNormalizer
{
    private readonly DonorIdentityResolver _softCreditResolver = new();

    public DonationUploadValidationResult Normalize(IEnumerable<DonationSourceRow> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        var errors = ImmutableArray.CreateBuilder<DonationUploadError>();
        var donations = new List<NormalizedDonation>();
        var duplicates = new Dictionary<AllocationKey, int>();
        var sourceCount = 0;
        var merged = 0;

        foreach (var row in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceCount++;
            var donation = NormalizeRow(row, errors);
            if (donation is null) continue;

            var candidate = new DonationDuplicateCandidate(donation.GiftImportId, donation.Fund, donation.Date,
                donation.Amount, donation.PaymentMethod, donation.GiftType,
                donation.CreditedDonorSourceId is { } id ? "id:" + id : "anonymous:unidentified");
            if (!DonationDuplicateClassifier.TryCreateKey(candidate, out var giftKey))
            {
                donations.Add(donation);
                continue;
            }

            var key = new AllocationKey(giftKey, donation.InternSourceId, donation.IsAnonymous,
                donation.IsSoftCredit, donation.ConstituentId, donation.SoftCreditRecipientId);
            if (!duplicates.TryGetValue(key, out var index))
            {
                duplicates.Add(key, donations.Count);
                donations.Add(donation);
                continue;
            }

            var existing = donations[index];
            donations[index] = existing with
            {
                SourceRows = existing.SourceRows.AddRange(donation.SourceRows),
                Contacts = existing.Contacts.Concat(donation.Contacts)
                    .DistinctBy(contact => (contact.Kind, contact.NormalizedValue)).ToImmutableArray(),
                Addresses = existing.Addresses.Concat(donation.Addresses).Distinct().ToImmutableArray()
            };
            merged++;
        }

        if (sourceCount == 0)
            errors.Add(new DonationUploadError(null, string.Empty, "No donation rows found. Existing data was not changed."));

        return new DonationUploadValidationResult
        {
            Donations = errors.Count == 0 ? donations.ToImmutableArray() : [],
            Errors = errors.ToImmutable(),
            SourceRowCount = sourceCount,
            DuplicateRowsMerged = merged
        };
    }

    private NormalizedDonation? NormalizeRow(DonationSourceRow row, ImmutableArray<DonationUploadError>.Builder errors)
    {
        var before = errors.Count;
        if (row.Date is null || row.Date.Value.Year < 1000) Invalid("Gift Date");
        var amount = row.Amount is { } sourceAmount ? decimal.Round(sourceAmount, 2, MidpointRounding.AwayFromZero) : (decimal?)null;
        if (amount is null || amount.Value <= -10000000000000000m || amount.Value >= 10000000000000000m) Invalid("Fund Split Amount");
        if (string.IsNullOrWhiteSpace(row.Fund)) Invalid("Fund Notes");

        var directId = Text(row.ConstituentId);
        var recipientId = Text(row.SoftCreditRecipientId);
        var internId = Text(row.InternSourceId);
        Limit(directId, 255, "Constituent ID");
        Limit(recipientId, 255, "Soft Credit Recipient ID");
        Limit(internId, 255, "Tribute System Record ID");
        Limit(row.Fund, 255, "Fund Notes");
        Limit(row.PaymentMethod, 255, "Gift Payment Type");
        Limit(row.GiftType, 255, "Gift Type");
        Limit(row.GiftImportId, 255, "Gift Import ID");
        Limit(row.InternDisplayName, 255, "Honor/Memorial Name");

        // Names are used only for the existing soft-credit heuristic, never to resolve donor identity.
        var input = new DonorIdentityInput(row.Fund, row.AccountName, row.SoftCreditName, row.PrimaryAddressee);
        var display = _softCreditResolver.Resolve(input);
        var thirdParty = _softCreditResolver.IsThirdPartySoftCredit(input);
        var softCredit = display.ResolutionSource == "SoftCredit";
        var creditedId = thirdParty ? recipientId : directId;
        if (!row.IsAnonymous && creditedId is null)
            Invalid(thirdParty ? "Soft Credit Recipient ID" : "Constituent ID");

        if (!row.IsAnonymous)
        {
            Limit(row.AccountName, 255, "Name");
            Limit(row.PrimaryAddressee, 255, "Primary Addressee");
            Limit(row.SoftCreditName, 255, "Soft Credit Recipient Name");
            Limit(display.DisplayName, 255, "Resolved donor name");
        }

        var contacts = ImmutableArray.CreateBuilder<DonationContactObservation>();
        var addresses = ImmutableArray<DonationAddress>.Empty;
        if (!row.IsAnonymous && !softCredit && !thirdParty)
        {
            AddContact(DonationContactKind.Email, row.Email, "Personal Email Number");
            AddContact(DonationContactKind.HomePhone, row.HomePhone, "Home Phone Number");
            AddContact(DonationContactKind.MobilePhone, row.MobilePhone, "Personal Mobile Phone Number");
            if (row.Address is { IsBlank: false } address)
            {
                var normalized = new DonationAddress(Text(address.Line1), Text(address.City), Text(address.State),
                    Text(address.PostalCode), Text(address.Country));
                Limit(normalized.Line1, 500, "Preferred Address Line 1");
                Limit(normalized.City, 500, "Preferred City");
                Limit(normalized.State, 500, "Preferred State");
                Limit(normalized.PostalCode, 500, "Preferred ZIP");
                Limit(normalized.Country, 500, "Preferred Country");
                addresses = [normalized];
            }
        }

        if (before != errors.Count) return null;
        return new NormalizedDonation
        {
            SourceRows = [row.RowNumber],
            Date = row.Date!.Value.Date,
            Amount = amount!.Value,
            Fund = row.Fund.Trim(),
            GiftImportId = Text(row.GiftImportId),
            CreditedDonorSourceId = creditedId,
            ConstituentId = row.IsAnonymous && thirdParty ? null : directId,
            SoftCreditRecipientId = row.IsAnonymous && !thirdParty ? null : recipientId,
            InternSourceId = internId,
            InternDisplayName = Text(row.InternDisplayName),
            AccountName = row.IsAnonymous ? string.Empty : row.AccountName.Trim(),
            PrimaryAddressee = row.IsAnonymous ? null : Text(row.PrimaryAddressee),
            SoftCreditName = row.IsAnonymous ? null : Text(row.SoftCreditName),
            DonorDisplayName = row.IsAnonymous ? "Anonymous" : display.DisplayName,
            PaymentMethod = row.PaymentMethod.Trim(),
            GiftType = row.GiftType.Trim(),
            IsAnonymous = row.IsAnonymous,
            IsSoftCredit = softCredit,
            IsThirdPartySoftCredit = thirdParty,
            Contacts = contacts.ToImmutable(),
            Addresses = addresses
        };

        void Invalid(string column) => errors.Add(new DonationUploadError(row.RowNumber, column, $"Row {row.RowNumber}: invalid or missing {column}."));
        void Limit(string? value, int maximum, string column)
        {
            if (value?.Length > maximum) Invalid(column);
        }
        void AddContact(DonationContactKind kind, string? value, string column)
        {
            var text = Text(value);
            if (text is null) return;
            var normalized = kind == DonationContactKind.Email
                ? DonorIdentityResolver.NormalizeWhitespace(text).ToUpperInvariant()
                : new string(text.Where(char.IsDigit).ToArray());
            Limit(text, 500, column);
            Limit(normalized, 500, column);
            if (normalized.Length == 0) { Invalid(column); return; }
            contacts.Add(new DonationContactObservation(kind, text, normalized));
        }
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record AllocationKey(DonationDuplicateKey Gift, string? InternSourceId, bool Anonymous,
        bool SoftCredit, string? ConstituentId, string? SoftCreditRecipientId);
}
