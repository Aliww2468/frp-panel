using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FrpPanel;

internal sealed record AppRelease(Version Version, string Notes, string FileName, Uri DownloadUrl, Uri ChecksumUrl, long Size, string? Digest);
internal sealed record DownloadedUpdate(AppRelease Release, string Path, string Sha256);

internal sealed class UpdateService : IDisposable
{
    internal const string LatestUrl = "https://api.github.com/repos/Aliww2468/frp-panel/releases/latest";
    const string DownloadRoot = "https://github.com/Aliww2468/frp-panel/releases/download/";
    const long MaxInstallerSize = 128 * 1024 * 1024;
    readonly HttpClient client;

    public UpdateService(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FrpPanel-Updater/1.3");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    internal static AppRelease? ParseRelease(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..], out var version))
            throw new InvalidDataException("更新版本信息不正确。");
        if (version <= new Version(current.Major, current.Minor, Math.Max(0, current.Build))) return null;
        string name = $"FRP-Panel-Setup-{version}-x64.exe";
        JsonElement Asset(string expected) => root.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == expected);
        var installer = Asset(name);
        var checksum = Asset(name + ".sha256");
        Uri Url(JsonElement asset, string expected)
        {
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (url != DownloadRoot + tag + "/" + expected) throw new InvalidDataException("更新下载地址不正确。");
            return new Uri(url);
        }
        long size = installer.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaxInstallerSize) throw new InvalidDataException("安装包大小不正确。");
        string? digest = installer.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
        if (digest != null && !Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}$")) throw new InvalidDataException("更新校验信息不正确。");
        string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new AppRelease(version, notes[..Math.Min(notes.Length, 8000)], name, Url(installer, name), Url(checksum, name + ".sha256"), size, digest);
    }

    async Task<HttpResponseMessage> Get(Uri url, CancellationToken token)
    {
        for (int redirects = 0; redirects < 6; redirects++)
        {
            if (url.Scheme != "https" || !new[] { "api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com" }.Contains(url.Host))
                throw new InvalidDataException("更新服务器重定向地址不受支持。");
            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location != null)
            {
                url = new Uri(url, response.Headers.Location);
                response.Dispose();
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var code = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException(code == HttpStatusCode.Forbidden ? "GitHub 暂时限制了请求，请稍后重试。" : $"更新服务器返回 {(int)code}，请稍后重试。");
            }
            return response;
        }
        throw new HttpRequestException("更新服务器重定向次数过多。");
    }

    async Task<string> ReadText(Uri url, int maximum, CancellationToken token)
    {
        using var response = await Get(url, token);
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(chunk, token)) != 0)
        {
            if (buffer.Length + count > maximum) throw new InvalidDataException("更新信息超过允许大小。");
            buffer.Write(chunk, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\ufeff');
    }

    public async Task<AppRelease?> Check(Version current, CancellationToken token) =>
        ParseRelease(await ReadText(new Uri(LatestUrl), 256 * 1024, token), current);

    internal static string ParseChecksum(string text, string name)
    {
        var match = Regex.Match(text.Trim(), @"^([0-9a-fA-F]{64})\s+\*?" + Regex.Escape(name) + "$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new InvalidDataException("安装包校验文件无效。");
        return match.Groups[1].Value.ToUpperInvariant();
    }

    public async Task<DownloadedUpdate> Download(AppRelease release, string cache, IProgress<int> progress, CancellationToken token)
    {
        string expected = ParseChecksum(await ReadText(release.ChecksumUrl, 4096, token), release.FileName);
        if (release.Digest != null && !release.Digest[7..].Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装包的两份校验信息不一致。");
        string directory = Path.Combine(cache, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, release.FileName), partial = path + ".part";
        try
        {
            using var response = await Get(release.DownloadUrl, token);
            if (response.Content.Headers.ContentLength is long length && length != release.Size) throw new InvalidDataException("安装包大小与发布信息不一致。");
            using var source = await response.Content.ReadAsStreamAsync(token);
            using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] bytes = new byte[65536];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(bytes, token)) != 0)
                {
                    total += count;
                    if (total > release.Size) throw new InvalidDataException("安装包超过预期大小。");
                    digest.AppendData(bytes, 0, count);
                    await target.WriteAsync(bytes.AsMemory(0, count), token);
                    progress.Report((int)(total * 100 / release.Size));
                }
                if (total != release.Size || !Convert.ToHexString(digest.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包校验失败，请重新下载。");
            }
            File.Move(partial, path);
            return new DownloadedUpdate(release, path, expected);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    public void Dispose() => client.Dispose();
}
