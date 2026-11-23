using Cya2.Core.Imports;

namespace Cya2.Core.Interfaces;

public sealed record DonationAccountMappingCounts(int UnresolvedNormalDonations, int UnresolvedInternDonations)
{
    public int UnresolvedDonations => UnresolvedNormalDonations + UnresolvedInternDonations;
}

public interface IDonationAccountMappingReader
{
    Task<DonationAccountMappingCounts> GetCountsAsync(IReadOnlyList<NormalizedDonation> donations, CancellationToken cancellationToken = default);
}
