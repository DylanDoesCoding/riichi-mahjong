// =============================================================================
// IEmailSender.cs — pluggable outbound email for password resets.
//
// Selection (Program.cs):
//   RESEND_API_KEY set   → ResendEmailSender (production)
//   EMAIL_MODE=console   → ConsoleEmailSender (dev — code appears in server log)
//   neither              → null → password reset reports itself unavailable
// =============================================================================

namespace RiichiServer.Auth
{
    public interface IEmailSender
    {
        Task SendAsync(string to, string subject, string body);
    }

    /// <summary>Dev-only sender: writes the mail to stdout instead of sending.</summary>
    public class ConsoleEmailSender : IEmailSender
    {
        public Task SendAsync(string to, string subject, string body)
        {
            Console.WriteLine($"[Email:console] To: {to} | Subject: {subject} | Body: {body}");
            return Task.CompletedTask;
        }
    }

    /// <summary>Sends via the Resend HTTP API (https://resend.com — free tier suffices).</summary>
    public class ResendEmailSender : IEmailSender
    {
        private static readonly HttpClient _http = new();
        private readonly string _apiKey;
        private readonly string _from;

        public ResendEmailSender(string apiKey, string? from)
        {
            _apiKey = apiKey;
            // onboarding@resend.dev works without domain verification (testing);
            // set EMAIL_FROM to a verified domain sender for real traffic.
            _from = string.IsNullOrWhiteSpace(from) ? "onboarding@resend.dev" : from;
        }

        public async Task SendAsync(string to, string subject, string body)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = System.Net.Http.Json.JsonContent.Create(new
            {
                from    = _from,
                to      = new[] { to },
                subject,
                text    = body,
            });

            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Resend returned {(int)resp.StatusCode}: {detail}");
            }
        }
    }
}
