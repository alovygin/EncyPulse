using System;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;

namespace EncyPulse.Core;

public interface INotificationSender
{
    string Channel { get; }
    Task SendAsync(Notification n, CancellationToken ct);
}

public static class SenderFactory
{
    public static Dictionary<string, INotificationSender> Build(Settings s, Log log)
    {
        var map = new Dictionary<string, INotificationSender>(StringComparer.OrdinalIgnoreCase);
        var c = s.Channels;
        if (c.Ntfy.Enabled && !string.IsNullOrWhiteSpace(c.Ntfy.Topic)) map[ChannelKeys.Ntfy] = new NtfySender(c.Ntfy);
        if (c.Pushover.Enabled && !string.IsNullOrWhiteSpace(c.Pushover.UserKey)) map[ChannelKeys.Pushover] = new PushoverSender(c.Pushover);
        if (c.Relay.Enabled && !string.IsNullOrWhiteSpace(c.Relay.Url)) map[ChannelKeys.Relay] = new RelaySender(c.Relay, s.InstallId);
        if (c.Email.Enabled && !string.IsNullOrWhiteSpace(c.Email.Address) && !string.IsNullOrWhiteSpace(c.Email.SmtpHost)) map[ChannelKeys.Email] = new SmtpEmailSender(c.Email);
        log.Debug($"senders: [{string.Join(",", map.Keys)}]");
        return map;
    }
}

internal static class Http
{
    public static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var c = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("EncyPulse/0.1");
        return c;
    }

    /// <summary>HTTP header values must be ASCII; RFC 2047 encodes anything else (ntfy understands it).</summary>
    public static string HeaderSafe(string value)
    {
        if (value.All(ch => ch < 128 && ch != '\r' && ch != '\n')) return value;
        return "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=";
    }

    public static async Task EnsureOk(HttpResponseMessage r, string what)
    {
        if (r.IsSuccessStatusCode) return;
        var body = "";
        try { body = (await r.Content.ReadAsStringAsync()).Trim(); } catch { }
        if (body.Length > 300) body = body[..300];
        throw new HttpRequestException($"{what} returned {(int)r.StatusCode} {r.ReasonPhrase}{(body.Length > 0 ? ": " + body : "")}");
    }
}

/// <summary>ntfy.sh or a self-hosted ntfy server. iOS and Android apps; Apple Watch through iPhone mirroring.</summary>
public sealed class NtfySender : INotificationSender
{
    private readonly NtfyChannel _c;
    public NtfySender(NtfyChannel c) => _c = c;
    public string Channel => ChannelKeys.Ntfy;

    public async Task SendAsync(Notification n, CancellationToken ct)
    {
        var token = Secrets.Reveal(_c.AccessToken);
        // ntfy.sh only forwards email copies for authenticated publishers; without a token the whole
        // message would be rejected (error 40053), so the email header is dropped rather than the push.
        var withEmail = !string.IsNullOrWhiteSpace(_c.EmailForward) && !string.IsNullOrWhiteSpace(token);
        using var resp = await PostAsync(n, token, withEmail, ct).ConfigureAwait(false);
        if (withEmail && resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var body = "";
            try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
            if (body.Contains("40053") || body.Contains("email", StringComparison.OrdinalIgnoreCase))
            {
                using var retry = await PostAsync(n, token, false, ct).ConfigureAwait(false);
                await Http.EnsureOk(retry, "ntfy (push only; the email copy was refused by the server)");
                return;
            }
        }
        await Http.EnsureOk(resp, "ntfy");
    }

    private async Task<HttpResponseMessage> PostAsync(Notification n, string token, bool withEmail, CancellationToken ct)
    {
        var url = $"{_c.Server.TrimEnd('/')}/{Uri.EscapeDataString(_c.Topic.Trim())}";
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(n.Body, Encoding.UTF8, "text/plain"),
        };
        req.Headers.TryAddWithoutValidation("Title", Http.HeaderSafe(n.Title));
        req.Headers.TryAddWithoutValidation("Priority", n.Priority > 0 ? "4" : "3");
        req.Headers.TryAddWithoutValidation("Tags", TagFor(n.Kind));
        if (withEmail) req.Headers.TryAddWithoutValidation("Email", _c.EmailForward.Trim());
        if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await Http.Client.SendAsync(req, ct).ConfigureAwait(false);
    }

    private static string TagFor(NotificationKind k) => k switch
    {
        NotificationKind.OperationFailed or NotificationKind.BatchFailed => "x",
        NotificationKind.SimulationCompleted => "movie_camera",
        NotificationKind.Test => "bell",
        _ => "white_check_mark",
    };
}

/// <summary>Pushover: native iOS, Android and Apple Watch apps.</summary>
public sealed class PushoverSender : INotificationSender
{
    private readonly PushoverChannel _c;
    public PushoverSender(PushoverChannel c) => _c = c;
    public string Channel => ChannelKeys.Pushover;

    public async Task SendAsync(Notification n, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["token"] = Secrets.Reveal(_c.AppToken).Trim(),
            ["user"] = Secrets.Reveal(_c.UserKey).Trim(),
            ["title"] = n.Title,
            ["message"] = n.Body,
            ["priority"] = n.Priority > 0 ? "1" : "0",
        };
        using var resp = await Http.Client.PostAsync("https://api.pushover.net/1/messages.json", new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        await Http.EnsureOk(resp, "Pushover");
    }
}

/// <summary>The ENCY Pulse relay contract (POST /v1/events). Rendering happens server-side, but title and body travel too.</summary>
public sealed class RelaySender : INotificationSender
{
    private readonly RelayChannel _c;
    private readonly string _installId;
    public RelaySender(RelayChannel c, string installId) { _c = c; _installId = installId; }
    public string Channel => ChannelKeys.Relay;

    public async Task SendAsync(Notification n, CancellationToken ct)
    {
        var url = _c.Url.TrimEnd('/');
        if (!url.EndsWith("/v1/events", StringComparison.OrdinalIgnoreCase)) url += "/v1/events";
        var payload = new
        {
            eventId = n.Id,
            installId = _installId,
            type = TypeName(n.Kind),
            occurredAt = n.CreatedAt.UtcDateTime,
            project = new { name = n.ProjectName },
            subject = new { name = n.Subject },
            stats = n.Stats,
            title = n.Title,
            body = n.Body,
            priority = n.Priority,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Idempotency-Key", n.Id);
        var key = Secrets.Reveal(_c.ApiKey);
        if (!string.IsNullOrWhiteSpace(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var resp = await Http.Client.SendAsync(req, ct).ConfigureAwait(false);
        await Http.EnsureOk(resp, "relay");
    }

    public static string TypeName(NotificationKind k) => k switch
    {
        NotificationKind.OperationCompleted => "operation.completed",
        NotificationKind.OperationFailed => "operation.failed",
        NotificationKind.ProjectCompleted => "project.completed",
        NotificationKind.BatchFailed => "batch.failed",
        NotificationKind.SimulationCompleted => "simulation.completed",
        NotificationKind.OperationSimulated => "operation.simulated",
        _ => "test",
    };
}

/// <summary>Plain SMTP. No dependencies; good enough for a company mail server or an app password.</summary>
public sealed class SmtpEmailSender : INotificationSender
{
    private readonly EmailChannel _c;
    public SmtpEmailSender(EmailChannel c) => _c = c;
    public string Channel => ChannelKeys.Email;

    public async Task SendAsync(Notification n, CancellationToken ct)
    {
        var from = string.IsNullOrWhiteSpace(_c.From) ? _c.Address : _c.From;
        using var msg = new MailMessage(from.Trim(), _c.Address.Trim())
        {
            Subject = n.Title,
            Body = n.Body + Environment.NewLine + Environment.NewLine + $"— ENCY Pulse on {Environment.MachineName}",
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
        };
        using var client = new SmtpClient(_c.SmtpHost.Trim(), _c.SmtpPort > 0 ? _c.SmtpPort : 587)
        {
            EnableSsl = _c.UseSsl,
            Timeout = 15000,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        var user = _c.SmtpUser?.Trim() ?? "";
        if (user.Length > 0) client.Credentials = new NetworkCredential(user, Secrets.Reveal(_c.SmtpPassword));
        ct.ThrowIfCancellationRequested();
        await client.SendMailAsync(msg, ct).ConfigureAwait(false);
    }
}
