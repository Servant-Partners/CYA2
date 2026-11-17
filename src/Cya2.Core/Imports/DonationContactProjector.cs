using System.Collections.Immutable;
using Cya2.Core.Services;

namespace Cya2.Core.Imports;

public sealed class DonationContactProjector
{
    public DonationContactProjection Project(string donorSourceId, IEnumerable<NormalizedDonation> survivingDonations,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(donorSourceId);
        ArgumentNullException.ThrowIfNull(survivingDonations);
        var eligible = new List<NormalizedDonation>();
        foreach (var donation in survivingDonations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!donation.IsAnonymous && !donation.IsSoftCredit && !donation.IsThirdPartySoftCredit
                && string.Equals(donation.CreditedDonorSourceId, donorSourceId, StringComparison.Ordinal))
                eligible.Add(donation);
        }

        var contacts = ImmutableArray.CreateBuilder<DonationContactObservation>();
        foreach (var kind in Enum.GetValues<DonationContactKind>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observations = eligible.Where(donation => donation.Contacts.Any(contact => contact.Kind == kind)).ToArray();
            if (observations.Length == 0) continue;
            var latest = observations.Max(donation => donation.Date);
            contacts.AddRange(observations.Where(donation => donation.Date == latest)
                .SelectMany(donation => donation.Contacts).Where(contact => contact.Kind == kind)
                .OrderBy(contact => contact.Value, StringComparer.Ordinal)
                .DistinctBy(contact => contact.NormalizedValue, StringComparer.Ordinal));
        }

        var addressDonations = eligible.Where(donation => donation.Addresses.Any(address => !address.IsBlank)).ToArray();
        var addresses = Array.Empty<DonationAddress>();
        if (addressDonations.Length > 0)
        {
            var latest = addressDonations.Max(donation => donation.Date);
            addresses = addressDonations.Where(donation => donation.Date == latest)
                .SelectMany(donation => donation.Addresses).Where(address => !address.IsBlank)
                .DistinctBy(address => (Key(address.Line1), Key(address.City), Key(address.State), Key(address.PostalCode), Key(address.Country)))
                .ToArray();
        }

        return new DonationContactProjection
        {
            Address = addresses.Length == 1 ? addresses[0] : null,
            HasAmbiguousAddress = addresses.Length > 1,
            Contacts = contacts.ToImmutable()
        };
    }

    private static string Key(string? value) => DonorIdentityResolver.NormalizeWhitespace(value).ToUpperInvariant();
}
