using System.Net;
using System.Text;
using Contoso.Enrollments.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Contoso.Enrollments.Tests;

[TestClass]
[TestCategory("ConsumerCompatibility")]
public sealed class ConsumerCompatibilityTests
{
    [TestMethod]
    public async Task ActualProducerSamplesPassThroughReferenceClient()
    {
        var samples = ConsumerSamples.FromEnvironment();
        if (samples is null)
        {
            foreach (var service in new[] { "Students", "Academics" })
            {
                var fixture = service == "Students" ? "student.json" : "course.json";
                var body = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
                using var document = System.Text.Json.JsonDocument.Parse(body);
                await Consume(service, document.RootElement.GetProperty("id").GetInt32(), body, LookupResult.Found);
            }
            return;
        }
        foreach (var sample in samples)
        {
            await Consume(sample.Schema == "Student" ? "Students" : "Academics",
                sample.Body.GetProperty("id").GetInt32(), sample.Body.GetRawText(), LookupResult.Found);
        }
    }

    [TestMethod]
    public async Task RequiredIdentityBreaksFailInRealLookupWithoutEnumeration()
    {
        foreach (var service in new[] { "Students", "Academics" })
        {
            foreach (var body in new[] { """{"renamedId":7}""", """{"id":"7"}""", """{"id":null}""",
                         """{"id":0}""", """{"id":8}""", "[]", "null", "{", "{\"id\":7,\"unused\":\"" + new string('x', 1_048_576) + "\"}" })
            {
                await Consume(service, 7, body, LookupResult.Unavailable);
            }
            // These are the only fields the reference lookup uses; producer details may evolve independently.
            await Consume(service, 7, """{"id":7,"unused":null}""", LookupResult.Found);
        }
    }

    [TestMethod]
    public void ExternalInputConfigurationFailsClosed()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read(null, null));
        Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-enrollments", AppContext.BaseDirectory));
        Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-students", "relative"));
        Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-students", Path.Combine(AppContext.BaseDirectory, "..")));
    }

    [TestMethod]
    public void ExternalFilesRequireCoverageAndBoundedJsonEnvelopes()
    {
        var root = Directory.CreateDirectory(Path.Combine("TestResults", "consumer-samples-" + Guid.NewGuid().ToString("N")));
        var path = Path.Combine(root.FullName, "sample.json");
        try
        {
            Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-students", root.FullName));
            File.WriteAllText(path, """{"schema":"Student","body":{"id":7}}""");
            Assert.HasCount(1, ConsumerSamples.Read("contoso-students", root.FullName));
            Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-academics", root.FullName));
            foreach (var invalid in new[] { "{", "[]", """{"schema":"Student","body":null}""",
                         """{"schema":"Unknown","body":{}}""", """{"schema":"Student","body":{},"url":"https://invalid.test"}""",
                         new string(' ', 1_048_577), """{"schema":"Student","body":{"nested":""" + new string('[', 33) + "0" + new string(']', 33) + "}}" })
            {
                File.WriteAllText(path, invalid);
                Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-students", root.FullName));
            }
            File.WriteAllText(path, """{"schema":"Student","body":{}}""");
            Directory.CreateDirectory(Path.Combine(root.FullName, "nested"));
            Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-students", root.FullName));
            Directory.Delete(Path.Combine(root.FullName, "nested"));
            for (var i = 0; i < 32; i++)
            {
                File.WriteAllText(Path.Combine(root.FullName, $"{i}.json"), """{"schema":"Student","body":{}}""");
            }
            Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-students", root.FullName));
            foreach (var file in root.EnumerateFiles()) { file.Delete(); }
            for (var i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(root.FullName, $"{i}.json"), """{"schema":"Student","body":{}}""".PadRight(900_000));
            }
            Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-students", root.FullName));
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static async Task Consume(string service, int id, string body, LookupResult expected)
    {
        Assert.IsGreaterThan(0, id);
        using var handler = new SampleHandler(body);
        using var client = new HttpClient(handler);
        var credential = new FakeCredential();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{service}:BaseUrl"] = "https://reference.example.test/",
            [$"{service}:Scope"] = $"api://{service.ToLowerInvariant()}/.default"
        }).Build();
        var lookup = new ReferenceLookup(new SampleClients(client), credential, config);
        Assert.AreEqual(expected, await lookup.FindAsync(service, id, CancellationToken.None));
        Assert.HasCount(1, handler.Requests);
        Assert.AreEqual($"/api/v1/{(service == "Students" ? "students" : "courses")}/{id}", handler.Requests.Single());
        CollectionAssert.AreEqual(new[] { $"api://{service.ToLowerInvariant()}/.default" }, credential.Scopes);
    }

    private sealed class SampleClients(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class SampleHandler(string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("reference.example.test", request.RequestUri!.Host);
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("test-token", request.Headers.Authorization?.Parameter);
            Requests.Add(request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
