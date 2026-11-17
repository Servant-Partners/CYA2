namespace Cya2.Core.Imports;

public static class DonationReplacementPolicy
{
    public const int OldDateConfirmationDays = 380;

    public static bool RequiresOldDateConfirmation(DateOnly minimumDonationDate, DateOnly today) =>
        today.DayNumber - minimumDonationDate.DayNumber > OldDateConfirmationDays;

    public static bool Replaces(DateTime existingDonationDate, DateOnly minimumDonationDate) =>
        DateOnly.FromDateTime(existingDonationDate) >= minimumDonationDate;
}
