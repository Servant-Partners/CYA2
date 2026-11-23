using System.Reflection;
using System.Security.Claims;
using Cya2.Application.DTOs;
using Cya2.Application.Interfaces;
using Cya2.Application.Services;
using Cya2.Core.Entities;
using DateRange = Cya2.Core.ValueObjects.DateRange;
using Cya2.Infrastructure.Services;
using Cya2.Shared.Utilities;
using cya2.Components.Pages;
using cya2.Services;
using cya2.Services.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Radzen;
using Xunit;

namespace Cya2.Web.Tests;

public sealed class HomeDateRangeHandoffTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Home_RestoresAndPersistsRangesAndUsesThemAfterAccountChange(bool embedded)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        services.AddMemoryCache();
        services.AddRadzenComponents();
        services.AddSingleton<IJSRuntime, UnusedJs>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton<AuthenticationStateProvider, TestAuthentication>();
        services.AddSingleton<IUserIdResolver, UserIdResolver>();
        services.AddSingleton<IUserSelectionService, MemoryUserSelectionService>();
        services.AddSingleton<IUserDateRangeSelectionService, MemoryUserDateRangeSelectionService>();
        services.AddSingleton<IUserScopedCacheInvalidator, EmptyInvalidator>();
        services.AddSingleton<ISessionUserStateService, SessionUserStateService>();
        services.AddSingleton<IDateRangeStateService, DateRangeStateService>();
        services.AddSingleton<UserSessionHydrationService>();
        services.AddSingleton<PageLoadCorrelationContext>();
        services.AddSingleton<IHomeAuthorizationService, AllowHome>();
        services.AddSingleton<Dashboard>();
        services.AddSingleton<IFinancialDashboardService>(p => p.GetRequiredService<Dashboard>());
        services.AddSingleton<IAccountSnapshotWarmupService, EmptyWarmup>();
        services.AddSingleton<IDonorService>(DispatchProxy.Create<IDonorService, UnusedDonors>());
        using var provider = services.BuildServiceProvider();
        var session = provider.GetRequiredService<ISessionUserStateService>();
        session.CurrentUserId = "1";
        session.SelectedAccountFund = "A";
        session.SelectedStartDate = new DateTime(2023, 3, 4);
        session.SelectedEndDate = new DateTime(2023, 5, 6);
        session.SelectedDatePreset = "Custom";
        var dashboard = provider.GetRequiredService<Dashboard>();
        await using var renderer = new TestRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var home = new Home();
        foreach (var property in typeof(Home).GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                     .Where(property => property.IsDefined(typeof(InjectAttribute), true)))
            property.SetValue(home, provider.GetRequiredService(property.PropertyType));
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync(home, ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(Home.EmbedMode)] = embedded,
            [nameof(Home.ShowWelcomeHeader)] = false
        })));
        await renderer.Dispatcher.InvokeAsync(() => InvokeAsync(home, "InitializeHomeAsync"));
        Assert.Contains(("A", new DateTime(2023, 3, 4), new DateTime(2023, 5, 6)), dashboard.Summaries);

        Set(home, "customPendingStartDate", new DateTime(2022, 2, 1));
        Set(home, "customPendingEndDate", new DateTime(2022, 2, 12));
        await renderer.Dispatcher.InvokeAsync(() => InvokeAsync(home, "ApplyCustomDateRangeAsync"));
        Assert.Equal(new DateTime(2022, 2, 1), session.SelectedStartDate);
        Assert.Equal(new DateTime(2022, 2, 12), session.SelectedEndDate);
        Assert.Equal("Custom", session.SelectedDatePreset);
        Assert.True(provider.GetRequiredService<IUserDateRangeSelectionService>().TryGetDateRange("1", out var saved));
        Assert.Equal(session.SelectedStartDate, saved.StartDate);
        Assert.Equal(session.SelectedEndDate, saved.EndDate);
        Assert.False(provider.GetRequiredService<IUserDateRangeSelectionService>().TryGetDateRange("2", out _));
        Assert.Contains(("A", saved.StartDate, saved.EndDate), dashboard.Summaries);

        await renderer.Dispatcher.InvokeAsync(() => InvokeAsync(home, "OnAccountSelectionAsync", "B"));
        Assert.Contains(("B", saved.StartDate, saved.EndDate), dashboard.Summaries);
        Assert.Equal(saved.StartDate, session.SelectedStartDate);
        Assert.Equal(saved.EndDate, session.SelectedEndDate);

        await renderer.Dispatcher.InvokeAsync(() => InvokeAsync(home, "OnVisualizationPresetChanged", DateRangeHelper.DateRangePreset.LastYear));
        var expectedStart = new DateTime(DateTime.Now.Year - 1, 1, 1);
        var expectedEnd = new DateTime(DateTime.Now.Year - 1, 12, 31);
        Assert.Equal(expectedStart, session.SelectedStartDate);
        Assert.Equal(expectedEnd, session.SelectedEndDate);
        Assert.Equal("LastYear", session.SelectedDatePreset);
        Assert.Contains(("B", expectedStart, expectedEnd), dashboard.Summaries);
        Assert.Contains(("B", expectedStart, expectedEnd), dashboard.Visualizations);
    }

    private static Task InvokeAsync(Home home, string name, params object[] args) =>
        (Task)typeof(Home).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, args)!;
    private static void Set(Home home, string name, object value) => typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(home, value);
    private sealed class TestRenderer(IServiceProvider services, ILoggerFactory loggerFactory) : Renderer(services, loggerFactory)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public Task RenderAsync(IComponent component, ParameterView parameters) => RenderRootComponentAsync(AssignRootComponentId(component), parameters);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }
    private sealed class TestAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim("UserId", "1")], "test"))));
    }
    private sealed class AllowHome : IHomeAuthorizationService
    {
        public Task<HomeAuthorizationResult> AuthorizeAsync(string userIdentifier, string? preferredFund, CancellationToken cancellationToken = default)
        {
            var account = new UserAccountContextAccount { AccountId = preferredFund == "B" ? 2 : 1, Fund = preferredFund ?? "A" };
            return Task.FromResult(new HomeAuthorizationResult(true, false, 1, new UserAccountContext(), account, ""));
        }
    }
    private sealed class Dashboard : IFinancialDashboardService
    {
        public List<(string, DateTime, DateTime)> Summaries { get; } = [];
        public List<(string, DateTime, DateTime)> Visualizations { get; } = [];
        private static FinancialDashboardDto Data(string fund) => new() { SelectedAccount = fund, UserAccounts = [new UserAccountDto { AccountId = 1, Fund = "A" }, new UserAccountDto { AccountId = 2, Fund = "B" }] };
        public Task<FinancialDashboardDto> GetDashboardDataAsync(string accountFund, string userId) => Task.FromResult(Data(accountFund));
        public Task<FinancialDashboardDto> GetDashboardSummaryDataAsync(string accountFund, string userId) => Task.FromResult(Data(accountFund));
        public Task<FinancialSummaryDto> GetCustomSummaryAsync(string accountFund, DateTime startDate, DateTime endDate, string userId) { Summaries.Add((accountFund, startDate, endDate)); return Task.FromResult(new FinancialSummaryDto()); }
        public Task<List<MonthlyAccountVisualizationDto>> GetMonthlyVisualizationAsync(string accountFund, DateTime startDate, DateTime endDate, string userId) { Visualizations.Add((accountFund, startDate, endDate)); return Task.FromResult(new List<MonthlyAccountVisualizationDto>()); }
        public Task<List<UserAccountDto>> GetUserAccountsAsync(string userId) => throw new NotImplementedException();
        public Task<bool> ValidateAccountAccessAsync(string accountFund, string userId) => throw new NotImplementedException();
    }
    private sealed class EmptyWarmup : IAccountSnapshotWarmupService
    {
        public void WarmDefaultAccount(Account account) { }
        public Task WarmInitialAccountsAsync(IEnumerable<UserAccountContextAccount> accounts, int? defaultAccountId, string userId = "", bool isAdminOrViewer = false, DateRange? donorSummaryRange = null, bool isAdminUser = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void RecordSelection(Account account) { }
        public void RecordSelection(UserAccountContextAccount account) { }
        public void RecordSelection(UserAccountContextAccount account, string userId, bool isAdminOrViewer = false) { }
        public Task WarmSelectedAccountAsync(UserAccountContextAccount account, string userId, bool isAdminOrViewer = false, DateRange? donorSummaryRange = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Invalidate() { }
    }
    public class UnusedDonors : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == nameof(IDonorService.GetMissingGiftDonorsAsync)
            ? Task.FromResult(new List<DonorSummaryDto>()) : throw new NotImplementedException();
    }
    private sealed class EmptyInvalidator : IUserScopedCacheInvalidator { public void RemoveUser(string userId) { } }
    private sealed class TestNavigation : NavigationManager { public TestNavigation() => Initialize("https://localhost/", "https://localhost/"); }
    private sealed class UnusedJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotImplementedException();
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotImplementedException();
    }
}
