# Gesaicon Tests

Este directorio contiene todos los tests automatizados para la aplicación Gesaicon.

## ?? Estructura de Tests

```
Gesaicon.Tests/
??? Gesaicon.Api.Tests/          # Tests del Backend (API)
?   ??? Controllers/
?   ?   ??? TicketsControllerTests.cs
?   ??? Services/
?   ?   ??? FolderIngestionServiceTests.cs
?   ?   ??? ReceiptAnalysisQueueServiceTests.cs
?   ??? Integration/
?       ??? TicketsApiIntegrationTests.cs
?
??? Gesaicon.Web.Tests/          # Tests del Frontend (Blazor)
    ??? Pages/
        ??? FileManagerTests.cs
```

## ??? Tecnologías Utilizadas

### Backend Tests
- **xUnit** - Framework de pruebas
- **Moq** - Librería de mocking
- **FluentAssertions** - Aserciones fluidas y legibles
- **Microsoft.AspNetCore.Mvc.Testing** - Tests de integración de API
- **EntityFrameworkCore.InMemory** - Base de datos en memoria para tests

### Frontend Tests
- **bUnit** - Testing de componentes Blazor
- **RichardSzalay.MockHttp** - Mock de HttpClient
- **FluentAssertions** - Aserciones fluidas

## ?? Ejecutar Tests

### Todos los tests
```bash
dotnet test
```

### Tests de un proyecto específico
```bash
dotnet test Gesaicon.Api.Tests
dotnet test Gesaicon.Web.Tests
```

### Tests con verbosidad detallada
```bash
dotnet test --logger "console;verbosity=detailed"
```

### Tests con cobertura de código
```bash
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=opencover
```

### Generar reporte HTML de cobertura
```bash
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura
reportgenerator -reports:coverage.cobertura.xml -targetdir:coveragereport -reporttypes:Html
```

## ?? Cobertura de Tests

### Gesaicon.Api.Tests

#### Controllers
- ? `TicketsControllerTests`
  - GetAll sin filtros
  - GetAll con filtro de estado
  - GetAll con búsqueda
  - GetAll con paginación
  - GetAll ordenamiento
  - GetOne con ID válido
  - GetOne con ID inválido
  - Reprocess
  - Reprocess con forzado
  - GetAnalysisMarkdown

#### Services
- ? `FolderIngestionServiceTests`
  - Inicialización correcta
  - Configuración por defecto
  - Servicio deshabilitado
  - Extensiones permitidas

- ? `ReceiptAnalysisQueueServiceTests`
  - Encolado de análisis
  - Múltiples items en cola
  - Record AnalysisWorkItem

#### Integration
- ? `TicketsApiIntegrationTests`
  - Endpoints HTTP
  - Respuestas JSON
  - Filtros y búsqueda
  - Paginación
  - Manejo de errores 404

### Gesaicon.Web.Tests

#### Pages
- ? `FileManagerTests`
  - Renderizado de componente
  - Input de búsqueda
  - Dropdown de filtro de estado
  - Botón de recarga
  - Visualización de tickets
  - Spinner de carga
  - Badges de estado
  - Controles de paginación
  - Total de registros
  - Manejo de errores
  - Encabezados de tabla

## ?? Tipos de Tests

### Tests Unitarios
Prueban componentes individuales en aislamiento usando mocks.

```csharp
[Fact]
public async Task GetOne_ReturnsTicket_WhenExists()
{
    // Arrange
    // Act
    // Assert
}
```

### Tests de Integración
Prueban la interacción entre múltiples componentes.

```csharp
[Fact]
public async Task GetAll_ReturnsSuccessStatusCode()
{
    var response = await _client.GetAsync("/api/tickets");
    response.StatusCode.Should().Be(HttpStatusCode.OK);
}
```

### Tests de Componentes Blazor
Prueban componentes de UI con bUnit.

```csharp
[Fact]
public void FileManager_RendersCorrectly()
{
    var cut = RenderComponent<FileManager>();
    cut.Find("h3").TextContent.Should().Contain("Gestor de Archivos");
}
```

## ?? Convenciones de Naming

- Nombres de métodos: `MethodName_StateUnderTest_ExpectedBehavior`
- Estructura AAA: Arrange, Act, Assert
- Un assert por test cuando sea posible
- Nombres descriptivos que expliquen la intención

## ?? Configuración de CI/CD

### GitHub Actions
```yaml
- name: Run tests
  run: dotnet test --no-build --verbosity normal

- name: Generate coverage report
  run: dotnet test /p:CollectCoverage=true
```

## ?? Objetivos de Cobertura

- **Objetivo General**: 80%+
- **Controllers**: 90%+
- **Services**: 85%+
- **Models**: 70%+
- **Componentes Blazor**: 75%+

## ?? Debugging Tests

### Visual Studio
1. Click derecho en el test
2. Seleccionar "Debug Test(s)"

### VS Code
1. Instalar extensión ".NET Core Test Explorer"
2. Click en el ícono de debug junto al test

### Línea de comandos
```bash
dotnet test --filter "FullyQualifiedName~TicketsControllerTests"
```

## ?? Contribuir

Al agregar nuevas funcionalidades, asegúrate de:

1. Escribir tests para código nuevo
2. Mantener cobertura >80%
3. Seguir convenciones de naming
4. Documentar tests complejos
5. Evitar tests frágiles (flaky tests)

## ?? Recursos

- [xUnit Documentation](https://xunit.net/)
- [Moq Quickstart](https://github.com/moq/moq4/wiki/Quickstart)
- [bUnit Documentation](https://bunit.dev/)
- [FluentAssertions](https://fluentassertions.com/)
- [ASP.NET Core Testing](https://docs.microsoft.com/en-us/aspnet/core/test/)

## ?? Troubleshooting

### Tests fallan en CI pero pasan localmente
- Verificar que no haya dependencias de rutas absolutas
- Asegurarse de que no haya dependencia del timezone
- Verificar configuración de cultura (CultureInfo)

### Tests lentos
- Usar InMemory database en lugar de base de datos real
- Minimizar delays artificiales
- Usar mocks en lugar de dependencias reales

### Tests intermitentes (Flaky)
- Evitar delays con `Task.Delay()`
- Usar `WaitForState()` en bUnit
- No depender de timing preciso

---

**Última actualización**: Enero 2025
**Versión**: 1.0.0
