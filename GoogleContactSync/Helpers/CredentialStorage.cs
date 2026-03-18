// Helpers/CredentialStorage.cs
// Stores OAuth2 refresh token + ClientId/ClientSecret in PasswordVault.

using Windows.Security.Credentials;

namespace GoogleContactSync.Helpers
{
    public static class CredentialStorage
    {
        private const string ResourceName = "GoogleContactSync";
        private const string TokenUser    = "RefreshToken";

        public static void SaveToken(string refreshToken)
        {
            var vault = new PasswordVault();
            try
            {
                var old = vault.FindAllByResource(ResourceName);
                foreach (var c in old) vault.Remove(c);
            }
            catch { }
            vault.Add(new PasswordCredential(ResourceName, TokenUser, refreshToken));
        }

        public static string LoadToken()
        {
            try
            {
                var vault = new PasswordVault();
                var cred  = vault.FindAllByResource(ResourceName)?[0];
                if (cred == null) return null;
                cred.RetrievePassword();
                return cred.Password;
            }
            catch { return null; }
        }

        public static void DeleteToken()
        {
            try
            {
                var vault = new PasswordVault();
                var creds = vault.FindAllByResource(ResourceName);
                foreach (var c in creds) vault.Remove(c);
            }
            catch { }
        }

        public static bool HasToken() => LoadToken() != null;
    }
}
