using Cya2.Application.Services;
using cya2.Components.Shared.LanguageResources;
using Microsoft.Extensions.Localization;
using Radzen;

namespace cya2.Services;

public static class AdminNotificationPolicy
{
    public static string UploadErrorDetail(Exception exception, IStringLocalizer<Resource> localizer)
        => exception is ImportUploadValidationException validation
            ? validation.Message
            : localizer["UploadValidationFailed"].Value;

    public static NotificationMessage Prepare(NotificationMessage message)
    {
        if (message.Severity is NotificationSeverity.Error or NotificationSeverity.Warning)
        {
            message.Duration = 0;
            message.CloseOnClick = false;
        }
        return message;
    }
}
