using Xunit;
using Bunit;
using FluentAssertions;
using Gesaicon.Web.Pages;
using Gesaicon.Web.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using RichardSzalay.MockHttp;

namespace Gesaicon.Web.Tests.Pages
{
    public class FileManagerTests : TestContext
    {
        private readonly MockHttpMessageHandler _mockHttp;

        public FileManagerTests()
        {
            _mockHttp = new MockHttpMessageHandler();
            var httpClient = _mockHttp.ToHttpClient();
            httpClient.BaseAddress = new Uri("http://localhost/");
            Services.AddSingleton(httpClient);
        }

        [Fact]
        public void FileManager_RendersCorrectly()
        {
            // Arrange
            var mockData = new PagedResult<ExpenseTicketDto>(
                Total: 0,
                Items: new List<ExpenseTicketDto>()
            );

            _mockHttp.When("*api/tickets*")
                .Respond("application/json", System.Text.Json.JsonSerializer.Serialize(mockData));

            // Act
            var cut = RenderComponent<FileManager>();

            // Assert
            cut.Find("h3").TextContent.Should().Contain("Gestor de Archivos");
        }

        [Fact]
        public void FileManager_HasSearchInput()
        {
            // Arrange
            var mockData = new PagedResult<ExpenseTicketDto>(0, new List<ExpenseTicketDto>());
            _mockHttp.When("*").Respond("application/json", System.Text.Json.JsonSerializer.Serialize(mockData));

            // Act
            var cut = RenderComponent<FileManager>();

            // Assert
            cut.FindAll("input[placeholder='Buscar...']").Should().HaveCount(1);
        }

        [Fact]
        public void FileManager_HasStatusFilterDropdown()
        {
            // Arrange
            var mockData = new PagedResult<ExpenseTicketDto>(0, new List<ExpenseTicketDto>());
            _mockHttp.When("*").Respond("application/json", System.Text.Json.JsonSerializer.Serialize(mockData));

            // Act
            var cut = RenderComponent<FileManager>();

            // Assert
            var selects = cut.FindAll("select");
            selects.Count.Should().BeGreaterThanOrEqualTo(1);
            selects.First().InnerHtml.Should().Contain("Processing");
            selects.First().InnerHtml.Should().Contain("Completed");
            selects.First().InnerHtml.Should().Contain("Error");
        }

        [Fact]
        public void FileManager_HasReloadButton()
        {
            // Arrange
            var mockData = new PagedResult<ExpenseTicketDto>(0, new List<ExpenseTicketDto>());
            _mockHttp.When("*").Respond("application/json", System.Text.Json.JsonSerializer.Serialize(mockData));

            // Act
            var cut = RenderComponent<FileManager>();

            // Assert
            var buttons = cut.FindAll("button");
            buttons.Should().Contain(b => b.TextContent.Contains("Recargar"));
        }

        [Fact]
        public async Task FileManager_DisplaysTickets_WhenDataIsLoaded()
        {
            // Arrange
            var mockTickets = new List<ExpenseTicketDto>
            {
                new ExpenseTicketDto(
                    Id: 1,
                    PublicId: Guid.NewGuid(),
                    FileName: "test1.jpg",
                    FileUrl: "/Uploads/test1.jpg",
                    FileSizeBytes: 1024,
                    Status: "Completed",
                    Amount: 50.00m,
                    CompanyName: "Test Company",
                    Category: "Food",
                    UploadedAt: DateTime.UtcNow,
                    AnalysisFileUrl: "/analysis1.md",
                    AnalysisFileName: "analysis1.md",
                    LastErrorMessage: null
                )
            };

            var mockData = new PagedResult<ExpenseTicketDto>(1, mockTickets);
            _mockHttp.When("*").Respond("application/json", System.Text.Json.JsonSerializer.Serialize(mockData));

            // Act
            var cut = RenderComponent<FileManager>();
            await Task.Delay(100); // Esperar a que se carguen los datos

            // Assert
            cut.WaitForState(() => cut.FindAll("tbody tr").Count > 0, TimeSpan.FromSeconds(2));
            cut.FindAll("tbody tr").Should().HaveCount(1);
            cut.Markup.Should().Contain("test1.jpg");
            cut.Markup.Should().Contain("Test Company");
        }

        [Fact]
        public void FileManager_ShowsLoadingSpinner_WhenLoading()
        {
            // Arrange
            _mockHttp.When("*").Respond(async (request) =>
            {
                await Task.Delay(5000); // Simular respuesta lenta
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                };
            });

            // Act
            var cut = RenderComponent<FileManager>();

            // Assert
            cut.FindAll(".spinner-border").Count.Should().BeGreaterThan(0);
        }

        [Fact]
        public void FileManager_DisplaysStatusBadges()
        {
            // Arrange
            var mockTickets = new List<ExpenseTicketDto>
            {
                new ExpenseTicketDto(1, Guid.NewGuid(), "test1.jpg", "/test1.jpg", 1024, "Completed", 
                    25m, "Company", "Food", DateTime.UtcNow, null, null, null),
                new ExpenseTicketDto(2, Guid.NewGuid(), "test2.jpg", "/test2.jpg", 2048, "Error", 
                    null, null, null, DateTime.UtcNow, null, null, "Error message")
            };

            var mockData = new PagedResult<ExpenseTicketDto>(2, mockTickets);
            _mockHttp.When("*").Respond("application/json", System.Text.Json.JsonSerializer.Serialize(mockData));

            // Act
            var cut = RenderComponent<FileManager>();
            cut.WaitForState(() => cut.FindAll(".status-badge").Count > 0, TimeSpan.FromSeconds(2));

            // Assert
            var badges = cut.FindAll(".status-badge");
            badges.Should().HaveCount(2);
            badges.Should().Contain(b => b.TextContent.Contains("Completed"));
            badges.Should().Contain(b => b.TextContent.Contains("Error"));
        }

        [Fact]
        public void FileManager_HasPaginationControls()
        {
            // Arrange
            var mockData = new PagedResult<ExpenseTicketDto>(100, new List<ExpenseTicketDto>());
            _mockHttp.When("*").Respond("application/json", System.Text.Json.JsonSerializer.Serialize(mockData));

            // Act
            var cut = RenderComponent<FileManager>();
            cut.WaitForState(() => cut.FindAll(".pagination").Count > 0, TimeSpan.FromSeconds(2));

            // Assert
            var pagination = cut.FindAll(".pagination");
            pagination.Should().HaveCount(1);
            cut.Markup.Should().Contain("Anterior");
            cut.Markup.Should().Contain("Siguiente");
        }

        [Fact]
        public void FileManager_DisplaysTotalCount()
        {
            // Arrange
            var mockData = new PagedResult<ExpenseTicketDto>(42, new List<ExpenseTicketDto>());
            _mockHttp.When("*").Respond("application/json", System.Text.Json.JsonSerializer.Serialize(mockData));

            // Act
            var cut = RenderComponent<FileManager>();
            cut.WaitForState(() => cut.Markup.Contains("Total:"), TimeSpan.FromSeconds(2));

            // Assert
            cut.Markup.Should().Contain("Total:");
            cut.Markup.Should().Contain("42");
            cut.Markup.Should().Contain("tickets");
        }

        [Fact]
        public void FileManager_ShowsErrorMessage_WhenApiCallFails()
        {
            // Arrange
            _mockHttp.When("*").Respond(HttpStatusCode.InternalServerError);

            // Act
            var cut = RenderComponent<FileManager>();
            cut.WaitForState(() => cut.FindAll(".alert-danger").Count > 0, TimeSpan.FromSeconds(2));

            // Assert
            cut.FindAll(".alert-danger").Should().HaveCount(1);
        }

        [Fact]
        public void FileManager_HasTableHeaders()
        {
            // Arrange
            var mockData = new PagedResult<ExpenseTicketDto>(0, new List<ExpenseTicketDto>());
            _mockHttp.When("*").Respond("application/json", System.Text.Json.JsonSerializer.Serialize(mockData));

            // Act
            var cut = RenderComponent<FileManager>();

            // Assert
            var headers = cut.FindAll("thead th");
            headers.Count.Should().BeGreaterThanOrEqualTo(7);
            cut.Markup.Should().Contain("Archivo");
            cut.Markup.Should().Contain("Empresa");
            cut.Markup.Should().Contain("Categoría");
            cut.Markup.Should().Contain("Importe");
            cut.Markup.Should().Contain("Estado");
            cut.Markup.Should().Contain("Acciones");
        }
    }
}
