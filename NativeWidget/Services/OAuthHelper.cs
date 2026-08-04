using System.Net;
using System.Security.Cryptography;
using System.Diagnostics;

namespace NativeWidget.Services;

public record Pkce(string Verifier, string Challenge);

public static class OAuthHelper
{
    public static Pkce MakePkce()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Base64Url(bytes);
        var challenge = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        return new Pkce(verifier, challenge);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // Opens the system browser to authUrl, listens on http://127.0.0.1:port/callback,
    // and returns the "code" query parameter once the redirect arrives.
    public static async Task<string> WaitForAuthCodeAsync(string authUrl, int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/callback/");
        listener.Start();

        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        var context = await listener.GetContextAsync();
        var query = context.Request.QueryString;
        var code = query["code"];
        var error = query["error"];

        var html = error == null
            ? "<h2>Sign-in successful. You can close this tab.</h2>"
            : "<h2>Sign-in failed. You can close this tab.</h2>";
        var buffer = System.Text.Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        context.Response.OutputStream.Close();
        listener.Stop();

        if (error != null) throw new Exception(error);
        return code ?? throw new Exception("no_code");
    }
}
