using AbsolutePathHelpers;
using Application.Configuration.Extensions;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Application.Common.Extensions;

namespace Application.ServiceMaster.Services;

public class ServiceManagerService(ILogger<ServiceManagerService> logger, IConfiguration configuration)
{
    private readonly ILogger<ServiceManagerService> _logger = logger;
    private readonly IConfiguration _configuration = configuration;

    private readonly TimeSpan _downloadProgressReportSpan = TimeSpan.FromSeconds(1);

    public async Task<AbsolutePath> Download(
        string name,
        string url,
        string version,
        Func<(AbsolutePath DownloadedFilePath, AbsolutePath ExtractDirectory), Task> extractFactory,
        Func<AbsolutePath, (AbsolutePath Path, string name)[]> executableLinkFactory,
        CancellationToken cancellationToken)
    {
        var nameLower = name.ToLowerInvariant();

        using var _ = _logger.BeginScopeMap(nameof(ServiceManagerService), nameof(Download), new()
        {
            ["ServiceName"] = nameLower,
        });

        _logger.LogInformation("Downloading service {ServiceName}...", nameLower);

        var guid = Guid.NewGuid().ToString();
        var homePath = _configuration.GetHomePath();
        var servicePath = _configuration.GetServicesPath() / nameLower;
        var downloadedPath = _configuration.GetDownloadsPath() / $"{nameLower}-{version}-{guid}";

        downloadedPath.Parent?.CreateDirectory();

        {
            using var client = new HttpClient();

            HttpRequestMessage message = new(HttpMethod.Head, url);
            using var response = await client.SendAsync(message, cancellationToken);
            response.EnsureSuccessStatusCode();
            long? length = response.Content.Headers.ContentLength;

            using var s = await client.GetStreamAsync(url, cancellationToken: cancellationToken);
            using var fs = new FileStream(downloadedPath, FileMode.OpenOrCreate);

            void logProgress(long totalDownloaded)
            {
                var totalDownloadedMB = totalDownloaded / 1024.0 / 1024.0;
                if (length != null)
                {
                    var lengthMB = length.Value / 1024.0 / 1024.0;
                    _logger.LogTrace("Download progress {ProgressTotalDownloadedMB:N2} / {ProgressTotalToDownloadMB:N2} MB...", totalDownloadedMB, lengthMB);
                }
                else
                {
                    _logger.LogTrace("Download progress {ProgressTotalDownloadedMB:N2}...", totalDownloadedMB);
                }
            }

            DateTimeOffset lastProg = DateTimeOffset.MinValue;
            await s.CopyToAsync(fs, progressCallback: downloadedLength =>
            {
                var now = DateTimeOffset.UtcNow;
                if (lastProg + _downloadProgressReportSpan < now)
                {
                    logProgress(downloadedLength);
                    lastProg = now;
                }

            }, cancellationToken: cancellationToken);

            if (length != null)
            {
                logProgress(length.Value);
            }
        }

        var extractDirectory = servicePath / version / guid;

        extractDirectory.CreateDirectory();

        await extractFactory((downloadedPath, extractDirectory));

        foreach (var (linkPath, linkName) in executableLinkFactory(extractDirectory))
        {
            AbsolutePath linkToCreate = homePath / linkName;
            await linkToCreate.Delete(cancellationToken);
            File.CreateSymbolicLink(linkToCreate, linkPath);
        }

        var index = new
        {
            version,
            guid,
        };

        await (servicePath / "index").Write(index, cancellationToken: cancellationToken);

        _logger.LogInformation("Service {ServiceName} downloaded", name);

        return extractDirectory;
    }

    public Task<AbsolutePath> Download(
        string name,
        string url,
        string version,
        Func<(AbsolutePath DownloadedFilePath, AbsolutePath ExtractDirectory), Task> extractFactory,
        CancellationToken cancellationToken)
    {
        return Download(name, url, version, extractFactory, _ => [], cancellationToken);
    }

    public async Task<AbsolutePath?> GetCurrentServicePath(string name, CancellationToken cancellationToken)
    {
        var nameLower = name.ToLowerInvariant();

        var servicePath = _configuration.GetServicesPath() / nameLower;
        var servicePathCurrentIndex = servicePath / "index";

        try
        {
            var indexStr = await servicePathCurrentIndex.ReadAllText(cancellationToken);
            var indexJson = JsonSerializer.Deserialize<JsonDocument>(indexStr)!;
            var currentVersion = indexJson.RootElement.GetProperty("version").GetString()!;
            var currentGuid = indexJson.RootElement.GetProperty("guid").GetString()!;
            _ = Guid.Parse(currentGuid);
            return servicePath / currentVersion / currentGuid;
        }
        catch { }

        return null;
    }

    public async Task<AbsolutePath?> GetServicePath(string name, string version, CancellationToken cancellationToken)
    {
        var nameLower = name.ToLowerInvariant();

        var servicePath = _configuration.GetServicesPath() / nameLower;
        var servicePathCurrentIndex = servicePath / "index";

        try
        {
            var indexStr = await servicePathCurrentIndex.ReadAllText(cancellationToken);
            var indexJson = JsonSerializer.Deserialize<JsonDocument>(indexStr)!;
            var currentVersion = indexJson.RootElement.GetProperty("version").GetString()!;
            var guid = indexJson.RootElement.GetProperty("guid").GetString()!;
            if (version != currentVersion)
            {
                return null;
            }
            _ = Guid.Parse(guid);
            return servicePath / version / guid;
        }
        catch { }

        return null;
    }

    public async Task<AbsolutePath> GetServicePathOrDownload(
        string name,
        string url,
        string version,
        Func<(AbsolutePath DownloadedFilePath, AbsolutePath ExtractDirectory), Task> extractFactory,
        Func<AbsolutePath, (AbsolutePath Path, string name)[]> executableLinkFactory,
        CancellationToken cancellationToken)
    {
        return await GetServicePath(name, version, cancellationToken) ??
            await Download(name, url, version, extractFactory, executableLinkFactory, cancellationToken);
    }

    public Task<AbsolutePath> GetServicePathOrDownload(
        string name,
        string url,
        string version,
        Func<(AbsolutePath DownloadedFilePath, AbsolutePath ExtractDirectory), Task> extractFactory,
        CancellationToken cancellationToken)
    {
        return GetServicePathOrDownload(name, url, version, extractFactory, _ => [], cancellationToken);
    }
}
