using Xunit;
using Moq;
using FluentAssertions;
using Gesaicon.Api.Services;
using Gesaicon.Api.Data;
using Gesaicon.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Gesaicon.Api.Diagnostics;

namespace Gesaicon.Api.Tests.Services
{
    public class FolderIngestionServiceTests : IDisposable
    {
        private readonly Mock<ILogger<FolderIngestionService>> _mockLogger;
        private readonly Mock<IHostEnvironment> _mockEnv;
        private readonly Mock<IReceiptAnalysisQueue> _mockQueue;
        private readonly ServiceProvider _serviceProvider;
        private readonly string _testDirectory;

        public FolderIngestionServiceTests()
        {
            _mockLogger = new Mock<ILogger<FolderIngestionService>>();
            _mockEnv = new Mock<IHostEnvironment>();
            _mockQueue = new Mock<IReceiptAnalysisQueue>();
            
            // Crear directorio temporal para tests
            _testDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDirectory);
            
            _mockEnv.Setup(e => e.ContentRootPath).Returns(_testDirectory);

            // Configurar ServiceProvider con DbContext
            var services = new ServiceCollection();
            services.AddDbContext<GesaiconDbContext>(options =>
                options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            
            _serviceProvider = services.BuildServiceProvider();
        }

        [Fact]
        public void Constructor_InitializesCorrectly()
        {
            // Arrange
            var options = Options.Create(new FileIngestionOptions { Enabled = true });

            // Act
            var service = new FolderIngestionService(
                _mockLogger.Object,
                _serviceProvider,
                _mockEnv.Object,
                options,
                _mockQueue.Object,
                new BackgroundStatusStore());

            // Assert
            service.Should().NotBeNull();
        }

        [Fact]
        public void FileIngestionOptions_HasDefaultValues()
        {
            // Arrange & Act
            var options = new FileIngestionOptions();

            // Assert
            options.Enabled.Should().BeFalse();
            options.SourceFolder.Should().Be("Incoming");
            options.BackupFolder.Should().Be("IncomingBackup");
            options.ProcessedFolder.Should().Be("IncomingProcessed");
            options.ErrorFolder.Should().Be("IncomingError");
            options.ScanIntervalSeconds.Should().Be(15);
            options.StableAgeSeconds.Should().Be(5);
            options.MaxPerScan.Should().Be(25);
            options.EnqueueForAnalysis.Should().BeTrue();
            options.AllowedExtensions.Should().Contain(".jpg");
            options.AllowedExtensions.Should().Contain(".png");
        }

        [Fact]
        public void FileIngestionOptions_CanBeConfigured()
        {
            // Arrange
            var options = new FileIngestionOptions
            {
                Enabled = true,
                SourceFolder = "CustomIncoming",
                ScanIntervalSeconds = 30,
                MaxPerScan = 50
            };

            // Assert
            options.Enabled.Should().BeTrue();
            options.SourceFolder.Should().Be("CustomIncoming");
            options.ScanIntervalSeconds.Should().Be(30);
            options.MaxPerScan.Should().Be(50);
        }

        [Fact]
        public async Task Service_DoesNotStart_WhenDisabled()
        {
            // Arrange
            var options = Options.Create(new FileIngestionOptions { Enabled = false });
            var service = new FolderIngestionService(
                _mockLogger.Object,
                _serviceProvider,
                _mockEnv.Object,
                options,
                _mockQueue.Object,
                new BackgroundStatusStore());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

            // Act
            await service.StartAsync(cts.Token);
            await Task.Delay(100);
            await service.StopAsync(CancellationToken.None);

            // Assert
            _mockLogger.Verify(
                l => l.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Deshabilitado")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public void AllowedExtensions_ShouldIncludeCommonImageFormats()
        {
            // Arrange
            var options = new FileIngestionOptions();

            // Assert
            options.AllowedExtensions.Should().Contain(new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" });
        }

        [Fact]
        public void DefaultConfiguration_ShouldHaveReasonableValues()
        {
            // Arrange
            var options = new FileIngestionOptions();

            // Assert
            options.ScanIntervalSeconds.Should().BeGreaterThan(0);
            options.StableAgeSeconds.Should().BeGreaterThan(0);
            options.MaxPerScan.Should().BeGreaterThan(0);
            options.SourceFolder.Should().NotBeNullOrEmpty();
            options.BackupFolder.Should().NotBeNullOrEmpty();
            options.ProcessedFolder.Should().NotBeNullOrEmpty();
            options.ErrorFolder.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public void ExpenseTicket_ShouldHavePurchaseDateField()
        {
            // Arrange & Act
            var ticket = new ExpenseTicket
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                PurchaseDate = new DateTime(2025, 10, 6),
                ExpenseYear = 2025,
                ExpenseMonth = 10
            };

            // Assert
            ticket.PurchaseDate.Should().NotBeNull();
            ticket.PurchaseDate.Value.Year.Should().Be(2025);
            ticket.PurchaseDate.Value.Month.Should().Be(10);
            ticket.PurchaseDate.Value.Day.Should().Be(6);
        }

        [Fact]
        public void ExpenseTicket_PurchaseDateCanBeNull()
        {
            // Arrange & Act
            var ticket = new ExpenseTicket
            {
                Id = 1,
                PublicId = Guid.NewGuid(),
                PurchaseDate = null
            };

            // Assert
            ticket.PurchaseDate.Should().BeNull();
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_testDirectory))
                {
                    Directory.Delete(_testDirectory, true);
                }
            }
            catch { }

            _serviceProvider?.Dispose();
        }
    }
}
