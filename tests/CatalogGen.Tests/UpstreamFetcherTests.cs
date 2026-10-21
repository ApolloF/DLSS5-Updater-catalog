using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CatalogGen;

namespace CatalogGen.Tests;

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }
}

public class UpstreamFetcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static byte[] Pe(int length = 64)
    {
        var b = new byte[length];
        b[0] = (byte)'M';
        b[1] = (byte)'Z';
        for (var i = 2; i < length; i++) b[i] = (byte)i;
        return b;
    }

    private UpstreamFetcher Fetcher(Func<HttpRequestMessage, HttpResponseMessage> respond, out StubHandler handler)
    {
        handler = new StubHandler(respond);
        return new UpstreamFetcher(new HttpClient(handler), _dir, TimeSpan.Zero);
    }

    private static HttpResponseMessage Ok(byte[] body, string? etag = "\"v1\"")
    {
        var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        if (etag is not null) res.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        res.Content.Headers.LastModified = new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero);
        return res;
    }

    [Fact]
    public async Task A_download_is_hashed_sized_and_kept_in_the_temp_folder()
    {
        var body = Pe(5000);
        var fetcher = Fetcher(_ => Ok(body), out _);

        var r = Assert.IsType<FetchResult.Fetched>(await fetcher.FetchAsync("https://x.test/a.addon64", null, default));

        Assert.Equal(Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(), r.Sha256);
        Assert.Equal(5000, r.Size);
        Assert.Equal("\"v1\"", r.ETag);
        Assert.Equal("Tue, 20 Oct 2026 16:00:00 GMT", r.LastModified);
        Assert.Equal(body, await File.ReadAllBytesAsync(r.Path));
        Assert.StartsWith(_dir, r.Path);
    }

    [Fact]
    public async Task An_html_page_is_rejected_and_leaves_no_file()
    {
        var fetcher = Fetcher(_ => Ok("<!DOCTYPE html><html>Not found</html>"u8.ToArray()), out _);

        var r = Assert.IsType<FetchResult.Failed>(await fetcher.FetchAsync("https://x.test/a.addon64", null, default));

        Assert.Contains("MZ", r.Reason);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_truncated_download_is_rejected()
    {
        var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Pe(100)) };
        res.Content.Headers.ContentLength = 200;
        var fetcher = Fetcher(_ => res, out _);

        var r = await fetcher.FetchAsync("https://x.test/a.addon64", null, default);

        Assert.IsType<FetchResult.Failed>(r);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_previous_build_makes_the_request_conditional_and_a_304_means_unchanged()
    {
        var fetcher = Fetcher(_ => new HttpResponseMessage(HttpStatusCode.NotModified), out var handler);
        var previous = new FileEntry { Sha256 = "aa", ETag = "\"v1\"", LastModified = "Tue, 20 Oct 2026 16:00:00 GMT" };

        var r = Assert.IsType<FetchResult.Unchanged>(await fetcher.FetchAsync("https://x.test/a.addon64", previous, default));

        var req = Assert.Single(handler.Requests);
        Assert.Equal("\"v1\"", req.Headers.IfNoneMatch.Single().ToString());
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero), req.Headers.IfModifiedSince);
        Assert.Equal("\"v1\"", r.ETag);
    }

    [Fact]
    public async Task A_host_that_ignores_conditions_but_reports_the_same_etag_and_size_is_not_downloaded_again()
    {
        var body = Pe(300);
        var read = false;
        var fetcher = Fetcher(_ =>
        {
            var res = Ok(body);
            res.Content = new StreamContent(new CallbackStream(body, () => read = true));
            res.Content.Headers.ContentLength = body.Length;
            return res;
        }, out _);
        var previous = new FileEntry { Sha256 = "aa", ETag = "\"v1\"", Size = body.Length };

        var r = await fetcher.FetchAsync("https://x.test/a.addon64", previous, default);

        Assert.IsType<FetchResult.Unchanged>(r);
        Assert.False(read);
    }

    [Fact]
    public async Task A_changed_etag_downloads_the_new_build()
    {
        var fetcher = Fetcher(_ => Ok(Pe(300), "\"v2\""), out _);
        var previous = new FileEntry { Sha256 = "aa", ETag = "\"v1\"", Size = 300 };

        var r = await fetcher.FetchAsync("https://x.test/a.addon64", previous, default);

        Assert.Equal("\"v2\"", Assert.IsType<FetchResult.Fetched>(r).ETag);
    }

    [Fact]
    public async Task Server_errors_are_retried_and_client_errors_are_not()
    {
        var calls = 0;
        var flaky = Fetcher(_ => ++calls < 3 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Ok(Pe()), out _);
        Assert.IsType<FetchResult.Fetched>(await flaky.FetchAsync("https://x.test/a", null, default));
        Assert.Equal(3, calls);

        calls = 0;
        var gone = Fetcher(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.NotFound); }, out _);
        var r = Assert.IsType<FetchResult.Failed>(await gone.FetchAsync("https://x.test/a", null, default));
        Assert.Equal("HTTP 404", r.Reason);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_network_error_after_every_attempt_is_a_failed_result_not_an_exception()
    {
        var fetcher = Fetcher(_ => throw new HttpRequestException("connection reset"), out var handler);

        var r = Assert.IsType<FetchResult.Failed>(await fetcher.FetchAsync("https://x.test/a", null, default));

        Assert.Contains("connection reset", r.Reason);
        Assert.Equal(3, handler.Requests.Count);
    }

    private sealed class CallbackStream(byte[] data, Action onRead) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            onRead();
            return base.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
