using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LagLens;

public sealed record UpdateCheckResult(Version CurrentVersion, Version LatestVersion, string TagName)
{
    public bool IsUpdateAvailable => LatestVersion > CurrentVersion;
    public string ReleaseUrl => $"{UpdateService.ProjectUrl}/releases/tag/{Uri.EscapeDataString(TagName)}";
}

public sealed class UpdateService
{
    public const string ProjectUrl = "https://github.com/suboy1219/lag-lens";
    private const string LatestReleaseApi = "https://api.github.com/repos/suboy1219/lag-lens/releases/latest";
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient _client;

    public static Version CurrentVersion { get; } = GetCurrentVersion();

    public UpdateService(HttpClient? client = null)
    {
        _client = client ?? SharedClient;
    }

    // A null result means there is no published stable release yet.
    public async Task<UpdateCheckResult?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.ParseAdd($"LagLens/{CurrentVersion}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<Release>(cancellationToken).ConfigureAwait(false);
        if (release == null)
        {
            throw new JsonException("更新信息为空。");
        }

        if (release.Draft || release.Prerelease)
        {
            return null;
        }

        var tag = release.TagName?.Trim();
        if (string.IsNullOrEmpty(tag) || !TryParseVersion(tag, out var version))
        {
            throw new JsonException("无法识别发布版本号。");
        }

        return new UpdateCheckResult(CurrentVersion, version!, tag);
    }

    private static bool TryParseVersion(string value, out Version? version)
    {
        // Release tags use v1.2.3; build metadata does not affect version ordering.
        var number = value.TrimStart('v', 'V').Split('+', 2)[0];
        if (!Version.TryParse(number, out version))
        {
            return false;
        }

        // Version treats missing components as -1, so normalize 1.2.3 and 1.2.3.0.
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        return true;
    }

    private static Version GetCurrentVersion()
    {
        var assembly = typeof(UpdateService).Assembly;
        var value = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (value != null && TryParseVersion(value, out var version))
        {
            return version!;
        }

        return assembly.GetName().Version ?? new Version(1, 0, 0, 0);
    }

    private sealed class Release
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }
    }
}
