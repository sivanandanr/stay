# scaffold-stay.ps1 — Windows-native scaffold for the Stay backend (modular monolith).
# Run from inside C:\Projects\Stay in PowerShell:
#     powershell -ExecutionPolicy Bypass -File .\scaffold-stay.ps1
#   or in an open PowerShell session:
#     Set-ExecutionPolicy -Scope Process Bypass -Force
#     .\scaffold-stay.ps1
# Requires the .NET 10 SDK (dotnet --version => 10.x). PowerShell 7 (pwsh) recommended.

$ErrorActionPreference = 'Stop'
$TF = 'net10.0'

$frameworkRef = @'
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
'@

function Inject-FrameworkRef([string]$proj) {
    (Get-Content $proj -Raw) -replace '</Project>', $frameworkRef | Set-Content $proj -Encoding utf8
}

Write-Host '>> Solution + common props'
dotnet new sln -n Stay

@'
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
'@ | Set-Content 'Directory.Build.props' -Encoding utf8

Write-Host '>> BuildingBlocks'
dotnet new classlib -n Stay.BuildingBlocks -o src/BuildingBlocks -f $TF
Remove-Item -Force src/BuildingBlocks/Class1.cs -ErrorAction SilentlyContinue

@'
namespace Stay.BuildingBlocks;

public readonly record struct Error(string Code, string Message);

public sealed class Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public Error? Error { get; }
    private Result(bool ok, T? value, Error? error) { IsSuccess = ok; Value = value; Error = error; }
    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Failure(Error error) => new(false, default, error);
}
'@ | Set-Content src/BuildingBlocks/Result.cs -Encoding utf8

@'
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Stay.BuildingBlocks;

public interface IModule
{
    void RegisterServices(IServiceCollection services, IConfiguration config);
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
'@ | Set-Content src/BuildingBlocks/IModule.cs -Encoding utf8

Inject-FrameworkRef 'src/BuildingBlocks/Stay.BuildingBlocks.csproj'
dotnet sln add 'src/BuildingBlocks/Stay.BuildingBlocks.csproj'

function Add-Module([string]$Name) {
    $lower = $Name.ToLower()
    $base  = "src/Modules/$Name"
    Write-Host ">> Module: $Name"

    dotnet new classlib -n "Stay.$Name.Domain"         -o "$base/Stay.$Name.Domain"         -f $TF
    dotnet new classlib -n "Stay.$Name.Application"     -o "$base/Stay.$Name.Application"     -f $TF
    dotnet new classlib -n "Stay.$Name.Infrastructure"  -o "$base/Stay.$Name.Infrastructure"  -f $TF
    dotnet new classlib -n "Stay.$Name.Contracts"       -o "$base/Stay.$Name.Contracts"       -f $TF
    Remove-Item -Force `
        "$base/Stay.$Name.Domain/Class1.cs", `
        "$base/Stay.$Name.Application/Class1.cs", `
        "$base/Stay.$Name.Infrastructure/Class1.cs", `
        "$base/Stay.$Name.Contracts/Class1.cs" -ErrorAction SilentlyContinue

    dotnet add "$base/Stay.$Name.Domain/Stay.$Name.Domain.csproj"              reference src/BuildingBlocks/Stay.BuildingBlocks.csproj
    dotnet add "$base/Stay.$Name.Application/Stay.$Name.Application.csproj"     reference "$base/Stay.$Name.Domain/Stay.$Name.Domain.csproj" src/BuildingBlocks/Stay.BuildingBlocks.csproj
    dotnet add "$base/Stay.$Name.Infrastructure/Stay.$Name.Infrastructure.csproj" reference "$base/Stay.$Name.Application/Stay.$Name.Application.csproj" src/BuildingBlocks/Stay.BuildingBlocks.csproj

    $infraProj = "$base/Stay.$Name.Infrastructure/Stay.$Name.Infrastructure.csproj"
    Inject-FrameworkRef $infraProj
    dotnet add $infraProj package Npgsql | Out-Null
    dotnet add $infraProj package Dapper | Out-Null

    $moduleCs = @"
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stay.BuildingBlocks;

namespace Stay.$Name.Infrastructure;

public sealed class ${Name}Module : IModule
{
    public void RegisterServices(IServiceCollection services, IConfiguration config) { }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/$lower/ping", () => Results.Ok(new { module = "$Name", ok = true }));
}
"@
    Set-Content "$base/Stay.$Name.Infrastructure/${Name}Module.cs" $moduleCs -Encoding utf8

    foreach ($p in 'Domain','Application','Infrastructure','Contracts') {
        dotnet sln add "$base/Stay.$Name.$p/Stay.$Name.$p.csproj"
    }
}

# Reference modules now (the pattern). Add the rest the same way:
# Add-Module 'ARI'; Add-Module 'Pricing'; Add-Module 'Payment'; Add-Module 'Search'
# Add-Module 'Reviews'; Add-Module 'Promotion'; Add-Module 'Channel'; Add-Module 'Admin'; Add-Module 'NotificationAdapter'
Add-Module 'Catalog'
Add-Module 'Booking'

Write-Host '>> Stay.Api (the /api/v1 host)'
dotnet new web -n Stay.Api -o src/Stay.Api -f $TF
dotnet add src/Stay.Api package Microsoft.AspNetCore.Authentication.JwtBearer | Out-Null
dotnet add src/Stay.Api package Swashbuckle.AspNetCore | Out-Null
dotnet add src/Stay.Api reference `
    src/BuildingBlocks/Stay.BuildingBlocks.csproj `
    src/Modules/Catalog/Stay.Catalog.Infrastructure/Stay.Catalog.Infrastructure.csproj `
    src/Modules/Booking/Stay.Booking.Infrastructure/Stay.Booking.Infrastructure.csproj
dotnet sln add src/Stay.Api/Stay.Api.csproj

@'
using System.Reflection;
using Stay.BuildingBlocks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication("Bearer").AddJwtBearer("Bearer", o =>
{
    o.Authority = builder.Configuration["Auth:Authority"];
    o.Audience  = builder.Configuration["Auth:Audience"];
    o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
});
builder.Services.AddAuthorization();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var modules = new[]
    {
        Assembly.Load("Stay.Catalog.Infrastructure"),
        Assembly.Load("Stay.Booking.Infrastructure")
    }
    .SelectMany(a => a.GetTypes())
    .Where(t => typeof(IModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
    .Select(t => (IModule)Activator.CreateInstance(t)!)
    .ToList();

foreach (var m in modules) m.RegisterServices(builder.Services, builder.Configuration);

var app = builder.Build();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok("healthy"));
foreach (var m in modules) m.MapEndpoints(app);

app.Run();
'@ | Set-Content src/Stay.Api/Program.cs -Encoding utf8

@'
{
  "Auth": { "Authority": "http://localhost:5001", "Audience": "stay-api" },
  "ConnectionStrings": { "Stay": "Host=localhost;Port=5432;Database=stay;Username=bp;Password=bp" }
}
'@ | Set-Content src/Stay.Api/appsettings.Development.json -Encoding utf8

Write-Host '>> Architecture boundary test'
dotnet new xunit -n Stay.ArchitectureTests -o tests/ArchitectureTests -f $TF
Remove-Item -Force tests/ArchitectureTests/UnitTest1.cs -ErrorAction SilentlyContinue
dotnet add tests/ArchitectureTests package NetArchTest.Rules | Out-Null
dotnet add tests/ArchitectureTests reference `
    src/Modules/Catalog/Stay.Catalog.Infrastructure/Stay.Catalog.Infrastructure.csproj `
    src/Modules/Booking/Stay.Booking.Infrastructure/Stay.Booking.Infrastructure.csproj

@'
using NetArchTest.Rules;
using Xunit;

public class ModuleBoundaryTests
{
    [Fact]
    public void Catalog_must_not_depend_on_Booking_Infrastructure()
    {
        var result = Types.InAssembly(typeof(Stay.Catalog.Infrastructure.CatalogModule).Assembly)
            .That().ResideInNamespaceStartingWith("Stay.Catalog")
            .ShouldNot().HaveDependencyOn("Stay.Booking.Infrastructure")
            .GetResult();
        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? new List<string>()));
    }
}
'@ | Set-Content tests/ArchitectureTests/ModuleBoundaryTests.cs -Encoding utf8
dotnet sln add tests/ArchitectureTests/Stay.ArchitectureTests.csproj

Write-Host '>> Build'
dotnet build

Write-Host ''
Write-Host 'Done. Next:'
Write-Host '  - add remaining modules: uncomment the Add-Module lines, then reference each new'
Write-Host '    Infrastructure from Stay.Api and add Assembly.Load(...) in Program.cs.'
Write-Host '  - dotnet run --project src/Stay.Api   ->   GET /api/v1/catalog/ping'
