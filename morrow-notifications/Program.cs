using Microsoft.EntityFrameworkCore;
using MN.DAL;
using MN.Dispatching;
using MN.Ingestion;
using MN.Interfaces;
using MN.Processing;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

// Tenant context (scoped — holds the current request's tenant for EF query filters)
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<TenantSessionContextInterceptor>();
builder.Services.AddScoped<UnscopedTenantQueryInterceptor>();

// Database
builder.Services.AddDbContext<AppDbContext>((sp, options) =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=morrow-notifications.db")
           .AddInterceptors(
               sp.GetRequiredService<TenantSessionContextInterceptor>(),
               sp.GetRequiredService<UnscopedTenantQueryInterceptor>()));

// Repositories
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<IRoutingRuleRepository, RoutingRuleRepository>();
builder.Services.AddScoped<IMessageRepository, MessageRepository>();

// Queue + Dead Letter (singleton — shared channel across the app lifetime)
builder.Services.AddSingleton<IMessageQueue, InMemoryMessageQueue>();
builder.Services.AddSingleton<IDeadLetterQueue, InMemoryDeadLetterQueue>();

// Rate limiting (singleton — holds per-tenant limiter state)
builder.Services.AddSingleton<IRateLimiterService, RateLimiterService>();

// Ingestion
builder.Services.AddScoped<IIngestionService, IngestionService>();

// Dispatch channels (all INotificationChannel impls — dispatcher picks by ChannelType string)
builder.Services.AddSingleton<INotificationChannel, SlackChannel>();
builder.Services.AddSingleton<INotificationChannel, TeamsChannel>();

// Dispatcher (singleton — stateless, holds only the channel dictionary)
builder.Services.AddSingleton<IMessageDispatcher, MessageDispatcher>();

// Background processor
builder.Services.AddHostedService<NotificationProcessorService>();

var app = builder.Build();

// Ensure DB is created and seed rate limiters for existing tenants
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    var rateLimiter = app.Services.GetRequiredService<IRateLimiterService>();
    var tenants = db.Tenants.Where(t => t.IsActive).ToList();
    foreach (var tenant in tenants)
        rateLimiter.ConfigureTenant(tenant.Id, tenant.RateLimitPerMinute);
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.MapControllers();
app.Run();

// Exposed for WebApplicationFactory<Program> in MN.Tests.
public partial class Program { }
