using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Lorerim.Gui.Models;

namespace Lorerim.Gui.Services.Nexus;

/// <summary>
/// Nexus Mods OAuth 2.0 PKCE flow (port of Jackify's nexus_oauth_service.py). The redirect
/// arrives via the jackify:// protocol handler and OAuthCallbackListener.
/// </summary>
public class NexusOAuthService(
    OAuthCallbackListener listener,
    ProtocolHandlerRegistrar registrar,
    NexusTokenStore tokenStore,
    LogService log
) : IDisposable
{
    public const string ClientId = "jackify";
    private const string AuthUrl = "https://users.nexusmods.com/oauth/authorize";
    private const string TokenUrl = "https://users.nexusmods.com/oauth/token";
    private const string UserInfoUrl = "https://users.nexusmods.com/oauth/userinfo";
    private const string Scopes = "public openid profile";
    private const string RedirectUri = "jackify://oauth/callback";
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Runs the full browser flow and persists the token. Returns the token or null.</summary>
    public async Task<OAuthTokenState?> AuthorizeAsync(CancellationToken ct)
    {
        // Off the UI thread: this shells out to xdg tooling that can block for seconds.
        await registrar.EnsureRegisteredAsync();

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["response_type"] = "code";
        query["client_id"] = ClientId;
        query["redirect_uri"] = RedirectUri;
        query["scope"] = Scopes;
        query["state"] = state;
        query["code_challenge"] = challenge;
        query["code_challenge_method"] = "S256";
        var authUrl = $"{AuthUrl}?{query}";

        var tcs = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCallback(Uri uri) => tcs.TrySetResult(uri);
        listener.CallbackReceived += OnCallback;
        try
        {
            OpenBrowser(authUrl);
            log.Append("Browser opened for Nexus authorisation — complete the sign-in there.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(CallbackTimeout);
            Uri callback;
            try
            {
                callback = await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log.Append("Nexus authorisation timed out after 5 minutes.");
                return null;
            }

            var parsed = HttpUtility.ParseQueryString(callback.Query);
            if (parsed["error"] is { } error)
            {
                log.Append($"Nexus authorisation failed: {error}");
                return null;
            }
            if (parsed["state"] != state)
            {
                log.Append("Nexus authorisation state mismatch — possible CSRF; aborting.");
                return null;
            }
            if (parsed["code"] is not { } code)
            {
                log.Append("Nexus authorisation returned no code.");
                return null;
            }

            var token = await ExchangeAsync(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = ClientId,
                    ["redirect_uri"] = RedirectUri,
                    ["code"] = code,
                    ["code_verifier"] = verifier,
                },
                ct
            );
            if (token is null)
            {
                return null;
            }
            await EnrichWithUserInfoAsync(token, ct);
            tokenStore.Save(token);
            log.Append(
                token.UserName is not null
                    ? $"Signed in to Nexus as {token.UserName}{(token.IsPremium ? " (Premium)" : "")}"
                    : "Signed in to Nexus."
            );
            return token;
        }
        finally
        {
            listener.CallbackReceived -= OnCallback;
        }
    }

    /// <summary>Returns a fresh token, refreshing (and persisting) if needed; null if signed out.</summary>
    public async Task<OAuthTokenState?> GetFreshTokenAsync(CancellationToken ct)
    {
        var token = tokenStore.Load();
        if (token?.RefreshToken is null)
        {
            return null;
        }
        if (!token.IsExpired)
        {
            return token;
        }
        var refreshed = await ExchangeAsync(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = token.RefreshToken,
            },
            ct
        );
        if (refreshed is null)
        {
            log.Append("Nexus token refresh failed — sign in again.");
            return null;
        }
        refreshed.UserName = token.UserName;
        refreshed.IsPremium = token.IsPremium;
        tokenStore.Save(refreshed);
        return refreshed;
    }

    private async Task<OAuthTokenState?> ExchangeAsync(
        Dictionary<string, string> form,
        CancellationToken ct
    )
    {
        try
        {
            using var response = await _http.PostAsync(
                TokenUrl,
                new FormUrlEncodedContent(form),
                ct
            );
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                log.Append($"Nexus token endpoint returned {(int)response.StatusCode}.");
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var token = new OAuthTokenState
            {
                AccessToken = root.GetProperty("access_token").GetString(),
                RefreshToken = root.TryGetProperty("refresh_token", out var rt)
                    ? rt.GetString()
                    : null,
                TokenType = root.TryGetProperty("token_type", out var tt)
                    ? tt.GetString() ?? "Bearer"
                    : "Bearer",
                ExpiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt64() : 3600,
                Scope = root.TryGetProperty("scope", out var sc)
                    ? sc.GetString() ?? Scopes
                    : Scopes,
                CreatedAt = root.TryGetProperty("created_at", out var ca)
                    ? ca.GetInt64()
                    : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                SavedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            return token.AccessToken is null ? null : token;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.Append($"Nexus token exchange failed: {e.Message}");
            return null;
        }
    }

    private async Task EnrichWithUserInfoAsync(OAuthTokenState token, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UserInfoUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            using var response = await _http.SendAsync(req, ct);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            token.UserName = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            // userinfo exposes membership_roles: ["member", "premium", ...]
            if (
                root.TryGetProperty("membership_roles", out var roles)
                && roles.ValueKind == JsonValueKind.Array
            )
            {
                foreach (var r in roles.EnumerateArray())
                {
                    if (
                        r.ValueKind == JsonValueKind.String
                        && string.Equals(r.GetString(), "premium", StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        token.IsPremium = true;
                    }
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            // cosmetic only
        }
    }

    private static void OpenBrowser(string url)
    {
        var psi = new ProcessStartInfo { FileName = "xdg-open", UseShellExecute = false };
        psi.ArgumentList.Add(url);
        // AppImage library paths must not leak into the browser process.
        foreach (var v in (string[])["LD_LIBRARY_PATH", "APPIMAGE", "APPDIR", "ARGV0", "OWD"])
        {
            psi.Environment.Remove(v);
        }
        // Dispose immediately: no pipes are held, and the runtime reaps the child on exit.
        Process.Start(psi)?.Dispose();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _http.Dispose();

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
}
