using System.Net;
using System.Text;
using System.Text.Json;
using CatalogGen;

namespace CatalogGen.Tests;

public class GitHubMirrorTests : IDisposable
{
    private const string Repo = "ApolloF/DLSS5-Updater-catalog";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cg-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;

    public GitHubMirrorTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "payload");
        File.WriteAllBytes(_file, new byte[1234]);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static HttpResponseMessage Json(HttpStatusCode code, object body) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static object Asset(long id, string name, string state, long size) => new
    {
        id, name, state, size, browser_download_url = $"https://github.com/{Repo}/releases/download/mirror-2026-10/{name}",
    };

    private sealed class Fake
    {
        public bool ReleaseExists;
        public List<object> Assets = [];
        public List<string> Calls { get; } = [];
        public int Uploads => Calls.Count(c => c.StartsWith("POST https://uploads"));

        public HttpResponseMessage Respond(HttpRequestMessage req)
        {
            var url = req.RequestUri!.ToString();
            Calls.Add($"{req.Method} {url}");
            if (req.Method == HttpMethod.Get && url.EndsWith("/releases/tags/mirror-2026-10"))
                return ReleaseExists ? Json(HttpStatusCode.OK, new { id = 77 }) : new HttpResponseMessage(HttpStatusCode.NotFound);
            if (req.Method == HttpMethod.Post && url.EndsWith("/releases")) return Json(HttpStatusCode.Created, new { id = 77 });
            if (req.Method == HttpMethod.Get && url.Contains("/releases/77/assets")) return Json(HttpStatusCode.OK, Assets);
            if (req.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (req.Method == HttpMethod.Post && url.StartsWith("https://uploads.github.com"))
            {
                var name = Uri.UnescapeDataString(url[(url.IndexOf("name=", StringComparison.Ordinal) + 5)..]);
                return Json(HttpStatusCode.Created, Asset(900, name, "uploaded", 1234));
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    private static string Sha => new('c', 64);

    [Fact]
    public void Assets_are_named_by_hash_prefix_with_a_safe_file_name()
    {
        Assert.Equal("cccccccccccccccc-renodx-x.addon64", GitHubMirror.AssetName(Sha, "renodx-x.addon64"));
        Assert.Equal("cccccccccccccccc-a_b_.addon64", GitHubMirror.AssetName(Sha, "a b#.addon64"));
    }

    [Fact]
    public async Task A_missing_monthly_release_is_created_and_the_file_uploaded_under_its_hash_name()
    {
        var fake = new Fake();
        var mirror = new GitHubMirror(new HttpClient(new StubHandler(fake.Respond)), Repo, "tok");

        var url = await mirror.PublishAsync("renodx-x.addon64", Sha, "2026-10", _file, default);

        Assert.Equal($"https://github.com/{Repo}/releases/download/mirror-2026-10/cccccccccccccccc-renodx-x.addon64", url);
        Assert.Contains(fake.Calls, c => c == $"POST https://api.github.com/repos/{Repo}/releases");
        Assert.Equal(1, fake.Uploads);
    }

    [Fact]
    public async Task An_asset_that_is_already_there_is_never_uploaded_again()
    {
        var fake = new Fake { ReleaseExists = true, Assets = [Asset(5, "cccccccccccccccc-renodx-x.addon64", "uploaded", 1234)] };
        var mirror = new GitHubMirror(new HttpClient(new StubHandler(fake.Respond)), Repo, "tok");

        var url = await mirror.PublishAsync("renodx-x.addon64", Sha, "2026-10", _file, default);

        Assert.EndsWith("cccccccccccccccc-renodx-x.addon64", url);
        Assert.Equal(0, fake.Uploads);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("DELETE"));
        Assert.DoesNotContain(fake.Calls, c => c == $"POST https://api.github.com/repos/{Repo}/releases");
    }

    [Fact]
    public async Task A_half_finished_upload_from_an_earlier_run_is_replaced()
    {
        var fake = new Fake { ReleaseExists = true, Assets = [Asset(5, "cccccccccccccccc-renodx-x.addon64", "starter", 0)] };
        var mirror = new GitHubMirror(new HttpClient(new StubHandler(fake.Respond)), Repo, "tok");

        await mirror.PublishAsync("renodx-x.addon64", Sha, "2026-10", _file, default);

        Assert.Contains(fake.Calls, c => c == $"DELETE https://api.github.com/repos/{Repo}/releases/assets/5");
        Assert.Equal(1, fake.Uploads);
    }

    [Fact]
    public async Task Two_publishes_of_the_same_hash_upload_once()
    {
        var fake = new Fake();
        var mirror = new GitHubMirror(new HttpClient(new StubHandler(fake.Respond)), Repo, "tok");

        var first = await mirror.PublishAsync("renodx-x.addon64", Sha, "2026-10", _file, default);
        var second = await mirror.PublishAsync("renodx-x.addon64", Sha, "2026-10", _file, default);

        Assert.Equal(first, second);
        Assert.Equal(1, fake.Uploads);
    }

    [Fact]
    public async Task A_refused_upload_throws_with_the_status()
    {
        var mirror = new GitHubMirror(new HttpClient(new StubHandler(req =>
            req.RequestUri!.Host == "uploads.github.com" ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("nope") }
            : new Fake { ReleaseExists = true }.Respond(req))), Repo, "tok");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => mirror.PublishAsync("renodx-x.addon64", Sha, "2026-10", _file, default));

        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task Requests_carry_the_token_and_only_the_token_given()
    {
        var fake = new Fake();
        var handler = new StubHandler(fake.Respond);
        var mirror = new GitHubMirror(new HttpClient(handler), Repo, "tok123");

        await mirror.PublishAsync("renodx-x.addon64", Sha, "2026-10", _file, default);

        Assert.All(handler.Requests, r => Assert.Equal("Bearer tok123", r.Headers.Authorization!.ToString()));
    }

    [Fact]
    public async Task Licences_come_from_the_repo_api_once_per_repo_and_noassertion_counts_as_none()
    {
        var calls = 0;
        var handler = new StubHandler(req =>
        {
            calls++;
            return req.RequestUri!.AbsolutePath.EndsWith("/gone/repo") ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : req.RequestUri.AbsolutePath.EndsWith("/some/other") ? Json(HttpStatusCode.OK, new { license = new { spdx_id = "NOASSERTION" } })
                : req.RequestUri.AbsolutePath.EndsWith("/no/license") ? Json(HttpStatusCode.OK, new { license = (object?)null })
                : Json(HttpStatusCode.OK, new { license = new { spdx_id = "MIT" } });
        });
        var licences = new GitHubLicences(new HttpClient(handler), null);

        Assert.Equal("MIT", await licences.SpdxAsync("clshortfuse/renodx", default));
        Assert.Equal("MIT", await licences.SpdxAsync("clshortfuse/renodx", default));
        Assert.Null(await licences.SpdxAsync("gone/repo", default));
        Assert.Null(await licences.SpdxAsync("some/other", default));
        Assert.Null(await licences.SpdxAsync("no/license", default));
        Assert.Equal(4, calls);
        Assert.All(handler.Requests, r => Assert.Null(r.Headers.Authorization));
    }

    [Fact]
    public async Task A_rate_limited_licence_lookup_throws_so_the_previous_licence_is_kept()
    {
        var licences = new GitHubLicences(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))), null);

        await Assert.ThrowsAsync<HttpRequestException>(() => licences.SpdxAsync("a/b", default));
    }

    [Fact]
    public async Task The_licence_file_gives_spdx_link_and_text_and_is_looked_up_once_per_repository()
    {
        var calls = 0;
        var text = Convert.ToBase64String(Encoding.UTF8.GetBytes("MIT License\n\nCopyright (c) 2024 Jane\n"));
        var handler = new StubHandler(req =>
        {
            calls++;
            return req.RequestUri!.AbsolutePath.EndsWith("/gone/repo/license")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json(HttpStatusCode.OK, new
                {
                    html_url = "https://github.com/clshortfuse/renodx/blob/main/LICENSE",
                    content = text.Insert(8, "\n"),
                    license = new { spdx_id = "MIT" },
                });
        });
        var licences = new GitHubLicences(new HttpClient(handler), null);

        var notice = await licences.NoticeAsync("clshortfuse/renodx", default);
        await licences.NoticeAsync("clshortfuse/renodx", default);

        Assert.Equal("MIT", notice!.Spdx);
        Assert.Equal("https://github.com/clshortfuse/renodx/blob/main/LICENSE", notice.Url);
        Assert.Equal(["Copyright (c) 2024 Jane"], MirrorNotes.CopyrightLines(notice.Text));
        Assert.Null(await licences.NoticeAsync("gone/repo", default));
        Assert.Equal(2, calls);
        Assert.EndsWith("/repos/clshortfuse/renodx/license", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    private sealed class NotesFake(string? body)
    {
        public List<string> Calls { get; } = [];
        public string? Patched;

        public HttpResponseMessage Respond(HttpRequestMessage req)
        {
            Calls.Add($"{req.Method} {req.RequestUri}");
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/releases/tags/mirror-2026-10"))
                return Json(HttpStatusCode.OK, new { id = 77, body });
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/releases/tags/mirror-2026-11"))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (req.Method == HttpMethod.Patch && req.RequestUri!.AbsolutePath.EndsWith("/releases/77"))
            {
                Patched = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result).RootElement.GetProperty("body").GetString();
                return Json(HttpStatusCode.OK, new { id = 77 });
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    [Fact]
    public async Task The_notes_are_patched_into_the_release_and_its_intro_paragraph_stays()
    {
        var fake = new NotesFake("Edited intro.\r\n\r\nold block");
        var mirror = new GitHubMirror(new HttpClient(new StubHandler(fake.Respond)), Repo, "tok");
        var body = MirrorNotes.Build([new RepoNotes("a/x", "MIT", null, ["Copyright (c) 1"], ["f.addon64"])]);

        await mirror.SetNotesAsync("2026-10", body, default);

        Assert.StartsWith("Edited intro.\n\n### a/x", fake.Patched);
        Assert.Contains("  - f.addon64", fake.Patched);
    }

    [Fact]
    public async Task Notes_that_are_already_there_are_not_written_again()
    {
        var body = MirrorNotes.Build([new RepoNotes("a/x", "MIT", null, [], ["f.addon64"])]);
        var fake = new NotesFake(body.Replace("\n", "\r\n"));
        var mirror = new GitHubMirror(new HttpClient(new StubHandler(fake.Respond)), Repo, "tok");

        await mirror.SetNotesAsync("2026-10", body, default);

        Assert.Null(fake.Patched);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("PATCH"));
    }

    [Fact]
    public async Task Notes_for_a_release_that_does_not_exist_fail_without_creating_one()
    {
        var fake = new NotesFake(null);
        var mirror = new GitHubMirror(new HttpClient(new StubHandler(fake.Respond)), Repo, "tok");

        await Assert.ThrowsAsync<HttpRequestException>(() => mirror.SetNotesAsync("2026-11", MirrorNotes.Intro, default));

        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("POST"));
    }
}
