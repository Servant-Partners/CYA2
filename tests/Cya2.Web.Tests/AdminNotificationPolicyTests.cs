using System.Globalization;
using Cya2.Application.Services;
using cya2.Components.Shared.LanguageResources;
using cya2.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Radzen;
using Xunit;

namespace Cya2.Web.Tests;

public sealed class AdminNotificationPolicyTests
{
    [Theory]
    [InlineData("Missing column: Tribute System Record ID")]
    [InlineData("Row 12: Gift Date is invalid.")]
    [InlineData("The worksheet exceeds the configured donation import complexity limits.")]
    public void ValidationFailure_PreservesActualReason(string reason)
    {
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<Resource>>();
        Assert.Equal(reason, AdminNotificationPolicy.UploadErrorDetail(new ImportUploadValidationException(reason), localizer));
    }

    [Fact]
    public void CapacityFailure_PreservesActionableReason()
    {
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<Resource>>();
        const string reason = "Please try again after an existing preview is imported or expires.";
        Assert.Equal(reason, AdminNotificationPolicy.UploadErrorDetail(new ImportUploadCapacityException(reason), localizer));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("es-US")]
    public void UnexpectedFailure_HidesExceptionDetailsAndUsesLocalizedFeedback(string culture)
    {
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var localizer = services.GetRequiredService<IStringLocalizer<Resource>>();
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.False(localizer["UploadValidationFailed"].ResourceNotFound);
            Assert.False(localizer["UploadFileTooLarge", 5000].ResourceNotFound);
            var detail = AdminNotificationPolicy.UploadErrorDetail(new IOException("sensitive internal details"), localizer);
            Assert.Equal(localizer["UploadValidationFailed"].Value, detail);
            Assert.DoesNotContain("sensitive", detail);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Theory]
    [InlineData(NotificationSeverity.Error)]
    [InlineData(NotificationSeverity.Warning)]
    public void ErrorsAndWarnings_RequireManualDismissal(NotificationSeverity severity)
    {
        var message = AdminNotificationPolicy.Prepare(new NotificationMessage { Severity = severity, Duration = 4000, CloseOnClick = true });
        Assert.Equal(0d, message.Duration);
        Assert.False(message.CloseOnClick);
    }

    [Fact]
    public void Success_RetainsExistingTimeout()
    {
        var message = AdminNotificationPolicy.Prepare(new NotificationMessage { Severity = NotificationSeverity.Success, Duration = 8000 });
        Assert.Equal(8000d, message.Duration);
    }
}
