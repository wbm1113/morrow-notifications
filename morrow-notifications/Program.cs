using Microsoft.EntityFrameworkCore;
using MN.BusinessLogic;
using MN.DAL;
using MN.Interfaces;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<TenantSessionContextInterceptor>();
builder.Services.AddScoped<UnscopedTenantQueryInterceptor>();

builder.Services.AddDbContext<AppDbContext>((sp, options) =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=morrow-notifications.db")
           .AddInterceptors(
               sp.GetRequiredService<TenantSessionContextInterceptor>(),
               sp.GetRequiredService<UnscopedTenantQueryInterceptor>()));

builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<IRoutingRuleRepository, RoutingRuleRepository>();
builder.Services.AddScoped<IMessageRepository, MessageRepository>();
builder.Services.AddScoped<IDispatchRepository, DispatchRepository>();
builder.Services.AddScoped<IDispatchOutboxRepository, DispatchOutboxRepository>();

builder.Services.AddSingleton<IEventQueue, InMemoryEventQueue>();
builder.Services.AddSingleton<IDispatchQueue, InMemoryDispatchQueue>();
builder.Services.AddSingleton<IDeadLetterQueue, InMemoryDeadLetterQueue>();

builder.Services.AddSingleton<IRateLimiterService, RateLimiterService>();

builder.Services.AddScoped<IIngestionService, IngestionService>();

builder.Services.AddSingleton<INotificationChannel, SlackChannel>();
builder.Services.AddSingleton<INotificationChannel, TeamsChannel>();

builder.Services.AddSingleton<IMessageDispatcher, MessageDispatcher>();

builder.Services.AddHostedService<EventRoutingProcessorService>();
builder.Services.AddHostedService<DispatchOutboxPublisherService>();
builder.Services.AddHostedService<DeliveryProcessorService>();

var app = builder.Build();

// when moving to redis, would remove this, move to lazy loading pattern
// and delete this block.  i.e., try the cache, if it's missing, read rate
// limit from db, write result to the cache, move on.  probably do per-instance
// semaphore on the DB check so you don't cache stampede.  if you have a lot
// of pods you'd want a distributed way of doing this
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

public partial class Program { }
