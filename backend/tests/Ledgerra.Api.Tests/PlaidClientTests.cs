using System.Net;
using System.Text;
using System.Text.Json;
using Ledgerra.Api.Services.Banking;
using Ledgerra.Domain.Bookkeeping;
namespace Ledgerra.Api.Tests;
public sealed class PlaidClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request); }

    [Theory]
    [InlineData("Sandbox", "sandbox.plaid.com")]
    [InlineData("Production", "production.plaid.com")]
    public async Task SendsOnlyToFixedProviderHostWithVersionHeaderAndServerCredentials(string environment, string host)
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(host, request.RequestUri!.Host); Assert.Equal("https", request.RequestUri.Scheme); Assert.Equal("/link/token/create", request.RequestUri.AbsolutePath);
            Assert.Equal("2020-09-14", request.Headers.GetValues("Plaid-Version").Single());
            var data = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync()); Assert.Equal("server-id", data.GetProperty("client_id").GetString()); Assert.Equal("server-secret", data.GetProperty("secret").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"link_token\":\"public-link\"}", Encoding.UTF8, "application/json") };
        }));
        var response = await new PlaidClient(http).SendAsync(new BankProviderSettings { ClientId = "server-id", Environment = environment }, "server-secret", "/link/token/create", new(), default);
        Assert.Equal("public-link", response.GetProperty("link_token").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PlaidClient(http).SendAsync(new BankProviderSettings(), "secret", "https://other.example/", new(), default));
    }

    [Fact]
    public async Task ErrorMessagesNeverEchoProviderBodyOrCredentials()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error_code\":\"oops access-token-secret\",\"error_message\":\"private-bank-secret\"}") })));
        var error = await Assert.ThrowsAsync<BankProviderException>(() => new PlaidClient(http).SendAsync(new BankProviderSettings(), "server-secret", "/accounts/get", new(), default));
        Assert.Equal("REQUEST_FAILED", error.Code); Assert.DoesNotContain("secret", error.Message);
    }
}
