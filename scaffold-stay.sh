#!/usr/bin/env bash
# scaffold-stay.sh — create the Stay backend skeleton (modular monolith).
# Run from inside the Stay/ folder:  bash scaffold-stay.sh
# Requires the .NET 10 SDK. Idempotent-ish: re-running into a populated dir will error on existing projects.
set -euo pipefail

TF="net10.0"
SLN="Stay"

echo ">> Solution + common props"
dotnet new sln -n "$SLN"

cat > Directory.Build.props <<'EOF'
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
EOF

echo ">> BuildingBlocks"
dotnet new classlib -n Stay.BuildingBlocks -o src/BuildingBlocks -f "$TF"
rm -f src/BuildingBlocks/Class1.cs
cat > src/BuildingBlocks/Result.cs <<'EOF'
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
EOF
cat > src/BuildingBlocks/IModule.cs <<'EOF'
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Stay.BuildingBlocks;

// Each module implements this; Stay.Api discovers and wires them at the composition root.
public interface IModule
{
    void RegisterServices(IServiceCollection services, IConfiguration config);
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
EOF
# BuildingBlocks needs ASP.NET abstractions for IModule
dotnet add src/BuildingBlocks package Microsoft.AspNetCore.App.Ref >/dev/null 2>&1 || true
# (Simpler: reference the framework via FrameworkReference)
sed -i 's#</Project>#  <ItemGroup>\n    <FrameworkReference Include="Microsoft.AspNetCore.App" />\n  </ItemGroup>\n</Project>#' src/BuildingBlocks/Stay.BuildingBlocks.csproj
dotnet sln add src/BuildingBlocks/Stay.BuildingBlocks.csproj

# ---- module factory: Domain / Application / Infrastructure / Contracts ----
add_module () {
  local NAME="$1"
  local LOWER; LOWER="$(echo "$NAME" | tr '[:upper:]' '[:lower:]')"
  local BASE="src/Modules/$NAME"
  echo ">> Module: $NAME"

  dotnet new classlib -n "Stay.$NAME.Domain"        -o "$BASE/Stay.$NAME.Domain"        -f "$TF"; rm -f "$BASE/Stay.$NAME.Domain/Class1.cs"
  dotnet new classlib -n "Stay.$NAME.Application"    -o "$BASE/Stay.$NAME.Application"    -f "$TF"; rm -f "$BASE/Stay.$NAME.Application/Class1.cs"
  dotnet new classlib -n "Stay.$NAME.Infrastructure" -o "$BASE/Stay.$NAME.Infrastructure" -f "$TF"; rm -f "$BASE/Stay.$NAME.Infrastructure/Class1.cs"
  dotnet new classlib -n "Stay.$NAME.Contracts"      -o "$BASE/Stay.$NAME.Contracts"      -f "$TF"; rm -f "$BASE/Stay.$NAME.Contracts/Class1.cs"

  # layering: Domain <- Application <- Infrastructure ; all -> BuildingBlocks ; Contracts standalone
  dotnet add "$BASE/Stay.$NAME.Domain/Stay.$NAME.Domain.csproj"               reference src/BuildingBlocks/Stay.BuildingBlocks.csproj
  dotnet add "$BASE/Stay.$NAME.Application/Stay.$NAME.Application.csproj"      reference "$BASE/Stay.$NAME.Domain/Stay.$NAME.Domain.csproj" src/BuildingBlocks/Stay.BuildingBlocks.csproj
  dotnet add "$BASE/Stay.$NAME.Infrastructure/Stay.$NAME.Infrastructure.csproj" reference "$BASE/Stay.$NAME.Application/Stay.$NAME.Application.csproj" src/BuildingBlocks/Stay.BuildingBlocks.csproj

  # Infrastructure framework ref (so the module can register endpoints) + data packages
  sed -i 's#</Project>#  <ItemGroup>\n    <FrameworkReference Include="Microsoft.AspNetCore.App" />\n  </ItemGroup>\n</Project>#' "$BASE/Stay.$NAME.Infrastructure/Stay.$NAME.Infrastructure.csproj"
  dotnet add "$BASE/Stay.$NAME.Infrastructure/Stay.$NAME.Infrastructure.csproj" package Npgsql >/dev/null
  dotnet add "$BASE/Stay.$NAME.Infrastructure/Stay.$NAME.Infrastructure.csproj" package Dapper >/dev/null

  # a minimal module with a health endpoint, proving the wiring
  cat > "$BASE/Stay.$NAME.Infrastructure/${NAME}Module.cs" <<EOF2
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stay.BuildingBlocks;

namespace Stay.$NAME.Infrastructure;

public sealed class ${NAME}Module : IModule
{
    public void RegisterServices(IServiceCollection services, IConfiguration config) { }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/v1/$LOWER/ping", () => Results.Ok(new { module = "$NAME", ok = true }));
}
EOF2

  for p in Domain Application Infrastructure Contracts; do
    dotnet sln add "$BASE/Stay.$NAME.$p/Stay.$NAME.$p.csproj"
  done
}

# Reference modules now (the pattern). Add the rest the same way.
add_module Catalog
add_module Booking
# add_module ARI ; add_module Pricing ; add_module Payment ; add_module Search
# add_module Reviews ; add_module Promotion ; add_module Channel ; add_module Admin ; add_module NotificationAdapter

echo ">> Stay.Api (the /api/v1 host)"
dotnet new web -n Stay.Api -o src/Stay.Api -f "$TF"
dotnet add src/Stay.Api package Microsoft.AspNetCore.Authentication.JwtBearer >/dev/null
dotnet add src/Stay.Api package Swashbuckle.AspNetCore >/dev/null
dotnet add src/Stay.Api reference src/BuildingBlocks/Stay.BuildingBlocks.csproj \
  src/Modules/Catalog/Stay.Catalog.Infrastructure/Stay.Catalog.Infrastructure.csproj \
  src/Modules/Booking/Stay.Booking.Infrastructure/Stay.Booking.Infrastructure.csproj
dotnet sln add src/Stay.Api/Stay.Api.csproj

cat > src/Stay.Api/Program.cs <<'EOF'
using System.Reflection;
using Stay.BuildingBlocks;

var builder = WebApplication.CreateBuilder(args);

// AuthN against UserService (OIDC). Anonymous allowed for browse; [Authorize] on write endpoints.
builder.Services.AddAuthentication("Bearer").AddJwtBearer("Bearer", o =>
{
    o.Authority = builder.Configuration["Auth:Authority"];
    o.Audience  = builder.Configuration["Auth:Audience"];
    o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
});
builder.Services.AddAuthorization();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Discover and register all IModule implementations from referenced module assemblies.
var modules = AppDomain.CurrentDomain.GetAssemblies()
    .Concat(new[] { Assembly.Load("Stay.Catalog.Infrastructure"), Assembly.Load("Stay.Booking.Infrastructure") })
    .Distinct()
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
EOF

cat > src/Stay.Api/appsettings.Development.json <<'EOF'
{
  "Auth": { "Authority": "http://localhost:5001", "Audience": "stay-api" },
  "ConnectionStrings": { "Stay": "Host=localhost;Port=5432;Database=stay;Username=bp;Password=bp" }
}
EOF

echo ">> Architecture boundary test"
dotnet new xunit -n Stay.ArchitectureTests -o tests/ArchitectureTests -f "$TF"; rm -f tests/ArchitectureTests/UnitTest1.cs
dotnet add tests/ArchitectureTests package NetArchTest.Rules >/dev/null
dotnet add tests/ArchitectureTests reference \
  src/Modules/Catalog/Stay.Catalog.Infrastructure/Stay.Catalog.Infrastructure.csproj \
  src/Modules/Booking/Stay.Booking.Infrastructure/Stay.Booking.Infrastructure.csproj
cat > tests/ArchitectureTests/ModuleBoundaryTests.cs <<'EOF'
using NetArchTest.Rules;
using Xunit;

public class ModuleBoundaryTests
{
    // A module must not depend on another module's Infrastructure. Generalize per pair.
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
EOF
dotnet sln add tests/ArchitectureTests/Stay.ArchitectureTests.csproj

echo ">> Build"
dotnet build

echo ""
echo "Done. Next:"
echo "  - add remaining modules:  ARI Pricing Payment Search Reviews Promotion Channel Admin NotificationAdapter"
echo "    (uncomment the add_module lines, or call add_module <Name> then reference its Infrastructure from Stay.Api)"
echo "  - dotnet run --project src/Stay.Api  ->  GET /api/v1/catalog/ping"
