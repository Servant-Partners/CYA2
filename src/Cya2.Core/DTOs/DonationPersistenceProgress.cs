namespace Cya2.Core.DTOs;

public sealed record DonationPersistenceProgress(string Phase, int? Completed = null, int? Total = null);
