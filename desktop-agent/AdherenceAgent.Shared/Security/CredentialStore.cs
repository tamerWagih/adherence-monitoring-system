namespace AdherenceAgent.Shared.Security;

public class CredentialStore
{
    private const string FileName = "creds.bin";

    public void Save(string workstationId, string apiKey)
    {
        Directory.CreateDirectory(Configuration.PathProvider.BaseDirectory);
        var path = Path.Combine(Configuration.PathProvider.BaseDirectory, FileName);
        var payload = System.Text.Json.JsonSerializer.Serialize(new Creds
        {
            WorkstationId = workstationId,
            ApiKey = apiKey
        });
        var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(
            bytes,
            optionalEntropy: null,
            scope: System.Security.Cryptography.DataProtectionScope.LocalMachine);
        File.WriteAllBytes(path, protectedBytes);
    }

    public (string workstationId, string apiKey)? Load()
    {
        var path = Path.Combine(Configuration.PathProvider.BaseDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                scope: System.Security.Cryptography.DataProtectionScope.LocalMachine);
            var payload = System.Text.Encoding.UTF8.GetString(bytes);
            var creds = System.Text.Json.JsonSerializer.Deserialize<Creds>(payload);
            if (creds == null || string.IsNullOrWhiteSpace(creds.WorkstationId) || string.IsNullOrWhiteSpace(creds.ApiKey))
            {
                return null;
            }
            return (creds.WorkstationId, creds.ApiKey);
        }
        catch
        {
            return null;
        }
    }

    public void Delete()
    {
        var path = Path.Combine(Configuration.PathProvider.BaseDirectory, FileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private class Creds
    {
        public string WorkstationId { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
    }
}

