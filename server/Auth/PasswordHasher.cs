// =============================================================================
// PasswordHasher.cs — PBKDF2-SHA256 password hashing (no external deps).
//
// Stored format: pbkdf2$<iterations>$<base64 salt>$<base64 hash>
// The iteration count is embedded so it can be raised later without
// invalidating existing hashes.
// =============================================================================

using System.Security.Cryptography;

namespace RiichiServer.Auth
{
    public static class PasswordHasher
    {
        private const int Iterations = 100_000;
        private const int SaltSize   = 16;
        private const int HashSize   = 32;

        public static string Hash(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var hash = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
            return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
        }

        public static bool Verify(string password, string stored)
        {
            try
            {
                var parts = stored.Split('$');
                if (parts.Length != 4 || parts[0] != "pbkdf2") return false;

                int iterations = int.Parse(parts[1]);
                var  salt      = Convert.FromBase64String(parts[2]);
                var  expected  = Convert.FromBase64String(parts[3]);
                var  actual    = Rfc2898DeriveBytes.Pbkdf2(
                    password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch
            {
                return false;
            }
        }
    }
}
