using System;
using System.Text.Json.Serialization;

namespace Lorerim.Gui.Models;

/// <summary>
/// Persisted OAuth token state. Field names mirror the Nexus token endpoint response so the
/// engine-facing NexusOAuthState JSON (upstream Wabbajack format) can be built directly.
/// </summary>
public class OAuthTokenState
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; set; } = 3600;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "public openid profile";

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    /// <summary>Unix seconds when we stored the token (drives refresh scheduling).</summary>
    [JsonPropertyName("_saved_at")]
    public long SavedAt { get; set; }

    [JsonPropertyName("user_name")]
    public string? UserName { get; set; }

    [JsonPropertyName("is_premium")]
    public bool IsPremium { get; set; }

    [JsonIgnore]
    public bool IsExpired =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= SavedAt + ExpiresIn - RefreshBufferSeconds;

    /// <summary>15-minute buffer so tokens stay valid through long installs (Jackify's choice).</summary>
    public const long RefreshBufferSeconds = 900;
}

[JsonSerializable(typeof(OAuthTokenState))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public partial class OAuthTokenStateCtx : JsonSerializerContext;
