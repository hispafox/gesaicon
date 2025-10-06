using Xunit;
using Moq;
using FluentAssertions;
using Gesaicon.Api.Controllers;
using Gesaicon.Api.Models;
using Gesaicon.Api.Data;
using Gesaicon.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Gesaicon.Api.Tests.Controllers
{
    public class TicketsControllerTests : IDisposable
    {
        private readonly GesaiconDbContext _context;
        private readonly Mock<ILogger<TicketsController>> _mockLogger;
        private readonly Mock<IReceiptAnalysisQueue> _mockQueue;
        private readonly TicketsController _controller;

        public TicketsControllerTests()
        {
            // Configurar InMemory Database
            var options = new DbContextOptionsBuilder<GesaiconDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new GesaiconDbContext(options);
            _mockLogger = new Mock<ILogger<TicketsController>>();
            _mockQueue = new Mock<IReceiptAnalysisQueue>();
            
            _controller = new TicketsController(_context, _mockLogger.Object, _mockQueue.Object);

            // Seed data
            SeedTestData();
        }

        private void SeedTestData()
        {
            var tickets = new[]
            {
                new ExpenseTicket
                {
                    Id = 1,
                    PublicId = Guid.NewGuid(),
                    FileName = "receipt1.jpg",
                    FileUrl = "/Uploads/receipt1.jpg",
                    FileSizeBytes = 1024,
                    Status = "Completed",
                    Amount = 25.50m,
                    CompanyName = "Test Company",
                    Category = "Food",
                    UploadedAt = DateTime.UtcNow.AddDays(-1)
                },
                new ExpenseTicket
                {
                    Id = 2,
                    PublicId = Guid.NewGuid(),
                    FileName = "receipt2.png",
                    FileUrl = "/Uploads/receipt2.png",
                    FileSizeBytes = 2048,
                    Status = "PendingAnalysis",
                    UploadedAt = DateTime.UtcNow.AddHours(-2)
                },
                new ExpenseTicket
                {
                    Id = 3,
                    PublicId = Guid.NewGuid(),
                    FileName = "receipt3.jpg",
                    FileUrl = "/Uploads/receipt3.jpg",
                    FileSizeBytes = 1500,
                    Status = "Error",
                    LastErrorMessage = "Analysis failed",
                    UploadedAt = DateTime.UtcNow.AddHours(-1)
                }
            };

            _context.ExpenseTickets.AddRange(tickets);
            _context.SaveChanges();
        }

        [Fact]
        public async Task GetAll_ReturnsAllTickets_WhenNoFilters()
        {
            // Act
            var result = await _controller.GetAll();

            // Assert
            result.Should().BeOfType<OkObjectResult>();
            var okResult = result as OkObjectResult;
            var pagedResult = okResult!.Value as TicketsController.PagedResult<TicketsController.ExpenseTicketDto>;
            
            pagedResult.Should().NotBeNull();
            pagedResult!.Total.Should().Be(3);
            pagedResult.Items.Should().HaveCount(3);
        }

        [Fact]
        public async Task GetAll_ReturnsFilteredTickets_WhenStatusProvided()
        {
            // Act
            var result = await _controller.GetAll(status: "Completed");

            // Assert
            result.Should().BeOfType<OkObjectResult>();
            var okResult = result as OkObjectResult;
            var pagedResult = okResult!.Value as TicketsController.PagedResult<TicketsController.ExpenseTicketDto>;
            
            pagedResult.Should().NotBeNull();
            pagedResult!.Total.Should().Be(1);
            pagedResult.Items.Should().HaveCount(1);
            pagedResult.Items.First().Status.Should().Be("Completed");
        }

        [Fact]
        public async Task GetAll_ReturnsFilteredTickets_WhenSearchProvided()
        {
            // Act
            var result = await _controller.GetAll(search: "Test Company");

            // Assert
            result.Should().BeOfType<OkObjectResult>();
            var okResult = result as OkObjectResult;
            var pagedResult = okResult!.Value as TicketsController.PagedResult<TicketsController.ExpenseTicketDto>;
            
            pagedResult.Should().NotBeNull();
            pagedResult!.Total.Should().Be(1);
            pagedResult.Items.First().CompanyName.Should().Be("Test Company");
        }

        [Fact]
        public async Task GetAll_ReturnsPaginatedResults()
        {
            // Act
            var result = await _controller.GetAll(skip: 0, take: 2);

            // Assert
            result.Should().BeOfType<OkObjectResult>();
            var okResult = result as OkObjectResult;
            var pagedResult = okResult!.Value as TicketsController.PagedResult<TicketsController.ExpenseTicketDto>;
            
            pagedResult.Should().NotBeNull();
            pagedResult!.Total.Should().Be(3);
            pagedResult.Items.Should().HaveCount(2);
        }

        [Fact]
        public async Task GetAll_ReturnsOrderedResults_Descending()
        {
            // Act
            var result = await _controller.GetAll(order: "desc");

            // Assert
            result.Should().BeOfType<OkObjectResult>();
            var okResult = result as OkObjectResult;
            var pagedResult = okResult!.Value as TicketsController.PagedResult<TicketsController.ExpenseTicketDto>;
            
            pagedResult.Should().NotBeNull();
            pagedResult!.Items.Should().BeInDescendingOrder(x => x.UploadedAt);
        }

        [Fact]
        public async Task GetOne_ReturnsTicket_WhenExists()
        {
            // Act
            var result = await _controller.GetOne(1);

            // Assert
            result.Should().BeOfType<OkObjectResult>();
            var okResult = result as OkObjectResult;
            var ticket = okResult!.Value as TicketsController.ExpenseTicketDto;
            
            ticket.Should().NotBeNull();
            ticket!.Id.Should().Be(1);
            ticket.FileName.Should().Be("receipt1.jpg");
            ticket.Amount.Should().Be(25.50m);
        }

        [Fact]
        public async Task GetOne_ReturnsNotFound_WhenTicketDoesNotExist()
        {
            // Act
            var result = await _controller.GetOne(999);

            // Assert
            result.Should().BeOfType<NotFoundResult>();
        }

        [Fact]
        public async Task Reprocess_EnqueuesTicket_WhenValid()
        {
            // Arrange
            _mockQueue.Setup(q => q.EnqueueAsync(It.IsAny<int>(), It.IsAny<int>()))
                .Returns(ValueTask.CompletedTask);

            // Act
            var result = await _controller.Reprocess(2, force: false);

            // Assert
            result.Should().BeOfType<OkObjectResult>();
            _mockQueue.Verify(q => q.EnqueueAsync(2, It.IsAny<int>()), Times.Once);
            
            var ticket = await _context.ExpenseTickets.FindAsync(2);
            ticket!.Status.Should().Be("PendingAnalysis");
            ticket.RetryCount.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task Reprocess_ReturnsNotFound_WhenTicketDoesNotExist()
        {
            // Act
            var result = await _controller.Reprocess(999);

            // Assert
            result.Should().BeOfType<NotFoundResult>();
        }

        [Fact]
        public async Task Reprocess_ReturnsConflict_WhenProcessingWithoutForce()
        {
            // Arrange
            var ticket = await _context.ExpenseTickets.FindAsync(2);
            ticket!.Status = "Processing";
            await _context.SaveChangesAsync();

            // Act
            var result = await _controller.Reprocess(2, force: false);

            // Assert
            result.Should().BeOfType<ConflictObjectResult>();
        }

        [Fact]
        public async Task Reprocess_SucceedsWithForce_WhenProcessing()
        {
            // Arrange
            var ticket = await _context.ExpenseTickets.FindAsync(2);
            ticket!.Status = "Processing";
            await _context.SaveChangesAsync();

            _mockQueue.Setup(q => q.EnqueueAsync(It.IsAny<int>(), It.IsAny<int>()))
                .Returns(ValueTask.CompletedTask);

            // Act
            var result = await _controller.Reprocess(2, force: true);

            // Assert
            result.Should().BeOfType<OkObjectResult>();
            _mockQueue.Verify(q => q.EnqueueAsync(2, It.IsAny<int>()), Times.Once);
        }

        [Fact]
        public async Task GetAnalysisMarkdown_ReturnsNotFound_WhenTicketDoesNotExist()
        {
            // Act
            var result = await _controller.GetAnalysisMarkdown(999);

            // Assert
            result.Should().BeOfType<NotFoundResult>();
        }

        [Fact]
        public async Task GetAnalysisMarkdown_ReturnsNotFound_WhenNoAnalysis()
        {
            // Act
            var result = await _controller.GetAnalysisMarkdown(2);

            // Assert
            result.Should().BeOfType<NotFoundResult>();
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }
    }
}
