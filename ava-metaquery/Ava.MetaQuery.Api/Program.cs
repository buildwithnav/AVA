using Ava.MetaQuery.Business.Services;
using Ava.MetaQuery.Data;
using Codex.Main;
using Codex.Web;
using Microsoft.EntityFrameworkCore;





var builder = WebApplication.CreateBuilder(args);

var configuration = builder.Configuration;
IWebHostEnvironment environment = builder.Environment;

var codexWebInit = new CodexWebInit<MetaQueryContext>(configuration);

// Add services to the container.
// Make sure to replace the APP_USER with the new App name that is set up in the DB using ACCTS_SA.PKG_OC_ACCTS_MGMT.CreateAppUser
// Resolve the Oracle app connection string via CDS.Codex. In local/degraded
// environments (no Oracle/Codex configured) it may be unavailable — in that case
// we log a warning and continue so the service still boots and serves /health.
// Oracle-backed endpoints will fail until a real connection string is provided.
string? connectionString = null;
try
{
    connectionString = Core.GetAppConnectionString("APP_USER");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[Ava.MetaQuery] Could not resolve Oracle app connection string: {ex.Message}");
}

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("[Ava.MetaQuery] APP_USER connection string not found — starting in DEGRADED mode " +
        "(Oracle-backed endpoints will fail; /health remains available).");
    connectionString = "Data Source=UNCONFIGURED;User Id=UNCONFIGURED;Password=UNCONFIGURED;";
}

builder.Services.AddDbContext<MetaQueryContext>(options => options.UseOracle(connectionString));
builder.Services.AddAutoMapper(typeof(MetaQueryService).Assembly);
builder.Services.AddAutoMapper(typeof(Program)); // Register AutoMapper
builder.Services.AddTransient<IMetaQueryService, MetaQueryService>();
builder.Services.AddTransient<ISqlExecutionService, SqlExecutionService>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(); 
codexWebInit.ConfigureServices(builder.Services);

var app = builder.Build();
codexWebInit.Configure(app, environment);

// Configure the HTTP request pipeline.    
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(x => x.SwaggerEndpoint("/swagger/v1/swagger.json", "CDS .Net 9 API Template v1"));
}

app.UseHttpsRedirection();
app.UseAuthorization();

// Lightweight liveness endpoint (no auth, no DB) so the AVA launcher and other
// services can confirm this API is up — consistent with the rest of the stack.
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ava-metaquery" }))
   .AllowAnonymous();

app.MapControllers();
app.Run();
