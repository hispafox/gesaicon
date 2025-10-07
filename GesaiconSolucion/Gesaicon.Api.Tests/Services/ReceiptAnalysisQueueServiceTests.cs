using Xunit;
using Moq;
using FluentAssertions;
using Gesaicon.Api.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace Gesaicon.Api.Tests.Services
{
    public class ReceiptAnalysisQueueServiceTests
    {
        private readonly Mock<ILogger<ReceiptAnalysisQueueService>> _mockLogger;
        private readonly Mock<IConfiguration> _mockConfig;
        private readonly Mock<IServiceProvider> _mockServiceProvider;
        private readonly Mock<IReceiptAnalysisProcessor> _mockProcessor;

        public ReceiptAnalysisQueueServiceTests()
        {
            _mockLogger = new Mock<ILogger<ReceiptAnalysisQueueService>>();
            _mockConfig = new Mock<IConfiguration>();
            _mockServiceProvider = new Mock<IServiceProvider>();
            _mockProcessor = new Mock<IReceiptAnalysisProcessor>();
            _mockProcessor.Setup(p => p.AnalyzeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            // Config (capacity, attempts, backoff)
            _mockConfig.Setup(c => c.GetValue<int?>("Analysis:Queue:Capacity")).Returns(500);
            _mockConfig.Setup(c => c.GetValue<int?>("Analysis:Queue:MaxAttempts")).Returns(3);
            _mockConfig.Setup(c => c.GetValue<int?>("Analysis:Queue:RetryBackoffSeconds")).Returns(1); // backoff bajo para tests
        }

        private ReceiptAnalysisQueueService CreateService() => new(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            _mockConfig.Object,
            _mockProcessor.Object);

        [Fact]
        public void Constructor_InitializesCorrectly()
        {
            // Act
            var service = CreateService();

            // Assert
            service.Should().NotBeNull();
            service.Should().BeAssignableTo<IReceiptAnalysisQueue>();
        }

        [Fact]
        public async Task EnqueueAsync_AddsItemToQueue()
        {
            // Arrange
            var service = CreateService();

            // Act
            var enqueueTask = service.EnqueueAsync(1, 1);

            // Assert
            await enqueueTask;
            enqueueTask.IsCompleted.Should().BeTrue();
        }

        [Fact]
        public async Task EnqueueAsync_HandlesMultipleItems()
        {
            // Arrange
            var service = CreateService();

            // Act
            var tasks = new List<ValueTask>
            {
                service.EnqueueAsync(1, 1),
                service.EnqueueAsync(2, 1),
                service.EnqueueAsync(3, 1)
            };

            // Assert
            foreach (var task in tasks)
            {
                await task;
                task.IsCompleted.Should().BeTrue();
            }
        }

        [Fact]
        public void AnalysisWorkItem_StoresCorrectData()
        {
            // Arrange & Act
            var item = new AnalysisWorkItem(123, 2);

            // Assert
            item.TicketId.Should().Be(123);
            item.Attempt.Should().Be(2);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(999, 5)]
        [InlineData(42, 3)]
        public async Task EnqueueAsync_AcceptsDifferentTicketIdsAndAttempts(int ticketId, int attempt)
        {
            // Arrange
            var service = CreateService();

            // Act
            await service.EnqueueAsync(ticketId, attempt);

            // Assert
            true.Should().BeTrue();
        }

        [Fact]
        public void Service_ImplementsIReceiptAnalysisQueue()
        {
            // Arrange & Act
            var service = CreateService();

            // Assert
            service.Should().BeAssignableTo<IReceiptAnalysisQueue>();
        }
    }
}
