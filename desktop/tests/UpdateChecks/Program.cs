using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FrpPanel;

void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
void Reject(Action action) { try { action(); } catch (Exception) { return; } throw new Exception("Invalid metadata accepted."); }
string name = "FRP-Panel-Setup-1.3.1-x64.exe";
string url = "https://github.com/Aliww2468/frp-panel/releases/download/v1.3.1/" + name;
byte[] bytes = Encoding.UTF8.GetBytes("test-installer-content");
string hash = Convert.ToHexString(SHA256.HashData(bytes));
string Metadata(string version = "v1.3.1", bool prerelease = false, string? download = null, long? size = null) => JsonSerializer.Serialize(new {
    tag_name = version, draft = false, prerelease, body = "Update notes",
    assets = new[] {
        new { name, browser_download_url = download ?? url, size = size ?? bytes.Length, digest = "sha256:" + hash },
        new { name = name + ".sha256", browser_download_url = url + ".sha256", size = 100L, digest = "" }
    }
});
var current = new Version(1, 3, 0, 0);
var release = UpdateService.ParseRelease(Metadata(), current)!;
Check(release.Version == new Version(1, 3, 1), "New version not detected.");
Check(UpdateService.ParseRelease(Metadata(), new Version(1, 3, 1, 0)) == null, "Same version treated as update.");
Check(UpdateService.ParseRelease(Metadata(), new Version(1, 4, 0)) == null, "Downgrade offered.");
Check(UpdateService.ParseRelease(Metadata(prerelease: true), current) == null, "Prerelease offered.");
Reject(() => UpdateService.ParseRelease(Metadata(version: "v1.4.0-beta"), current));
Reject(() => UpdateService.ParseRelease(Metadata(download: "https://example.com/installer.exe"), current));
Reject(() => UpdateService.ParseRelease(Metadata(size: 1024L * 1024 * 200), current));
Reject(() => UpdateService.ParseChecksum(hash + "  other.exe", name));

string cache = Path.Combine(Path.GetTempPath(), "FrpPanel-UpdateChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(cache);
using (var service = new UpdateService(new FakeHandler(request => {
    string address = request.RequestUri!.AbsoluteUri;
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = address == UpdateService.LatestUrl
        ? new StringContent(Metadata()) : address.EndsWith(".sha256") ? new StringContent(hash + "  " + name + "\n") : new ByteArrayContent(bytes) };
})))
{
    Check((await service.Check(current, CancellationToken.None))!.Version == release.Version, "HTTP release check failed.");
    var file = await service.Download(release, cache, new Progress<int>(), CancellationToken.None);
    Check(File.ReadAllBytes(file.Path).SequenceEqual(bytes), "Downloaded content changed.");
    Check(file.Sha256 == hash, "Checksum changed.");
    File.Delete(file.Path);
}
using (var corrupt = new UpdateService(new FakeHandler(request => new HttpResponseMessage(HttpStatusCode.OK) {
    Content = request.RequestUri!.AbsoluteUri.EndsWith(".sha256") ? new StringContent(hash + "  " + name) : new ByteArrayContent(new byte[bytes.Length])
})))
{
    bool failed = false;
    try { await corrupt.Download(release, cache, new Progress<int>(), CancellationToken.None); }
    catch (InvalidDataException) { failed = true; }
    Check(failed, "Corrupt installer accepted.");
    Check(!Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Any(), "Partial installer retained after failure.");
}
using (var redirect = new UpdateService(new FakeHandler(_ => {
    var response = new HttpResponseMessage(HttpStatusCode.Redirect);
    response.Headers.Location = new Uri("http://example.com/installer.exe");
    return response;
})))
{
    bool failed = false;
    try { await redirect.Check(current, CancellationToken.None); } catch (InvalidDataException) { failed = true; }
    Check(failed, "Untrusted redirect accepted.");
}
using (var cancelled = new UpdateService(new FakeHandler(_ => throw new Exception("Cancelled request reached handler."))))
{
    using var cts = new CancellationTokenSource(); cts.Cancel();
    bool failed = false;
    try { await cancelled.Check(current, cts.Token); } catch (OperationCanceledException) { failed = true; }
    Check(failed, "Cancellation ignored.");
}
// Only empty directories created by this test remain.
foreach (string directory in Directory.GetDirectories(cache)) Directory.Delete(directory);
Directory.Delete(cache);
Console.WriteLine("PASS: version comparison, prerelease/downgrade rejection, trusted URLs, size limits, checksum verification, corrupt download cleanup, redirects and cancellation.");

if (args.Contains("--live"))
{
    using var live = new UpdateService();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var latest = await live.Check(new Version(0, 0, 0), timeout.Token) ?? throw new Exception("Live release not found.");
    string liveCache = Path.Combine(Path.GetTempPath(), "FrpPanel-LiveUpdateCheck-" + Guid.NewGuid().ToString("N"));
    var download = await live.Download(latest, liveCache, new Progress<int>(), timeout.Token);
    Console.WriteLine($"PASS: live GitHub release {latest.Version}, downloaded {latest.Size} bytes and verified SHA-256 {download.Sha256}.");
    File.Delete(download.Path);
    Directory.Delete(Path.GetDirectoryName(download.Path)!);
    Directory.Delete(liveCache);
}

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request));
    }
}
