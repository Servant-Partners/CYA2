using Cya2.Core.Entities;

namespace Cya2.Core.Interfaces;

public interface IInternAccountStore
{
    Task<Account> SaveAsync(int actorId, int? accountId, string name, string sourceId);
    Task DeleteAsync(int actorId, int accountId);
}
