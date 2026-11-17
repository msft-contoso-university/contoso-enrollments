using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace Contoso.Enrollments.Api;

public enum LookupResult { Found, Missing, Unavailable }
public interface IReferenceLookup { Task<LookupResult> FindAsync(string service, int id, CancellationToken cancellationToken); }

public sealed class ReferenceLookup(IHttpClientFactory clients, TokenCredential credential, IConfiguration config) : IReferenceLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict
    };

    public async Task<LookupResult> FindAsync(string service, int id, CancellationToken cancellationToken)
    {
        var baseUrl = config[$"{service}:BaseUrl"]!;
        var scope = config[$"{service}:Scope"]!;
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), $"api/v1/{(service == "Students" ? "students" : "courses")}/{id}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var response = await clients.CreateClient("references").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return LookupResult.Missing;
            if (!response.IsSuccessStatusCode) return LookupResult.Unavailable;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await response.Content.LoadIntoBufferAsync(1_048_576, timeout.Token);
            var document = await response.Content.ReadFromJsonAsync<ReferenceDto>(JsonOptions, timeout.Token);
            return document?.Id == id ? LookupResult.Found : LookupResult.Unavailable;
        }
        catch (Exception ex) when (ex is HttpRequestException or Azure.Identity.CredentialUnavailableException or Azure.Identity.AuthenticationFailedException or System.Text.Json.JsonException or NotSupportedException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return LookupResult.Unavailable;
        }
    }
    private sealed record ReferenceDto(int Id);
}
