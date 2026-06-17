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
