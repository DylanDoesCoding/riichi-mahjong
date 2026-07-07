// =============================================================================
// TokenService.cs — HMAC-SHA256 signed session tokens.
//
// Token format: base64url("v1|accountId|username|expiresUnix") . base64url(hmac)
// The username never contains '|' (enforced by the registration regex).
//
// Key comes from the TOKEN_SIGNING_KEY environment variable. Without it a
// random per-boot key is used, which works but invalidates all sessions on
// every server restart — fine for local dev, set the env var in production.
// =============================================================================

using System.Security.Cryptography;
using System.Text;

namespace RiichiServer.Auth
{
    public class TokenService
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

        private readonly byte[] _key;

        public TokenService(string? configuredKey)
        {
            if (!string.IsNullOrWhiteSpace(configuredKey))
            {
                _key = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
            }
            else
            {
                _key = RandomNumberGenerator.GetBytes(32);
                Console.WriteLine(
                    "[Auth] WARNING: TOKEN_SIGNING_KEY not set — using a random key; " +
                    "logins will not survive a server restart.");
            }
        }

        /// <param name="tokenVersion">The account's current token version. Password
        /// changes bump it, which invalidates every previously issued token.</param>
        public string Create(long accountId, string username, int tokenVersion)
        {
            long expires  = DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds();
            string p64    = B64Url(Encoding.UTF8.GetBytes($"v2|{accountId}|{username}|{expires}|{tokenVersion}"));
            return $"{p64}.{B64Url(Sign(p64))}";
        }

        public bool TryValidate(string token, out long accountId, out string username, out int tokenVersion)
        {
            accountId    = 0;
            username     = "";
            tokenVersion = -1;

            var parts = token.Split('.');
            if (parts.Length != 2) return false;

            byte[] sig;
            string payload;
            try
            {
                sig     = FromB64Url(parts[1]);
                payload = Encoding.UTF8.GetString(FromB64Url(parts[0]));
            }
            catch { return false; }

            if (!CryptographicOperations.FixedTimeEquals(sig, Sign(parts[0]))) return false;

            var fields = payload.Split('|');
            if (fields.Length != 5 || fields[0] != "v2") return false;
            if (!long.TryParse(fields[1], out accountId)) return false;
            if (!long.TryParse(fields[3], out long expires)) return false;
            if (!int.TryParse(fields[4], out tokenVersion)) return false;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires) return false;

            username = fields[2];
            return true;
        }

        private byte[] Sign(string data)
        {
            using var hmac = new HMACSHA256(_key);
            return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        private static string B64Url(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] FromB64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        }
    }
}
