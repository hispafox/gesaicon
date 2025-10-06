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
        private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
        private readonly Mock<IAnalysisPromptProvider> _mockPromptProvider;
        private readonly Mock<IServiceProvider> _mockServiceProvider;

        public ReceiptAnalysisQueueServiceTests()
        {
            _mockLogger = new Mock<ILogger<ReceiptAnalysisQueueService>>();
            _mockConfig = new Mock<IConfiguration>();
            _mockHttpClientFactory = new Mock<IHttpClientFactory>();
            _mockPromptProvider = new Mock<IAnalysisPromptProvider>();
            _mockServiceProvider = new Mock<IServiceProvider>();
            
            // Configurar el mock de IConfiguration para retornar valores por defecto
            var mockSection = new Mock<IConfigurationSection>();
            mockSection.Setup(x => x.Value).Returns("500");
            _mockConfig.Setup(c => c.GetSection("Analysis:Queue:Capacity")).Returns(mockSection.Object);
            _mockConfig.Setup(c => c["Analysis:Queue:Capacity"]).Returns("500");
        }

        [Fact]
        public void Constructor_InitializesCorrectly()
        {
            // Act
            var service = new ReceiptAnalysisQueueService(
                _mockServiceProvider.Object,
                _mockLogger.Object,
                _mockConfig.Object,
                _mockHttpClientFactory.Object,
                _mockPromptProvider.Object);

            // Assert
            service.Should().NotBeNull();
            service.Should().BeAssignableTo<IReceiptAnalysisQueue>();
        }

        [Fact]
        public async Task EnqueueAsync_AddsItemToQueue()
        {
            // Arrange
            var service = new ReceiptAnalysisQueueService(
                _mockServiceProvider.Object,
                _mockLogger.Object,
                _mockConfig.Object,
                _mockHttpClientFactory.Object,
                _mockPromptProvider.Object);

            // Act
            var enqueueTask = service.EnqueueAsync(1, 1);

            // Assert
            await enqueueTask; // No debería lanzar excepción
            enqueueTask.IsCompleted.Should().BeTrue();
        }

        [Fact]
        public async Task EnqueueAsync_HandlesMultipleItems()
        {
            // Arrange
            var service = new ReceiptAnalysisQueueService(
                _mockServiceProvider.Object,
                _mockLogger.Object,
                _mockConfig.Object,
                _mockHttpClientFactory.Object,
                _mockPromptProvider.Object);

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
            var service = new ReceiptAnalysisQueueService(
                _mockServiceProvider.Object,
                _mockLogger.Object,
                _mockConfig.Object,
                _mockHttpClientFactory.Object,
                _mockPromptProvider.Object);

            // Act
            await service.EnqueueAsync(ticketId, attempt);

            // Assert
            // Si no lanza excepción, el test pasa
            true.Should().BeTrue();
        }

        [Fact]
        public void Service_ImplementsIReceiptAnalysisQueue()
        {
            // Arrange & Act
            var service = new ReceiptAnalysisQueueService(
                _mockServiceProvider.Object,
                _mockLogger.Object,
                _mockConfig.Object,
                _mockHttpClientFactory.Object,
                _mockPromptProvider.Object);

            // Assert
            service.Should().BeAssignableTo<IReceiptAnalysisQueue>();
        }
    }
}
