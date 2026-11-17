using Cya2.Application.Interfaces;
using Cya2.Core.Entities;
using Cya2.Core.Interfaces;

namespace Cya2.Application.Services;

public sealed class InternAccountService(IInternAccountStore store, IAdminAuthorizationContext authorization, IImportCacheInvalidator invalidator)
{
    public async Task<Account> SaveAsync(int? accountId, string name, string sourceId)
    {
        var actor = await authorization.GetCurrentAdminUserIdAsync() ?? throw new UnauthorizedAccessException();
        try { return await store.SaveAsync(actor, accountId, name, sourceId); }
        finally { invalidator.Invalidate(CacheDataDomains.AccountMetadata | CacheDataDomains.Authorization); }
    }

    public async Task DeleteAsync(int accountId)
    {
        var actor = await authorization.GetCurrentAdminUserIdAsync() ?? throw new UnauthorizedAccessException();
        try { await store.DeleteAsync(actor, accountId); }
        finally { invalidator.Invalidate(CacheDataDomains.AccountMetadata | CacheDataDomains.Authorization); }
    }
}
