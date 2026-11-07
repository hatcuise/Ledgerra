using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ledgerra.Domain.Bookkeeping;
namespace Ledgerra.Api.Services.Banking;

public interface IPlaidClient
{
    Task<JsonElement> SendAsync(BankProviderSettings settings, string secret, string path, Dictionary<string, object?> body, CancellationToken ct);
}
public sealed class BankProviderException(string code) : Exception($"Bank provider: {code}. Try reconnecting if bank authentication has expired.")
{
    public string Code { get; } = code;
}
public sealed class PlaidClient(HttpClient http) : IPlaidClient
{
    public async Task<JsonElement> SendAsync(BankProviderSettings settings, string secret, string path, Dictionary<string, object?> body, CancellationToken ct)
    {
        string[] paths = ["/link/token/create", "/item/public_token/exchange", "/accounts/get", "/transactions/sync", "/item/remove"];
        if (!paths.Contains(path)) throw new InvalidOperationException("Unsupported bank provider operation.");
        var host = settings.Environment switch { "Sandbox" => "https://sandbox.plaid.com", "Production" => "https://production.plaid.com", _ => throw new InvalidOperationException("Invalid bank environment.") };
        body = new Dictionary<string, object?>(body) { ["client_id"] = settings.ClientId, ["secret"] = secret };
        using var message = new HttpRequestMessage(HttpMethod.Post, host + path) { Content = JsonContent.Create(body) };
        message.Headers.Add("Plaid-Version", "2020-09-14");
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(bytes, ct)) > 0)
        { if (buffer.Length + count > 2 * 1024 * 1024) throw new BankProviderException("RESPONSE_TOO_LARGE"); await buffer.WriteAsync(bytes.AsMemory(0, count), ct); }
        JsonDocument document;
        try { document = JsonDocument.Parse(buffer.ToArray()); }
        catch (JsonException) { throw new BankProviderException("INVALID_RESPONSE"); }
        using (document)
        {
            if (!response.IsSuccessStatusCode)
            {
                var code = document.RootElement.TryGetProperty("error_code", out var value) ? value.GetString() : null;
                throw new BankProviderException(code is not null && Regex.IsMatch(code, "^[A-Z0-9_]{1,80}$") ? code : "REQUEST_FAILED");
            }
            return document.RootElement.Clone();
        }
    }
}
