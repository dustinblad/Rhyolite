using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rhyolite.Core.Protocol;

/// <summary>
/// Defines the RBAC role assigned to a human user, AI agent harness, or peer Rhyolite node.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<VaultRole>))]
public enum VaultRole
{
    VaultOwner,
    Admin,
    Editor,
    Reader,
    AiAgentReadWrite,
    AiAgentReadOnly,
    TrustedPeerNode
}

/// <summary>
/// Discrete vault operations governed by <see cref="RhyoliteAuthEngine"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<VaultOperation>))]
public enum VaultOperation
{
    ReadNote,
    WriteNote,
    PatchNote,
    DeleteNote,
    SearchVault,
    QueryGraph,
    ModifyCanvas,
    SyncPeer,
    ManageAuth
}

/// <summary>
/// Supported identity and OAuth2/OIDC Single Sign-On provider types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OAuthProviderType>))]
public enum OAuthProviderType
{
    EmailPassword,
    EnterpriseSsoOidc,
    Google,
    GitHub,
    Microsoft,
    Apple
}

/// <summary>
/// Fine-grained Folder prefix and #tag Access Control List (ACL) scope attached to an account or API token.
/// </summary>
public sealed record AccessScope(
    IReadOnlyList<string> AllowedFolderPrefixes,
    IReadOnlyList<string> DeniedFolderPrefixes,
    IReadOnlyList<string> AllowedTags,
    IReadOnlyList<string> DeniedTags,
    bool AllowDelete = true)
{
    public static AccessScope Unrestricted { get; } = new(
        AllowedFolderPrefixes: [],
        DeniedFolderPrefixes: [],
        AllowedTags: [],
        DeniedTags: [],
        AllowDelete: true);

    public static AccessScope ForAiAgent(
        IEnumerable<string>? allowedFolders = null,
        IEnumerable<string>? deniedFolders = null,
        IEnumerable<string>? deniedTags = null,
        bool allowDelete = false) => new(
            AllowedFolderPrefixes: allowedFolders?.Select(NormalizeFolderPrefix).ToArray() ?? [],
            DeniedFolderPrefixes: deniedFolders?.Select(NormalizeFolderPrefix).ToArray() ?? ["99 — Personal/"],
            AllowedTags: [],
            DeniedTags: deniedTags?.Select(NormalizeTag).ToArray() ?? ["#private", "#secret"],
            AllowDelete: allowDelete);

    internal static string NormalizeFolderPrefix(string prefix)
    {
        var trimmed = prefix.Replace('\\', '/').TrimStart('/');
        return trimmed.Length > 0 && !trimmed.EndsWith('/') ? $"{trimmed}/" : trimmed;
    }

    internal static string NormalizeTag(string tag)
    {
        var trimmed = tag.Trim();
        return trimmed.StartsWith('#') ? trimmed.ToLowerInvariant() : $"#{trimmed.ToLowerInvariant()}";
    }
}

/// <summary>
/// Represents an authenticated Rhyolite user account with PBKDF2-SHA512 credentials or federated OIDC link.
/// </summary>
public sealed record UserAccount(
    string UserId,
    string Email,
    string DisplayName,
    VaultRole Role,
    OAuthProviderType Provider,
    string PasswordHashBase64,
    string PasswordSaltBase64,
    int Pbkdf2Iterations,
    AccessScope Scope,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastLoginAtUtc,
    bool IsActive = true);

/// <summary>
/// Stateless HMAC-SHA256 signed bearer token payload issued by <see cref="RhyoliteAuthEngine"/>.
/// </summary>
public sealed record AuthToken(
    string TokenId,
    string RawBearerToken,
    string UserId,
    string Email,
    VaultRole Role,
    AccessScope Scope,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Stored metadata for a scoped Personal Access Token (PAT) or AI agent API key.
/// Only the SHA-256 digest of the raw key is stored.
/// </summary>
public sealed record ApiKeyCredential(
    string KeyId,
    string Label,
    string OwnerUserId,
    VaultRole Role,
    string KeySha256Hex,
    AccessScope Scope,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool IsRevoked = false);

/// <summary>
/// Returned once upon API key creation; includes the unhashed secret key string.
/// </summary>
public sealed record ApiKeyCreationResult(
    ApiKeyCredential Credential,
    string RawApiKey);

/// <summary>
/// Result of evaluating RBAC and folder/#tag ACLs for an incoming operation.
/// </summary>
public sealed record AccessDecision(
    bool IsAllowed,
    VaultRole EffectiveRole,
    string PrincipalId,
    string Reason);

/// <summary>
/// Descriptor for a registered OAuth2 / OIDC / Enterprise SSO identity provider.
/// </summary>
public sealed record OAuthProviderDescriptor(
    OAuthProviderType ProviderType,
    string DisplayName,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string DefaultScopes,
    bool SupportsPkce,
    bool IsEnabled);

/// <summary>
/// RFC 7636 Proof Key for Code Exchange (PKCE) challenge parameters.
/// </summary>
public sealed record PkceChallenge(
    string CodeVerifier,
    string CodeChallenge,
    string CodeChallengeMethod,
    string State);

/// <summary>
/// Contains the constructed OAuth2 authorization URL and associated PKCE state.
/// </summary>
public sealed record OAuthAuthorizationRequest(
    OAuthProviderType ProviderType,
    string AuthorizationUrl,
    PkceChallenge Pkce,
    string RedirectUri);

/// <summary>
/// Contract for pluggable OAuth2 / OIDC / Enterprise SSO identity providers.
/// </summary>
public interface IIdentityProviderPlugin
{
    OAuthProviderDescriptor Descriptor { get; }

    OAuthAuthorizationRequest CreateAuthorizationRequest(
        string clientId,
        string redirectUri,
        string? domainHint = null);
}

/// <summary>
/// Standard implementation of <see cref="IIdentityProviderPlugin"/> for OIDC & OAuth 2.0 providers with S256 PKCE.
/// </summary>
public sealed class StandardOAuthProviderPlugin(OAuthProviderDescriptor descriptor) : IIdentityProviderPlugin
{
    public OAuthProviderDescriptor Descriptor { get; } = descriptor;

    public OAuthAuthorizationRequest CreateAuthorizationRequest(
        string clientId,
        string redirectUri,
        string? domainHint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        var pkce = RhyoliteAuthEngine.GeneratePkceChallenge();
        var sb = new StringBuilder(Descriptor.AuthorizationEndpoint);
        sb.Append(Descriptor.AuthorizationEndpoint.Contains('?') ? '&' : '?');
        sb.Append("response_type=code");
        sb.Append("&client_id=").Append(Uri.EscapeDataString(clientId));
        sb.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        sb.Append("&scope=").Append(Uri.EscapeDataString(Descriptor.DefaultScopes));
        sb.Append("&state=").Append(Uri.EscapeDataString(pkce.State));

        if (Descriptor.SupportsPkce)
        {
            sb.Append("&code_challenge=").Append(Uri.EscapeDataString(pkce.CodeChallenge));
            sb.Append("&code_challenge_method=").Append(pkce.CodeChallengeMethod);
        }

        if (!string.IsNullOrWhiteSpace(domainHint))
        {
            var hintParam = Descriptor.ProviderType switch
            {
                OAuthProviderType.Google => "hd",
                OAuthProviderType.Microsoft => "domain_hint",
                _ => "login_hint"
            };
            sb.Append('&').Append(hintParam).Append('=').Append(Uri.EscapeDataString(domainHint));
        }

        if (Descriptor.ProviderType == OAuthProviderType.Apple)
        {
            sb.Append("&response_mode=form_post");
        }

        return new OAuthAuthorizationRequest(
            Descriptor.ProviderType,
            sb.ToString(),
            pkce,
            redirectUri);
    }
}

/// <summary>
/// Core Identity, PBKDF2-SHA512 Credential, Stateless HMAC-SHA256 Token, Fine-Grained RBAC/ACL,
/// and Pluggable OAuth2/OIDC SSO Engine for Rhyolite.
/// </summary>
public sealed class RhyoliteAuthEngine
{
    public const int DefaultPbkdf2Iterations = 210_000;
    private const int SaltByteLength = 32;
    private const int DerivedKeyByteLength = 64;

    private readonly byte[] _signingKey;
    private readonly ConcurrentDictionary<string, UserAccount> _accountsByEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, UserAccount> _accountsById = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ApiKeyCredential> _apiKeysByHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ApiKeyCredential> _apiKeysById = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<OAuthProviderType, IIdentityProviderPlugin> _oauthProviders = new();
    private readonly ConcurrentDictionary<string, PkceChallenge> _pendingPkceByState = new(StringComparer.Ordinal);

    public RhyoliteAuthEngine(byte[]? masterSigningKey = null)
    {
        _signingKey = masterSigningKey is { Length: >= 32 }
            ? [.. masterSigningKey]
            : RandomNumberGenerator.GetBytes(32);

        RegisterDefaultOAuthProviders();
    }

    public IReadOnlyCollection<UserAccount> Accounts => [.. _accountsById.Values];

    public IReadOnlyCollection<ApiKeyCredential> ApiKeys => [.. _apiKeysById.Values];

    /// <summary>
    /// Registers the built-in OAuth2 / OIDC / Enterprise SSO provider descriptors.
    /// </summary>
    private void RegisterDefaultOAuthProviders()
    {
        RegisterIdentityProvider(new StandardOAuthProviderPlugin(new OAuthProviderDescriptor(
            OAuthProviderType.EmailPassword,
            "Rhyolite Email & Password",
            AuthorizationEndpoint: "rhyolite://auth/local/login",
            TokenEndpoint: "rhyolite://auth/local/token",
            DefaultScopes: "vault:full",
            SupportsPkce: false,
            IsEnabled: true)));

        RegisterIdentityProvider(new StandardOAuthProviderPlugin(new OAuthProviderDescriptor(
            OAuthProviderType.EnterpriseSsoOidc,
            "Enterprise SSO (OIDC / Okta / Keycloak)",
            AuthorizationEndpoint: "https://sso.enterprise.example.com/oauth2/v1/authorize",
            TokenEndpoint: "https://sso.enterprise.example.com/oauth2/v1/token",
            DefaultScopes: "openid profile email groups offline_access",
            SupportsPkce: true,
            IsEnabled: true)));

        RegisterIdentityProvider(new StandardOAuthProviderPlugin(new OAuthProviderDescriptor(
            OAuthProviderType.Google,
            "Continue with Google",
            AuthorizationEndpoint: "https://accounts.google.com/o/oauth2/v2/auth",
            TokenEndpoint: "https://oauth2.googleapis.com/token",
            DefaultScopes: "openid email profile",
            SupportsPkce: true,
            IsEnabled: true)));

        RegisterIdentityProvider(new StandardOAuthProviderPlugin(new OAuthProviderDescriptor(
            OAuthProviderType.GitHub,
            "Continue with GitHub",
            AuthorizationEndpoint: "https://github.com/login/oauth/authorize",
            TokenEndpoint: "https://github.com/login/oauth/access_token",
            DefaultScopes: "read:user user:email",
            SupportsPkce: true,
            IsEnabled: true)));

        RegisterIdentityProvider(new StandardOAuthProviderPlugin(new OAuthProviderDescriptor(
            OAuthProviderType.Microsoft,
            "Continue with Microsoft (Entra ID)",
            AuthorizationEndpoint: "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
            TokenEndpoint: "https://login.microsoftonline.com/common/oauth2/v2.0/token",
            DefaultScopes: "openid profile email User.Read",
            SupportsPkce: true,
            IsEnabled: true)));

        RegisterIdentityProvider(new StandardOAuthProviderPlugin(new OAuthProviderDescriptor(
            OAuthProviderType.Apple,
            "Sign in with Apple",
            AuthorizationEndpoint: "https://appleid.apple.com/auth/authorize",
            TokenEndpoint: "https://appleid.apple.com/auth/token",
            DefaultScopes: "name email",
            SupportsPkce: true,
            IsEnabled: true)));
    }

    public void RegisterIdentityProvider(IIdentityProviderPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        _oauthProviders[plugin.Descriptor.ProviderType] = plugin;
    }

    public IReadOnlyList<OAuthProviderDescriptor> GetIdentityProviders() =>
        [.. _oauthProviders.Values.OrderBy(p => p.Descriptor.ProviderType).Select(p => p.Descriptor)];

    /// <summary>
    /// Generates an RFC 7636 S256 PKCE code verifier, challenge, and anti-CSRF state token.
    /// </summary>
    public static PkceChallenge GeneratePkceChallenge()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var codeVerifier = Base64UrlEncode(verifierBytes);
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var codeChallenge = Base64UrlEncode(challengeBytes);
        var state = Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

        return new PkceChallenge(codeVerifier, codeChallenge, "S256", state);
    }

    /// <summary>
    /// Initiates an OAuth2 / OIDC authorization flow and caches the PKCE challenge by state.
    /// </summary>
    public OAuthAuthorizationRequest BeginOAuthFlow(
        OAuthProviderType providerType,
        string clientId,
        string redirectUri,
        string? domainHint = null)
    {
        if (!_oauthProviders.TryGetValue(providerType, out var plugin))
        {
            throw new InvalidOperationException($"OAuth provider '{providerType}' is not registered.");
        }

        var request = plugin.CreateAuthorizationRequest(clientId, redirectUri, domainHint);
        _pendingPkceByState[request.Pkce.State] = request.Pkce;
        return request;
    }

    /// <summary>
    /// Verifies the PKCE code verifier against the state challenge and exchanges the federated identity for a Rhyolite <see cref="AuthToken"/>.
    /// </summary>
    public AuthToken CompleteOAuthFlow(
        OAuthProviderType providerType,
        string state,
        string codeVerifier,
        string externalEmail,
        string externalDisplayName,
        VaultRole defaultRole = VaultRole.Editor,
        AccessScope? scope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalEmail);

        if (!_pendingPkceByState.TryRemove(state, out var pkce))
        {
            throw new CryptographicException("Invalid or expired OAuth state parameter.");
        }

        var computedChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(computedChallenge),
                Encoding.ASCII.GetBytes(pkce.CodeChallenge)))
        {
            throw new CryptographicException("PKCE S256 code_verifier validation failed.");
        }

        var normalizedEmail = externalEmail.Trim().ToLowerInvariant();
        var account = _accountsByEmail.GetOrAdd(normalizedEmail, _ =>
        {
            var now = DateTimeOffset.UtcNow;
            var newAccount = new UserAccount(
                UserId: $"usr_{Guid.NewGuid():N}",
                Email: normalizedEmail,
                DisplayName: string.IsNullOrWhiteSpace(externalDisplayName) ? normalizedEmail : externalDisplayName.Trim(),
                Role: defaultRole,
                Provider: providerType,
                PasswordHashBase64: string.Empty,
                PasswordSaltBase64: string.Empty,
                Pbkdf2Iterations: 0,
                Scope: scope ?? AccessScope.Unrestricted,
                CreatedAtUtc: now,
                LastLoginAtUtc: now,
                IsActive: true);
            _accountsById[newAccount.UserId] = newAccount;
            return newAccount;
        });

        return IssueBearerToken(account);
    }

    /// <summary>
    /// Registers a new Email + Password account using PBKDF2-HMAC-SHA512 key derivation.
    /// </summary>
    public UserAccount RegisterAccount(
        string email,
        string displayName,
        string password,
        VaultRole role = VaultRole.VaultOwner,
        AccessScope? scope = null,
        int iterations = DefaultPbkdf2Iterations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var normalizedEmail = email.Trim().ToLowerInvariant();
        if (_accountsByEmail.ContainsKey(normalizedEmail))
        {
            throw new InvalidOperationException($"An account with email '{normalizedEmail}' is already registered.");
        }

        var hashBase64 = HashPassword(password, out var saltBytes, iterations);
        var saltBase64 = Convert.ToBase64String(saltBytes);
        var now = DateTimeOffset.UtcNow;

        var account = new UserAccount(
            UserId: $"usr_{Guid.NewGuid():N}",
            Email: normalizedEmail,
            DisplayName: string.IsNullOrWhiteSpace(displayName) ? normalizedEmail : displayName.Trim(),
            Role: role,
            Provider: OAuthProviderType.EmailPassword,
            PasswordHashBase64: hashBase64,
            PasswordSaltBase64: saltBase64,
            Pbkdf2Iterations: iterations,
            Scope: scope ?? AccessScope.Unrestricted,
            CreatedAtUtc: now,
            LastLoginAtUtc: now,
            IsActive: true);

        _accountsByEmail[normalizedEmail] = account;
        _accountsById[account.UserId] = account;
        return account;
    }

    /// <summary>
    /// Authenticates an Email + Password user in constant time and issues a signed <see cref="AuthToken"/>.
    /// </summary>
    public AuthToken LoginWithEmailPassword(string email, string password, TimeSpan? tokenLifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var normalizedEmail = email.Trim().ToLowerInvariant();
        if (!_accountsByEmail.TryGetValue(normalizedEmail, out var account) || !account.IsActive)
        {
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        if (!VerifyPassword(password, account.PasswordHashBase64, account.PasswordSaltBase64, account.Pbkdf2Iterations))
        {
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        var updated = account with { LastLoginAtUtc = DateTimeOffset.UtcNow };
        _accountsByEmail[normalizedEmail] = updated;
        _accountsById[updated.UserId] = updated;

        return IssueBearerToken(updated, tokenLifetime);
    }

    /// <summary>
    /// Derives a 512-bit PBKDF2-SHA512 password hash with a fresh 256-bit cryptographic salt.
    /// </summary>
    public static string HashPassword(string password, out byte[] salt, int iterations = DefaultPbkdf2Iterations)
    {
        salt = RandomNumberGenerator.GetBytes(SaltByteLength);
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA512,
            DerivedKeyByteLength);
        return Convert.ToBase64String(derived);
    }

    /// <summary>
    /// Verifies a candidate password against a stored PBKDF2-SHA512 hash using constant-time comparison.
    /// </summary>
    public static bool VerifyPassword(string password, string hashBase64, string saltBase64, int iterations)
    {
        if (string.IsNullOrEmpty(hashBase64) || string.IsNullOrEmpty(saltBase64) || iterations <= 0)
        {
            return false;
        }

        var salt = Convert.FromBase64String(saltBase64);
        var expectedHash = Convert.FromBase64String(hashBase64);
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA512,
            expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    /// <summary>
    /// Issues a stateless HMAC-SHA256 signed bearer token for the specified account.
    /// </summary>
    public AuthToken IssueBearerToken(UserAccount account, TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(account);

        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(lifetime ?? TimeSpan.FromHours(12));
        var tokenId = $"tok_{Guid.NewGuid():N}";

        var payload = new SignedTokenClaims(
            TokenId: tokenId,
            UserId: account.UserId,
            Email: account.Email,
            Role: account.Role,
            Scope: account.Scope,
            IssuedAtUnixSeconds: now.ToUnixTimeSeconds(),
            ExpiresAtUnixSeconds: expires.ToUnixTimeSeconds());

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var payloadB64 = Base64UrlEncode(payloadBytes);
        var signatureBytes = HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(payloadB64));
        var signatureB64 = Base64UrlEncode(signatureBytes);

        var rawToken = $"rhy_jwt_{payloadB64}.{signatureB64}";
        return new AuthToken(
            TokenId: tokenId,
            RawBearerToken: rawToken,
            UserId: account.UserId,
            Email: account.Email,
            Role: account.Role,
            Scope: account.Scope,
            IssuedAtUtc: now,
            ExpiresAtUtc: expires);
    }

    /// <summary>
    /// Creates a scoped Personal Access Token (PAT) / API Key for AI agent harnesses or P2P peer nodes.
    /// </summary>
    public ApiKeyCreationResult CreateApiKey(
        string ownerUserId,
        string label,
        VaultRole role,
        AccessScope? scope = null,
        TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(lifetime ?? TimeSpan.FromDays(90));
        var keyId = $"key_{Guid.NewGuid():N}";
        var rawSecret = $"rhy_pat_{Base64UrlEncode(RandomNumberGenerator.GetBytes(32))}";
        var keyHashHex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawSecret)));

        var credential = new ApiKeyCredential(
            KeyId: keyId,
            Label: label.Trim(),
            OwnerUserId: ownerUserId,
            Role: role,
            KeySha256Hex: keyHashHex,
            Scope: scope ?? (role is VaultRole.AiAgentReadWrite or VaultRole.AiAgentReadOnly
                ? AccessScope.ForAiAgent()
                : AccessScope.Unrestricted),
            CreatedAtUtc: now,
            ExpiresAtUtc: expires,
            IsRevoked: false);

        _apiKeysByHash[keyHashHex] = credential;
        _apiKeysById[keyId] = credential;

        return new ApiKeyCreationResult(credential, rawSecret);
    }

    public bool RevokeApiKey(string keyId)
    {
        if (!_apiKeysById.TryGetValue(keyId, out var existing))
        {
            return false;
        }

        var revoked = existing with { IsRevoked = true };
        _apiKeysById[keyId] = revoked;
        _apiKeysByHash[revoked.KeySha256Hex] = revoked;
        return true;
    }

    /// <summary>
    /// Validates either a stateless HMAC-SHA256 bearer token (<c>rhy_jwt_...</c>) or a scoped API key (<c>rhy_pat_...</c>).
    /// </summary>
    public bool TryValidateToken(
        string? rawToken,
        out VaultRole role,
        out AccessScope scope,
        out string principalId,
        out string failureReason)
    {
        role = VaultRole.Reader;
        scope = AccessScope.Unrestricted;
        principalId = string.Empty;

        if (string.IsNullOrWhiteSpace(rawToken))
        {
            failureReason = "Missing authentication token.";
            return false;
        }

        var token = rawToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? rawToken["Bearer ".Length..].Trim()
            : rawToken.Trim();

        var now = DateTimeOffset.UtcNow;

        if (token.StartsWith("rhy_pat_", StringComparison.Ordinal))
        {
            var hashHex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            if (!_apiKeysByHash.TryGetValue(hashHex, out var apiKey))
            {
                failureReason = "Unknown API key.";
                return false;
            }

            if (apiKey.IsRevoked)
            {
                failureReason = "API key has been revoked.";
                return false;
            }

            if (now >= apiKey.ExpiresAtUtc)
            {
                failureReason = "API key has expired.";
                return false;
            }

            role = apiKey.Role;
            scope = apiKey.Scope;
            principalId = apiKey.KeyId;
            failureReason = string.Empty;
            return true;
        }

        if (token.StartsWith("rhy_jwt_", StringComparison.Ordinal))
        {
            var body = token["rhy_jwt_".Length..];
            var dotIndex = body.IndexOf('.');
            if (dotIndex <= 0 || dotIndex >= body.Length - 1)
            {
                failureReason = "Malformed bearer token structure.";
                return false;
            }

            var payloadB64 = body[..dotIndex];
            var sigB64 = body[(dotIndex + 1)..];

            byte[] providedSig;
            try
            {
                providedSig = Base64UrlDecode(sigB64);
            }
            catch (FormatException)
            {
                failureReason = "Invalid bearer token signature encoding.";
                return false;
            }

            var expectedSig = HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(payloadB64));
            if (!CryptographicOperations.FixedTimeEquals(providedSig, expectedSig))
            {
                failureReason = "Bearer token HMAC-SHA256 signature mismatch.";
                return false;
            }

            SignedTokenClaims? claims;
            try
            {
                var payloadBytes = Base64UrlDecode(payloadB64);
                claims = JsonSerializer.Deserialize<SignedTokenClaims>(payloadBytes);
            }
            catch (Exception)
            {
                failureReason = "Malformed bearer token claims payload.";
                return false;
            }

            if (claims is null)
            {
                failureReason = "Empty bearer token claims.";
                return false;
            }

            if (now.ToUnixTimeSeconds() >= claims.ExpiresAtUnixSeconds)
            {
                failureReason = "Bearer token has expired.";
                return false;
            }

            role = claims.Role;
            scope = claims.Scope;
            principalId = claims.UserId;
            failureReason = string.Empty;
            return true;
        }

        failureReason = "Unsupported token prefix. Expected 'rhy_jwt_' or 'rhy_pat_'.";
        return false;
    }

    /// <summary>
    /// Enforces Role-Based Access Control (RBAC), Folder prefix ACLs, and #tag restrictions for a vault operation.
    /// </summary>
    public AccessDecision CheckAccess(
        string? token,
        VaultOperation operation,
        string? notePath = null,
        IEnumerable<string>? tags = null)
    {
        if (!TryValidateToken(token, out var role, out var scope, out var principalId, out var failureReason))
        {
            return new AccessDecision(false, VaultRole.Reader, string.Empty, failureReason);
        }

        if (!IsOperationPermittedByRole(role, operation))
        {
            return new AccessDecision(
                false,
                role,
                principalId,
                $"Role '{role}' is not permitted to perform operation '{operation}'.");
        }

        if (operation == VaultOperation.DeleteNote && !scope.AllowDelete)
        {
            return new AccessDecision(
                false,
                role,
                principalId,
                "Token scope explicitly forbids note deletion (AllowDelete = false).");
        }

        if (!string.IsNullOrWhiteSpace(notePath))
        {
            var normalizedPath = notePath.Replace('\\', '/').TrimStart('/');

            foreach (var deniedPrefix in scope.DeniedFolderPrefixes)
            {
                var normDenied = AccessScope.NormalizeFolderPrefix(deniedPrefix);
                if (normalizedPath.StartsWith(normDenied, StringComparison.OrdinalIgnoreCase))
                {
                    return new AccessDecision(
                        false,
                        role,
                        principalId,
                        $"Note path '{normalizedPath}' is inside denied folder prefix '{normDenied}'.");
                }
            }

            if (scope.AllowedFolderPrefixes.Count > 0)
            {
                var matchedAllowed = false;
                foreach (var allowedPrefix in scope.AllowedFolderPrefixes)
                {
                    var normAllowed = AccessScope.NormalizeFolderPrefix(allowedPrefix);
                    if (normalizedPath.StartsWith(normAllowed, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedAllowed = true;
                        break;
                    }
                }

                if (!matchedAllowed)
                {
                    return new AccessDecision(
                        false,
                        role,
                        principalId,
                        $"Note path '{normalizedPath}' is outside the token's allowed folder prefixes.");
                }
            }
        }

        if (tags is not null)
        {
            var normalizedTags = tags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(AccessScope.NormalizeTag)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var deniedTag in scope.DeniedTags)
            {
                var normDenied = AccessScope.NormalizeTag(deniedTag);
                if (normalizedTags.Contains(normDenied))
                {
                    return new AccessDecision(
                        false,
                        role,
                        principalId,
                        $"Access blocked by denied tag '{normDenied}'.");
                }
            }

            if (scope.AllowedTags.Count > 0 && normalizedTags.Count > 0)
            {
                var hasAllowedTag = scope.AllowedTags
                    .Select(AccessScope.NormalizeTag)
                    .Any(normalizedTags.Contains);

                if (!hasAllowedTag)
                {
                    return new AccessDecision(
                        false,
                        role,
                        principalId,
                        "Note does not contain any of the token's required allowed #tags.");
                }
            }
        }

        return new AccessDecision(true, role, principalId, "Permitted");
    }

    private static bool IsOperationPermittedByRole(VaultRole role, VaultOperation operation) => role switch
    {
        VaultRole.VaultOwner => true,
        VaultRole.Admin => true,
        VaultRole.Editor => operation is not (VaultOperation.SyncPeer or VaultOperation.ManageAuth),
        VaultRole.Reader => operation is VaultOperation.ReadNote or VaultOperation.SearchVault or VaultOperation.QueryGraph,
        VaultRole.AiAgentReadWrite => operation is VaultOperation.ReadNote
            or VaultOperation.WriteNote
            or VaultOperation.PatchNote
            or VaultOperation.DeleteNote
            or VaultOperation.SearchVault
            or VaultOperation.QueryGraph
            or VaultOperation.ModifyCanvas,
        VaultRole.AiAgentReadOnly => operation is VaultOperation.ReadNote
            or VaultOperation.SearchVault
            or VaultOperation.QueryGraph,
        VaultRole.TrustedPeerNode => operation is VaultOperation.ReadNote
            or VaultOperation.WriteNote
            or VaultOperation.PatchNote
            or VaultOperation.DeleteNote
            or VaultOperation.SyncPeer,
        _ => false
    };

    internal static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    internal static byte[] Base64UrlDecode(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    private sealed record SignedTokenClaims(
        string TokenId,
        string UserId,
        string Email,
        VaultRole Role,
        AccessScope Scope,
        long IssuedAtUnixSeconds,
        long ExpiresAtUnixSeconds);
}
