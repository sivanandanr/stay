using System.Reflection;
using Stay.BuildingBlocks;
using Stay.BuildingBlocks.Outbox;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication("Bearer").AddJwtBearer("Bearer", o =>
{
    o.Authority = builder.Configuration["Auth:Authority"];
    o.Audience  = builder.Configuration["Auth:Audience"];
    o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
});
builder.Services.AddAuthorization(o =>
{
    // Admin/ops endpoints (e.g. host approval). Roles come from admin.role_assignment via
    // RoleClaimsTransformation (§12, P0-B5) — added as role claims, so RequireRole authorizes
    // server-side from the role store (token-carried roles are honored too, additively).
    o.AddPolicy("ops", p => p.RequireRole("ops", "admin"));
    // Distribution partners authenticate via client-credentials and carry the "partner" role (Phase 9).
    o.AddPolicy("partner", p => p.RequireRole("partner"));
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var modules = new[]
    {
        Assembly.Load("Stay.Catalog.Infrastructure"),
        Assembly.Load("Stay.Booking.Infrastructure"),
        Assembly.Load("Stay.Admin.Infrastructure"),
        Assembly.Load("Stay.Payment.Infrastructure"),
        Assembly.Load("Stay.Search.Infrastructure"),
        Assembly.Load("Stay.Guest.Infrastructure"),
        Assembly.Load("Stay.Reviews.Infrastructure"),
        Assembly.Load("Stay.Channel.Infrastructure"),
        Assembly.Load("Stay.Promotion.Infrastructure")
    }
    .SelectMany(a => a.GetTypes())
    .Where(t => typeof(IModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
    .Select(t => (IModule)Activator.CreateInstance(t)!)
    .ToList();

foreach (var m in modules) m.RegisterServices(builder.Services, builder.Configuration);

// Transactional outbox (P0-A6): dispatcher drains each context's outbox_message to Kafka,
// and a demo consumer logs receipt to prove the round-trip end to end.
builder.Services.AddOutbox(o =>
{
    o.ConnectionString = builder.Configuration.GetConnectionString("Stay")
        ?? throw new InvalidOperationException("Missing connection string 'Stay'.");
    o.BootstrapServers = builder.Configuration["Kafka:BootstrapServers"] ?? "localhost:9092";
    o.Topic           = builder.Configuration["Kafka:Topic"] ?? "stay.outbox";
    o.ConsumerGroup   = builder.Configuration["Kafka:ConsumerGroup"] ?? "stay-outbox-consumer";
    // Every writing context's outbox is drained to Kafka so all event pipelines flow
    // (catalog → search/audit; booking → reviews/notifications; payment/ari downstream;
    // reviews → audit). Each schema must have a reviews.outbox_message-shaped table (schema.sql).
    o.Schemas         = ["catalog", "ari", "booking", "payment", "reviews", "channel"];
});
builder.Services.AddOutboxConsumer();

var app = builder.Build();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok("healthy"));
foreach (var m in modules) m.MapEndpoints(app);

app.Run();

// Exposed so WebApplicationFactory<Program> can host the API in integration tests.
public partial class Program;
