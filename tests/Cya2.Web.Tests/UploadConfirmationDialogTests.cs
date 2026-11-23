using Cya2.Application.Interfaces;
using cya2.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Radzen;
using Xunit;

namespace Cya2.Web.Tests;

public sealed class UploadConfirmationDialogTests
{
    [Fact]
    public async Task Preparation_ShowsLoadingBeforeReadingAndThenShowsValidatedConfirmation()
    {
        using var services = CreateServices();
        var activator = services.GetRequiredService<CapturingActivator>();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var pendingPreview = new TaskCompletionSource<FilePreviewResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationCalls = 0;
        var submissions = 0;
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<TestDialog>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(UploadConfirmationDialog.PreparePreview)] = (Func<Task<FilePreviewResult>>)(() =>
                {
                    preparationCalls++;
                    return pendingPreview.Task;
                }),
                [nameof(UploadConfirmationDialog.SubmitImport)] = (Func<Task>)(() =>
                {
                    submissions++;
                    return Task.CompletedTask;
                })
            })));

        var initial = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("aria-busy=\"true\"", initial);
        Assert.Contains("role=\"status\"", initial);
        Assert.Equal(0, preparationCalls);

        Task preparation = Task.CompletedTask;
        await renderer.Dispatcher.InvokeAsync(() => { preparation = activator.Dialog!.AfterRenderAsync(true); });
        Assert.Equal(1, preparationCalls);
        Assert.Contains("aria-busy=\"true\"", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));

        pendingPreview.SetResult(new FilePreviewResult
        {
            PreviewId = "preview",
            FileName = "donations.xlsx",
            FileSizeBytes = 1024,
            MinimumDonationDate = new DateOnly(2020, 1, 1),
            RequiresOldDateAcknowledgment = true
        });
        await preparation;
        var ready = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.DoesNotContain("aria-busy=\"true\"", ready);
        Assert.Contains("donations.xlsx", ready);
        Assert.Contains("type=\"checkbox\"", ready);
        Assert.Contains("disabled", ready);
        Assert.Equal(0, submissions);

        await renderer.Dispatcher.InvokeAsync(() => activator.Dialog!.AfterRenderAsync(false));
        Assert.Equal(1, preparationCalls);
    }

    [Fact]
    public async Task PreparationFailure_ShowsSafeErrorWithoutConfirmation()
    {
        using var services = CreateServices();
        var activator = services.GetRequiredService<CapturingActivator>();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<TestDialog>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(UploadConfirmationDialog.PreparePreview)] = (Func<Task<FilePreviewResult>>)(() =>
                    Task.FromException<FilePreviewResult>(new InvalidOperationException("private diagnostic detail")))
            })));

        await renderer.Dispatcher.InvokeAsync(() => activator.Dialog!.AfterRenderAsync(true));
        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("role=\"alert\"", html);
        Assert.DoesNotContain("aria-busy=\"true\"", html);
        Assert.DoesNotContain("private diagnostic detail", html);
        Assert.DoesNotMatch("<[^>]+\\bclass=\"[^\"]*\\bupload-confirm-actions\\b", html);
    }

    [Fact]
    public async Task ExistingPreview_ShowsConfirmationWithoutLoading()
    {
        using var services = CreateServices();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<TestDialog>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(UploadConfirmationDialog.Preview)] = new FilePreviewResult
                {
                    PreviewId = "preview",
                    FileName = "accounting.xlsx",
                    FileSizeBytes = 1024
                }
            })));

        var html = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
        Assert.Contains("accounting.xlsx", html);
        Assert.DoesNotContain("aria-busy=\"true\"", html);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        services.AddSingleton<IJSRuntime, UnusedJsRuntime>();
        services.AddSingleton<NavigationManager, TestNavigationManager>();
        services.AddScoped<DialogService>();
        services.AddSingleton<CapturingActivator>();
        services.AddSingleton<IComponentActivator>(provider => provider.GetRequiredService<CapturingActivator>());
        return services.BuildServiceProvider();
    }

    public sealed class TestDialog : UploadConfirmationDialog
    {
        public Task AfterRenderAsync(bool firstRender) => base.OnAfterRenderAsync(firstRender);
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public TestDialog? Dialog { get; private set; }

        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is TestDialog dialog) Dialog = dialog;
            return component;
        }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/admin");
    }

    private sealed class UnusedJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => throw new InvalidOperationException("JavaScript is not used during static rendering.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);
    }
}
