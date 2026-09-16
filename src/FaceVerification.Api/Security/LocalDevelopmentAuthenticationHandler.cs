using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FaceVerification.Api.Security;

public sealed class LocalDevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string AuthenticationSchemeName = "DevelopmentIdentity";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[] { new Claim("sub", "development-user"), new Claim("client_id", "development-client"), new Claim(ClaimTypes.NameIdentifier, "development-user") };
        var identity = new ClaimsIdentity(claims, AuthenticationSchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationSchemeName)));
    }
}
