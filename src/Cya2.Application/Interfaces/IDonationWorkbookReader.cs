using Cya2.Core.Imports;

namespace Cya2.Application.Interfaces;

public interface IDonationWorkbookReader
{
    DonationUploadValidationResult Read(Stream file, CancellationToken cancellationToken = default);
}
