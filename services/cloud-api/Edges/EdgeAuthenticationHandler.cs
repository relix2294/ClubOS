using System.Security.Claims;
using System.Text.Encodings.Web;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClubOS.CloudApi.Edges;

/// <summary>
/// Аутентификация Edge: <c>Authorization: ClubOS-Sig &lt;jws&gt;</c>, подписанный приватным ключом Edge.
/// Проверяются: сертификат Edge выдан нашим dev CA и не истёк, CN = edgeId, подпись, срок, jti (anti-replay).
/// </summary>
public sealed class EdgeAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ClubOsDbContext db,
    CloudTokenValidator validator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "EdgeSignature";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrEmpty(header) || !header.StartsWith(SignedToken.Scheme + " ", StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header[(SignedToken.Scheme.Length + 1)..].Trim();
        if (!SignedToken.TryReadKeyId(token, out var edgeId))
        {
            return AuthenticateResult.Fail("malformed edge token");
        }

        var edge = await db.Edges.AsNoTracking().SingleOrDefaultAsync(x => x.Id == edgeId, Context.RequestAborted);
        if (edge is null)
        {
            return AuthenticateResult.Fail("unknown edge");
        }

        var result = validator.Validator.Validate(token, edge.CertificatePem, SignedToken.AudienceCloud,
            DevCertificateAuthority.RoleEdge);
        if (!result.Success)
        {
            Logger.LogWarning("Edge {EdgeId} auth rejected: {Reason}", edgeId, result.Error);
            return AuthenticateResult.Fail(result.Error ?? "invalid edge token");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(EdgeContext.EdgeIdClaim, edge.Id),
            new Claim(StaffContext.TenantClaim, edge.TenantId),
            new Claim(EdgeContext.LocationClaim, edge.LocationId)
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

/// <summary>Singleton-обёртка валидатора: держит кэш jti между запросами.</summary>
public sealed class CloudTokenValidator(DevCertificateAuthority ca, TimeProvider time)
{
    public SignedTokenValidator Validator { get; } = new(ca.Certificate, time);
}
