using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lorerim.Gui.Models;

namespace Lorerim.Gui.Services.Nexus;

/// <summary>
/// Persists the Nexus OAuth token (mode 600) and builds the engine-facing auth environment:
/// NEXUS_OAUTH_INFO (upstream Wabbajack NexusOAuthState JSON), NEXUS_OAUTH_CLIENT_ID and
/// NEXUS_API_KEY, plus JACKIFY_TOKEN_WRITEBACK for refresh-token rotation.
/// </summary>
public class NexusTokenStore(LogService log)
{
    public OAuthTokenState? Load()
    {
        if (!File.Exists(TokenPath))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(TokenPath),
                OAuthTokenStateCtx.Default.OAuthTokenState
            );
        }
        catch (JsonException)
        {
            log.Append("Nexus token file is corrupt; sign in again.");
            return null;
        }
    }

    public void Save(OAuthTokenState token)
    {
        Directory.CreateDirectory(AppSettings.AppDataPath);
        var json = JsonSerializer.Serialize(token, OAuthTokenStateCtx.Default.OAuthTokenState);
        AtomicFile.WriteAllTextAsync(TokenPath, json).GetAwaiter().GetResult();
        File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Clear()
    {
        try
        {
            File.Delete(TokenPath);
        }
        catch (IOException)
        {
            // nothing to clear
        }
    }

    /// <summary>
    /// Env vars for the engine subprocess. Falls back to a plain API key when OAuth is absent.
    /// </summary>
    public Dictionary<string, string> BuildEngineAuthEnv(string? apiKeyFallback)
    {
        var env = new Dictionary<string, string>
        {
            ["JACKIFY_TOKEN_WRITEBACK"] = WritebackPath,
        };
        var token = Load();
        if (token?.AccessToken is not null && token.RefreshToken is not null)
        {
            // Upstream Wabbajack NexusOAuthState shape; _received_at is a Windows FILETIME.
            var state = new JsonObject
            {
                ["oauth"] = new JsonObject
                {
                    ["access_token"] = token.AccessToken,
                    ["token_type"] = token.TokenType,
                    ["expires_in"] = token.ExpiresIn,
                    ["refresh_token"] = token.RefreshToken,
                    ["scope"] = token.Scope,
                    ["created_at"] = token.CreatedAt,
                    ["_received_at"] = token.SavedAt * 10_000_000L + 116_444_736_000_000_000L,
                },
            };
            env["NEXUS_OAUTH_INFO"] = state.ToJsonString();
            env["NEXUS_OAUTH_CLIENT_ID"] = NexusOAuthService.ClientId;
            env["NEXUS_API_KEY"] = apiKeyFallback ?? token.AccessToken;
        }
        else if (!string.IsNullOrEmpty(apiKeyFallback))
        {
            env["NEXUS_API_KEY"] = apiKeyFallback;
        }
        return env;
    }

    /// <summary>
    /// Apply the engine-written refreshed token after the process exits, preserving
    /// refresh-token rotation. No-op when the file doesn't exist.
    /// </summary>
    public void ApplyWriteback()
    {
        var path = WritebackPath;
        if (!File.Exists(path))
        {
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (
                !doc.RootElement.TryGetProperty("oauth", out var oauth)
                || oauth.ValueKind != JsonValueKind.Object
            )
            {
                return;
            }
            var access = GetString(oauth, "access_token");
            var refresh = GetString(oauth, "refresh_token");
            if (access is null || refresh is null)
            {
                return;
            }
            var existing = Load() ?? new OAuthTokenState();
            existing.AccessToken = access;
            existing.RefreshToken = refresh;
            existing.ExpiresIn = oauth.TryGetProperty("expires_in", out var e)
                && e.TryGetInt64(out var expiresIn)
                    ? expiresIn
                    : existing.ExpiresIn;
            existing.SavedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Save(existing);
            log.Append("Applied refreshed Nexus token from engine writeback.");
        }
        catch (Exception e)
        {
            log.Append($"Failed to apply token writeback: {e.Message}");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public string WritebackPath =>
        Path.Join(AppSettings.AppDataPath, $"oauth_writeback_{Environment.ProcessId}.json");

    public static string TokenPath => Path.Join(AppSettings.AppDataPath, "nexus_oauth.json");
}
