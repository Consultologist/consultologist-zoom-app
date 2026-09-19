using System.Security.Cryptography;
using System.Text.Json;

using Consultologist.ZoomApp.Core.Zoom;

using Microsoft.AspNetCore.DataProtection;

namespace Consultologist.ZoomApp.Zoom;

/// <summary>
/// The verified Zoom meeting context, carried in an encrypted cookie. It is set
/// from the decrypted <c>X-Zoom-App-Context</c> on the Home-URL load — the meeting
/// Zoom itself signed, not a value the browser supplied — and read by the meeting
/// endpoints to bind a request to that meeting. SameSite=None + Secure because the
/// app runs inside Zoom's cross-site iframe.
/// </summary>
public sealed class ZoomContextCookie(IDataProtectionProvider provider)
{
    public const string CookieName = "zoom_ctx";

    private readonly IDataProtector _protector = provider.CreateProtector("Consultologist.ZoomApp.ZoomContext.v1");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Write(HttpContext http, ZoomContext context)
    {
        var payload = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(context, Json));
        http.Response.Cookies.Append(CookieName, Convert.ToBase64String(payload), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.None,
            IsEssential = true,
        });
    }

    public ZoomContext? Read(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            var bytes = _protector.Unprotect(Convert.FromBase64String(value));
            return JsonSerializer.Deserialize<ZoomContext>(bytes, Json);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or JsonException)
        {
            return null;
        }
    }
}
