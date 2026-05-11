using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TodoFloat.Services;

public enum UpdateCheckStatus
{
    UpdateAvailable,
    UpToDate,
    NoRelease,
    Failed
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string CurrentVersion,
    string? LatestVersion = null,
    string? ReleaseName = null,
    string? ReleaseUrl = null,
    string? ErrorMessage = null);

public sealed class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/MrQ-Coding/TodoFloat/releases/latest";
    private static readonly Regex VersionRegex = new(@"\d+(?:\.\d+){0,3}", RegexOptions.Compiled);
    private static readonly HttpClient Client = CreateClient();

    public async Task<UpdateCheckResult> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        var current = GetCurrentVersion();
        var currentText = ToDisplayVersion(current);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await Client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(UpdateCheckStatus.NoRelease, currentText);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    currentText,
                    ErrorMessage: $"GitHub 返回 {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = json.RootElement;
            var tag = ReadString(root, "tag_name");
            var latest = TryParseVersion(tag);
            if (latest is null)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    currentText,
                    ErrorMessage: $"最新 Release 标签无法识别：{tag ?? "(空)"}");
            }

            var latestText = ToDisplayVersion(latest);
            var releaseName = ReadString(root, "name") ?? tag;
            var releaseUrl = ReadString(root, "html_url");

            return CompareVersions(latest, current) > 0
                ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, currentText, latestText, releaseName, releaseUrl)
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate, currentText, latestText, releaseName, releaseUrl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, currentText, ErrorMessage: ex.Message);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TodoFloat");
        return client;
    }

    private static Version GetCurrentVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static Version? TryParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var match = VersionRegex.Match(tag.Trim().TrimStart('v', 'V'));
        if (!match.Success) return null;

        var parts = match.Value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            return Version.TryParse($"{parts[0]}.0", out var single) ? single : null;
        }

        if (parts.Length > 4)
        {
            parts = parts[..4];
        }

        return Version.TryParse(string.Join('.', parts), out var version) ? version : null;
    }

    private static int CompareVersions(Version left, Version right)
    {
        return Normalize(left).CompareTo(Normalize(right));
    }

    private static Version Normalize(Version version)
    {
        return new Version(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }

    private static string ToDisplayVersion(Version version)
    {
        var normalized = Normalize(version);
        return normalized.Revision > 0
            ? normalized.ToString(4)
            : normalized.ToString(3);
    }
}
