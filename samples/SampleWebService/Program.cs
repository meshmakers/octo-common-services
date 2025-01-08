using Meshmakers.Octo.Services.Swagger.Configuration;
using NLog;
using NLog.Web;
using SampleWebService.Configuration;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

// NLog: setup the logger first to catch all errors
var nLogFactory = LogManager.Setup().RegisterNLogWeb().LoadConfigurationFromFile("nlog.config").LogFactory;
var logger = nLogFactory.GetCurrentClassLogger();

try
{
    logger.Debug("init main");
    
    var builder = WebApplication.CreateBuilder(args);
    
    // NLog: Setup NLog for Dependency injection
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Trace);
    builder.Host.UseNLog();

// Add services to the container.
    builder.Services.ConfigureOptions<ConfigureOctoOpenApiOptions>();

    builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
    builder.Services.AddOctoApiVersioningAndDocumentation()
        .AddVersion()
        .AddVersion("v2");

    var app = builder.Build();

    app.UseHttpsRedirection();

    app.UseAuthorization();

    app.UseOctoApiVersioningAndDocumentation();

    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    //NLog: catch setup errors
    logger.Error(ex, "Stopped program because of exception");
    throw;
}
finally
{
    // Ensure to flush and stop internal timers/threads before application-exit (Avoid segmentation fault on Linux)
    LogManager.Shutdown();
}