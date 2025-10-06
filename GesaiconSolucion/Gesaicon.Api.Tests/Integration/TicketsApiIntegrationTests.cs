using System.Net;
using System.Net.Http.Json;
using Xunit;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Gesaicon.Api.Data;
using Gesaicon.Api.Models;
using Microsoft.EntityFrameworkCore;
using static Gesaicon.Api.Controllers.TicketsController;

namespace Gesaicon.Api.Tests.Integration
{
    public class TicketsApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly WebApplicationFactory<Program> _factory;
        private readonly HttpClient _client;

        public TicketsApiIntegrationTests(WebApplicationFactory<Program> factory)
        {
            _factory = factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    // Remover el DbContext existente
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<GesaiconDbContext>));

                    if (descriptor != null)
                    {
                        services.Remove(descriptor);
                    }

                    // Remover también el DbContext registrado
                    var dbContextDescriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(GesaiconDbContext));

                    if (dbContextDescriptor != null)
                    {
                        services.Remove(dbContextDescriptor);
                    }

                    // Agregar DbContext con InMemory database
                    services.AddDbContext<GesaiconDbContext>(options =>
                    {
                        options.UseInMemoryDatabase("TestDb_" + Guid.NewGuid());
                    });
                });
            });

            _client = _factory.CreateClient();
            
            // Seed test data después de crear el cliente
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
            SeedTestData(db);
        }

        private static void SeedTestData(GesaiconDbContext context)
        {
            var tickets = new[]
            {
                new ExpenseTicket
                {
                    PublicId = Guid.NewGuid(),
                    FileName = "test-receipt1.jpg",
                    FileUrl = "/Uploads/test-receipt1.jpg",
                    FileSizeBytes = 1024,
                    Status = "Completed",
                    Amount = 50.00m,
                    CompanyName = "Test Store",
                    Category = "Groceries",
                    UploadedAt = DateTime.UtcNow.AddDays(-1)
                },
                new ExpenseTicket
                {
                    PublicId = Guid.NewGuid(),
                    FileName = "test-receipt2.png",
                    FileUrl = "/Uploads/test-receipt2.png",
                    FileSizeBytes = 2048,
                    Status = "PendingAnalysis",
                    UploadedAt = DateTime.UtcNow.AddHours(-2)
                }
            };

            context.ExpenseTickets.AddRange(tickets);
            context.SaveChanges();
        }

        [Fact]
        public async Task GetAll_ReturnsSuccessStatusCode()
        {
            // Act
            var response = await _client.GetAsync("/api/tickets");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task GetAll_ReturnsPagedResult()
        {
            // Act
            var response = await _client.GetAsync("/api/tickets");
            var result = await response.Content.ReadFromJsonAsync<PagedResult<ExpenseTicketDto>>();

            // Assert
            result.Should().NotBeNull();
            result!.Total.Should().BeGreaterThan(0);
            result.Items.Should().NotBeEmpty();
        }

        [Fact]
        public async Task GetAll_WithStatusFilter_ReturnsFilteredResults()
        {
            // Act
            var response = await _client.GetAsync("/api/tickets?status=Completed");
            var result = await response.Content.ReadFromJsonAsync<PagedResult<ExpenseTicketDto>>();

            // Assert
            result.Should().NotBeNull();
            result!.Items.Should().OnlyContain(t => t.Status == "Completed");
        }

        [Fact]
        public async Task GetAll_WithPagination_ReturnsCorrectNumberOfItems()
        {
            // Act
            var response = await _client.GetAsync("/api/tickets?skip=0&take=1");
            var result = await response.Content.ReadFromJsonAsync<PagedResult<ExpenseTicketDto>>();

            // Assert
            result.Should().NotBeNull();
            result!.Items.Count.Should().BeLessThanOrEqualTo(1);
        }

        [Fact]
        public async Task GetAll_WithSearch_ReturnsMatchingResults()
        {
            // Act
            var response = await _client.GetAsync("/api/tickets?search=Test Store");
            var result = await response.Content.ReadFromJsonAsync<PagedResult<ExpenseTicketDto>>();

            // Assert
            result.Should().NotBeNull();
            result!.Items.Should().Contain(t => t.CompanyName == "Test Store");
        }

        [Fact]
        public async Task GetOne_WithValidId_ReturnsTicket()
        {
            // Arrange
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
            var firstTicket = await db.ExpenseTickets.FirstAsync();

            // Act
            var response = await _client.GetAsync($"/api/tickets/{firstTicket.Id}");
            var ticket = await response.Content.ReadFromJsonAsync<ExpenseTicketDto>();

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            ticket.Should().NotBeNull();
            ticket!.Id.Should().Be(firstTicket.Id);
        }

        [Fact]
        public async Task GetOne_WithInvalidId_ReturnsNotFound()
        {
            // Act
            var response = await _client.GetAsync("/api/tickets/99999");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task GetAnalysisMarkdown_WithNoAnalysis_ReturnsNotFound()
        {
            // Arrange
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
            var ticket = await db.ExpenseTickets.FirstAsync(t => t.Status == "PendingAnalysis");

            // Act
            var response = await _client.GetAsync($"/api/tickets/{ticket.Id}/analysis");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task Reprocess_WithValidId_ReturnsSuccess()
        {
            // Arrange
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GesaiconDbContext>();
            var ticket = await db.ExpenseTickets.FirstAsync();

            // Act
            var response = await _client.PostAsync($"/api/tickets/{ticket.Id}/reprocess", null);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Reprocess_WithInvalidId_ReturnsNotFound()
        {
            // Act
            var response = await _client.PostAsync("/api/tickets/99999/reprocess", null);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task ApiEndpoints_ReturnJsonContentType()
        {
            // Act
            var response = await _client.GetAsync("/api/tickets");

            // Assert
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        }
    }
}
