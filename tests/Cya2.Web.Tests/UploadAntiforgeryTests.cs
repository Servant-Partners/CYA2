using System.Security.Claims;
using Cya2.Application.Interfaces;
using Cya2.Application.Services;
using cya2.Controllers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cya2.Web.Tests;

public sealed class UploadAntiforgeryTests
{
    [Theory]
    [InlineData("donations/preview")]
    [InlineData("donations/confirm")]
    [InlineData("accounting/preview")]
    [InlineData("accounting/confirm")]
    public async Task MissingToken_IsRejectedBeforeImportService(string endpoint)
    {
        var imports = new RecordingImportOrchestrationService();
        var logger = new RecordingLogger();
        var controller = CreateController(imports, new FakeAntiforgery { IsValid = false }, logger);
        var path = $"/api/upload/{endpoint}/private-path\r\nForged log entry";
        controller.Request.Path = path;
        controller.HttpContext.TraceIdentifier = "test-trace";

        IActionResult? result = endpoint.EndsWith("preview", StringComparison.Ordinal)
            ? (await InvokePreviewAsync(controller, endpoint)).Result
            : (await InvokeConfirmAsync(controller, endpoint)).Result;

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Antiforgery validation failed.", badRequest.Value);
        Assert.Equal(0, imports.PreviewCalls);
        Assert.Equal(0, imports.ImportCalls);
        Assert.Equal("Antiforgery validation failed for upload endpoint. TraceId=test-trace, HasHeaderToken=False, HasFormToken=False",
            Assert.Single(logger.Messages));
        Assert.DoesNotContain(logger.Values, value => value is string text && text.Contains(path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("donations/preview")]
    [InlineData("donations/confirm")]
    [InlineData("accounting/preview")]
    [InlineData("accounting/confirm")]
    public async Task ValidToken_ReachesImportService(string endpoint)
    {
        var imports = new RecordingImportOrchestrationService();
        var controller = CreateController(imports, new FakeAntiforgery { IsValid = true });

        IActionResult? result = endpoint.EndsWith("preview", StringComparison.Ordinal)
            ? (await InvokePreviewAsync(controller, endpoint)).Result
            : (await InvokeConfirmAsync(controller, endpoint)).Result;

        Assert.Equal(1, endpoint.EndsWith("preview", StringComparison.Ordinal) ? imports.PreviewCalls : imports.ImportCalls);
        Assert.NotEqual(400, result is ObjectResult objectResult ? objectResult.StatusCode : null);
    }

    [Theory]
    [InlineData("donations", "preview-1", "Donation import confirmation requested.")]
    [InlineData("donations", "preview-1\r\nForged log entry", "Donation import confirmation requested.")]
    [InlineData("accounting", "preview-1", "Accounting import confirmation requested.")]
    [InlineData("accounting", "preview-1\r\nForged log entry", "Accounting import confirmation requested.")]
    public async Task Confirmation_DoesNotLogPreviewId(string importType, string previewId, string expectedMessage)
    {
        var imports = new RecordingImportOrchestrationService();
        var logger = new RecordingLogger();
        var controller = CreateController(imports, new FakeAntiforgery { IsValid = true }, logger);

        var request = new ConfirmImportRequest { PreviewId = previewId };
        var result = importType == "donations"
            ? await controller.ConfirmDonations(request, CancellationToken.None)
            : await controller.ConfirmAccounting(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, imports.ImportCalls);
        Assert.Equal(previewId, imports.LastPreviewId);
        Assert.Equal(expectedMessage, Assert.Single(logger.Messages));
        Assert.DoesNotContain(logger.Values, value => value is string text && text.Contains(previewId, StringComparison.Ordinal));
    }

    private static UploadController CreateController(
        RecordingImportOrchestrationService imports,
        FakeAntiforgery antiforgery,
        ILogger<UploadController>? logger = null)
    {
        var controller = new UploadController(
            imports,
            logger ?? NullLogger<UploadController>.Instance,
            Options.Create(new ImportUploadOptions()),
            antiforgery);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "admin-1"), new Claim(ClaimTypes.Role, "Admin")],
                "Test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static async Task<ActionResult<FilePreviewResult>> InvokePreviewAsync(UploadController controller, string endpoint)
    {
        var file = new TestFormFile("test.xlsx");
        return endpoint == "donations/preview"
            ? await controller.PreviewDonations(file, CancellationToken.None)
            : await controller.PreviewAccounting(file, CancellationToken.None);
    }

    private sealed class TestFormFile(string fileName) : IFormFile
    {
        public Stream OpenReadStream() => new MemoryStream([1, 2, 3]);
        public long Length => 3;
        public string FileName => fileName;
        public string Name => "file";
        public string ContentType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        public IHeaderDictionary Headers => new HeaderDictionary();
        public string ContentDisposition => string.Empty;
        public void CopyTo(Stream target) => target.Write(new byte[] { 1, 2, 3 });
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) => target.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken).AsTask();
    }

    private static async Task<ActionResult<ImportResult>> InvokeConfirmAsync(UploadController controller, string endpoint)
        => endpoint == "donations/confirm"
            ? await controller.ConfirmDonations(new ConfirmImportRequest { PreviewId = "preview-1" }, CancellationToken.None)
            : await controller.ConfirmAccounting(new ConfirmImportRequest { PreviewId = "preview-1" }, CancellationToken.None);

    private sealed class FakeAntiforgery : IAntiforgery
    {
        public bool IsValid { get; set; }

        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext)
            => new("request", "cookie", "header", "form");

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext)
            => new("request", "cookie", "header", "form");

        public void SetCookieTokenAndHeader(HttpContext httpContext)
        {
        }

        public Task<bool> IsRequestValidAsync(HttpContext httpContext)
            => Task.FromResult(IsValid);

        public Task ValidateRequestAsync(HttpContext httpContext)
            => IsValid ? Task.CompletedTask : Task.FromException(new AntiforgeryValidationException("invalid"));
    }

    private sealed class RecordingLogger : ILogger<UploadController>
    {
        public List<string> Messages { get; } = [];
        public List<object?> Values { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
                Values.AddRange(properties.Select(property => property.Value));
        }
    }

    private sealed class RecordingImportOrchestrationService : IImportOrchestrationService
    {
        public int PreviewCalls { get; private set; }
        public int ImportCalls { get; private set; }
        public string? LastPreviewId { get; private set; }

        public Task<FilePreviewResult> PreviewAsync(Stream file, string importType, string fileName, string contentType, CancellationToken cancellationToken = default)
        {
            PreviewCalls++;
            return Task.FromResult(new FilePreviewResult { PreviewId = "preview-1", FileName = fileName });
        }

        public Task<ImportResult> ImportFromPreviewAsync(string previewId, string importType, CancellationToken cancellationToken = default)
        {
            ImportCalls++;
            LastPreviewId = previewId;
            return Task.FromResult(new ImportResult { ProgressId = "progress-1" });
        }

        public Task<ImportResult> StartImportFromPreviewAsync(string previewId, string importType, string? progressId = null)
        {
            ImportCalls++;
            return Task.FromResult(new ImportResult { ProgressId = progressId ?? "progress-1" });
        }
    }
}
