using System.Text.Encodings.Web;
using cya2.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cya2.Web.Tests;

public sealed class AdminErrorNoticeTests
{
    [Theory]
    [InlineData(false, "alert-danger")]
    [InlineData(true, "alert-warning")]
    public async Task Notice_RendersVisibleDetailsAndExplicitCloseControl(bool warning, string expectedClass)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<AdminErrorNotice>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(AdminErrorNotice.Summary)] = "Upload error",
                [nameof(AdminErrorNotice.Detail)] = "Missing column: Tribute System Record ID\nRow 12: Gift Date is invalid.",
                [nameof(AdminErrorNotice.CloseLabel)] = "Close",
                [nameof(AdminErrorNotice.IsWarning)] = warning
            }));
            return component.ToHtmlString();
        });

        Assert.Contains(expectedClass, html);
        Assert.Contains("role=\"alert\"", html);
        Assert.Contains("aria-live=\"assertive\"", html);
        Assert.Contains("Upload error", html);
        Assert.Contains("Missing column: Tribute System Record ID", html);
        Assert.Contains("Row 12: Gift Date is invalid.", html);
        Assert.Contains("white-space:pre-wrap", html);
        Assert.Contains("aria-label=\"Close\"", html);
        Assert.Contains("btn-close", html);
    }

    [Fact]
    public async Task Notice_EncodesDetailsRatherThanRenderingMarkup()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        const string detail = "<script>alert('unsafe')</script>";
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<AdminErrorNotice>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(AdminErrorNotice.Detail)] = detail
            }));
            return component.ToHtmlString();
        });

        Assert.Contains(HtmlEncoder.Default.Encode(detail), html);
        Assert.DoesNotContain("<script>", html);
    }
}
