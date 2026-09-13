using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentFoundry.Desktop;

// One bounded, in-memory ticket table. No URL/cookie token and no persistent credentials.
public sealed class AttachmentTickets
{
    private readonly object gate = new();
    private readonly Dictionary<string, (string Pane, string Generation, DateTimeOffset Expires)> issued = new();
    private bool closed;

    public string Mint(string pane, string generation, DateTimeOffset now)
    {
        TerminalBoundary.Id(pane); TerminalBoundary.Id(generation);
        lock (gate)
        {
            if (closed) throw new InvalidOperationException("Terminal is shutting down");
            foreach (var key in issued.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray()) issued.Remove(key);
            if (issued.Count >= 128) throw new InvalidOperationException("Too many pending attachments");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            issued.Add(token, (pane, generation, now.AddSeconds(20)));
            return token;
        }
    }

    public bool Consume(string token, string pane, string generation, DateTimeOffset now)
    {
        lock (gate)
            return !closed && issued.Remove(token, out var item) && item.Expires > now &&
                   item.Pane == pane && item.Generation == generation;
    }

    public void RevokeAll() { lock (gate) { closed = true; issued.Clear(); } }
}

public static class TerminalBoundary
{
    public static bool WebSocket(string[] hosts, string[] origins, string authority) =>
        hosts.Length == 1 && hosts[0] == authority && origins.Length == 1 && origins[0] == "http://" + authority;

    public static bool Http(string[] hosts, string[] origins, string[] keys, string authority, string secret, string method) =>
        hosts.Length == 1 && hosts[0] == authority && keys.Length == 1 && keys[0].Length == secret.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(keys[0]), Encoding.UTF8.GetBytes(secret)) &&
        (origins.Length == 1 && origins[0] == "http://" + authority || method == "GET" && origins.Length == 0);

    public static bool OwnedUri(string value, string origin, bool terminal = false) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "http" &&
        uri.GetLeftPart(UriPartial.Authority) == origin && uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        (uri.AbsolutePath == "/" || !terminal && uri.AbsolutePath is "/observatory" or "/library" or "/settings");

    public static string Id(string value) => Guid.TryParseExact(value, "D", out var id) && id.ToString() == value
        ? value : throw new ArgumentException("Invalid pane or generation ID");

    public static JsonDocument Parse(byte[] data, string[] fields)
    {
        if (data.Length > 65536) throw new JsonException("Request too large");
        var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 8 });
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Object required");
            var names = document.RootElement.EnumerateObject().Select(x => x.Name).ToArray();
            if (names.Length != fields.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                names.Except(fields, StringComparer.Ordinal).Any()) throw new JsonException("Unexpected or duplicate fields");
            return document;
        }
        catch { document.Dispose(); throw; }
    }
}
