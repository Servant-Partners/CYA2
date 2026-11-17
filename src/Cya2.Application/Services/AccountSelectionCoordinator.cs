using Cya2.Application.Interfaces;
using Cya2.Core.ValueObjects;

namespace Cya2.Application.Services;

public sealed class AccountSelectionCoordinator : IAccountSelectionCoordinator
{
    private readonly ISessionUserStateService _sessionUserState;
    private readonly IUserSelectionService _userSelectionService;
    private readonly IAccountSnapshotWarmupService _warmupService;
    private readonly IUserAuthorizationService _authorizationService;

    public AccountSelectionCoordinator(
        ISessionUserStateService sessionUserState,
        IUserSelectionService userSelectionService,
        IAccountSnapshotWarmupService warmupService,
        IUserAuthorizationService authorizationService)
    {
        _sessionUserState = sessionUserState;
        _userSelectionService = userSelectionService;
        _warmupService = warmupService;
        _authorizationService = authorizationService;
    }

    public async Task SelectAsync(
        UserAccountContextAccount account,
        string userId,
        bool isAdminOrViewer,
        bool persistSelection,
        DateRange? donorSummaryRange = null)
    {
        var authorization = await _authorizationService.ValidateAccountAccessAsync(userId, account.AccountId);
        if (!authorization.IsAuthorized)
        {
            throw new UnauthorizedAccessException(authorization.Reason);
        }

        var selection = string.IsNullOrWhiteSpace(account.InternName) ? account.Fund ?? string.Empty : account.InternName;
        _sessionUserState.SelectedAccountFund = selection;
        if (persistSelection && !string.IsNullOrWhiteSpace(userId))
        {
            _userSelectionService.SetSelectedAccount(userId, selection);
        }

        await _warmupService.WarmSelectedAccountAsync(
            account,
            userId,
            isAdminOrViewer,
            donorSummaryRange);
    }
}
