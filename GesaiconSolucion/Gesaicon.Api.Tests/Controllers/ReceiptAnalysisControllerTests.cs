using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Security.Cryptography;
using FluentAssertions;
using Gesaicon.Api.Controllers;
using Gesaicon.Api.Data;
using Gesaicon.Api.Models;
using Gesaicon.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Gesaicon.Api.Tests.Controllers
{
    public class ReceiptAnalysisControllerTests
    {
        [Fact]
        public async Task Analyze_WhenTicketExists_ReplacesFileAndUpdatesTicket()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempRoot);
            var originalCwd = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(tempRoot);

            ServiceProvider? serviceProvider = null;
            try
            {
                var uploadsRoot = Path.Combine(tempRoot, "Uploads");
                Directory.CreateDirectory(uploadsRoot);

                var services = new ServiceCollection();
                services.AddDbContext<GesaiconDbContext>(options =>
                    options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
                serviceProvider = services.BuildServiceProvider();

                Guid existingPublicId;
                int existingId;
                const string relativePath = "company/demo/2024/01/sample.jpg";
                var existingFilePath = Path.Combine(uploadsRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(existingFilePath)!);
                File.WriteAllText(existingFilePath, "old content");

                var analysisPath = Path.Combine(uploadsRoot, "analysis-old.md");
                File.WriteAllText(analysisPath, "old analysis");

                using (var scope = serviceProvider.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
                    existingPublicId = Guid.NewGuid();
                    var ticket = new ExpenseTicket
                    {
                        PublicId = existingPublicId,
                        FileName = "sample.jpg",
                        FileUrl = $"/Uploads/{relativePath}",
                        RelativePath = relativePath,
                        FileSizeBytes = 3,
                        FileHash = "OLD",
                        UploadedAt = DateTime.UtcNow.AddDays(-1),
                        Status = "Completed",
                        Amount = 10m,
                        CompanyName = "Old Corp",
                        Category = "Other",
                        AnalysisMarkdown = "old markdown",
                        AnalysisJson = "{\"old\":true}",
                        AnalysisFileName = "analysis-old.md",
                        AnalysisFileUrl = "/Uploads/analysis-old.md",
                        LastErrorMessage = "old error"
                    };
                    db.ExpenseTickets.Add(ticket);
                    await db.SaveChangesAsync();
                    existingId = ticket.Id;
                }

                var responsePayload = new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new
                            {
                                content = "{\"Amount\": 99.95, \"Company\": \"New Co\", \"Category\": \"Food\"}\n\n## Markdown\nNuevo analisis"
                            }
                        }
                    },
                    usage = new { prompt_tokens = 10, completion_tokens = 20, total_tokens = 30 }
                };
                var responseJson = JsonSerializer.Serialize(responsePayload);
                var httpClient = new HttpClient(new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                }));

                var httpFactory = new Mock<IHttpClientFactory>();
                httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["GROQ:ApiKey"] = "fake",
                        ["GROQ:ChatEndpoint"] = "https://example.local"
                    })
                    .Build();

                var promptProvider = new Mock<IAnalysisPromptProvider>();
                promptProvider.Setup(p => p.GetPrompt()).Returns("prompt");

                var controller = new ReceiptAnalysisController(httpFactory.Object, configuration, NullLogger<ReceiptAnalysisController>.Instance, promptProvider.Object);
                var httpContext = new DefaultHttpContext();
                httpContext.Request.Scheme = "https";
                httpContext.Request.Host = new HostString("api.example.com");
                httpContext.RequestServices = serviceProvider;
                controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

                var dataBytes = Encoding.UTF8.GetBytes("new image bytes");
                var formFile = new FormFile(new MemoryStream(dataBytes), 0, dataBytes.Length, "file", "ticket.png")
                {
                    Headers = new HeaderDictionary(),
                    ContentType = "image/png"
                };

                var request = new UploadExpenseTicketRequest { File = formFile };

                var result = await controller.Analyze(request, existingId) as OkObjectResult;

                result.Should().NotBeNull();
                var payload = result!.Value!;
                var valueType = payload.GetType();
                var returnedTicketId = (int?)valueType.GetProperty("TicketId")!.GetValue(payload);
                var returnedPublicId = (Guid?)valueType.GetProperty("PublicId")!.GetValue(payload);
                var returnedMarkdown = (string?)valueType.GetProperty("AnalysisMarkdown")!.GetValue(payload);
                var returnedAnalysisUrl = (string?)valueType.GetProperty("AnalysisFileUrl")!.GetValue(payload);
                returnedTicketId.Should().Be(existingId);
                returnedPublicId.Should().Be(existingPublicId);
                returnedMarkdown.Should().Contain("Nuevo analisis");
                returnedAnalysisUrl.Should().Be($"/Uploads/{existingPublicId}-analysis.md");

                using var scopeCheck = serviceProvider.CreateScope();
                var dbCheck = scopeCheck.ServiceProvider.GetRequiredService<GesaiconDbContext>();
                var updated = await dbCheck.ExpenseTickets.FirstAsync(t => t.Id == existingId);
                updated.PublicId.Should().Be(existingPublicId);
                updated.Amount.Should().Be(99.95m);
                updated.CompanyName.Should().Be("New Co");
                updated.Category.Should().Be("Food");
                updated.AnalysisMarkdown.Should().Contain("Nuevo analisis");
                updated.AnalysisFileName.Should().Be($"{existingPublicId}-analysis.md");
                updated.AnalysisFileUrl.Should().Be($"/Uploads/{existingPublicId}-analysis.md");
                updated.Status.Should().Be("Completed");
                updated.LastErrorMessage.Should().BeNull();
                updated.FileUrl.Should().Be($"/Uploads/{relativePath}");
                updated.RelativePath.Should().Be(relativePath);
                updated.FileSizeBytes.Should().Be(dataBytes.Length);

                using var sha = SHA256.Create();
                var expectedHash = Convert.ToHexString(sha.ComputeHash(dataBytes));
                updated.FileHash.Should().Be(expectedHash);

                var rewrittenBytes = File.ReadAllBytes(existingFilePath);
                rewrittenBytes.Should().Equal(dataBytes);

                var analysisFile = Path.Combine(uploadsRoot, $"{existingPublicId}-analysis.md");
                File.Exists(analysisFile).Should().BeTrue();
                File.ReadAllText(analysisFile).Should().Contain("Nuevo analisis");
            }
            finally
            {
                Directory.SetCurrentDirectory(originalCwd);
                if (serviceProvider is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
        }

        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly HttpResponseMessage _response;

            public StubHttpMessageHandler(HttpResponseMessage response)
            {
                _response = response;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_response);
            }
        }
    }
}
