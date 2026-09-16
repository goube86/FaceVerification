using System.Threading.RateLimiting;
using FaceVerification.Api;
using FaceVerification.Api.Security;
using FaceVerification.Infrastructure;
using FaceVerification.Infrastructure.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddControllers();
builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Components ??= new OpenApiComponents();
    document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
    document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "JWT Bearer token issued by the configured authority."
    };
    document.Security ??= [];
    document.Security.Add(new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document, null)] = []
    });
    return Task.CompletedTask;
}));
builder.Services.AddRequestTimeouts(options => options.AddPolicy("inference", TimeSpan.FromSeconds(20)));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("verification", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst("client_id")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var localIdentityEnabled = builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Authentication:EnableDevelopmentIdentity");
if (localIdentityEnabled)
{
    builder.Services.AddAuthentication(LocalDevelopmentAuthenticationHandler.AuthenticationSchemeName)
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, LocalDevelopmentAuthenticationHandler>(LocalDevelopmentAuthenticationHandler.AuthenticationSchemeName, null);
}
else
{
    var authority = builder.Configuration["Authentication:Authority"];
    var audience = builder.Configuration["Authentication:Audience"];
    if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(audience))
        throw new InvalidOperationException("Authentication Authority and Audience are required when the Development identity is disabled.");
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = true;
    });
}
builder.Services.AddAuthorization();

var modelsRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "models"));
builder.Services.AddFaceVerificationInfrastructure(builder.Configuration, modelsRoot);

var app = builder.Build();
_ = app.Services.GetRequiredService<ModelRuntime>();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString("N");
    context.Items["CorrelationId"] = correlationId;
    context.Response.Headers["X-Correlation-ID"] = correlationId;
    await next();
});
app.UseRequestTimeouts();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

if (builder.Configuration.GetValue("OpenApi:Enabled", builder.Environment.IsDevelopment()))
{
    app.MapOpenApi("/openapi/{documentName}.json");
    app.MapScalarApiReference("/scalar/v1", options => options.WithTitle("Face Verification API").WithOpenApiRoutePattern("/openapi/v1.json"));
}

app.Run();

public partial class Program;
