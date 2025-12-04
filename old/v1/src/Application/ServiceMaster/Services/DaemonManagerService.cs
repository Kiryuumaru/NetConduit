using AbsolutePathHelpers;
using Application.Configuration.Extensions;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Application.Common.Extensions;

namespace Application.ServiceMaster.Services;

public class DaemonManagerService(ILogger<DaemonManagerService> logger, IConfiguration configuration, ServiceManagerService serviceDownloader)
{
    private readonly ILogger<DaemonManagerService> _logger = logger;
    private readonly IConfiguration _configuration = configuration;
    private readonly ServiceManagerService _serviceDownloader = serviceDownloader;

    private const string WinswBuild = "build.1";

    public async Task<AbsolutePath> PrepareServiceWrapper(CancellationToken cancellationToken)
    {
        using var _ = _logger.BeginScopeMap(nameof(DaemonManagerService), nameof(PrepareServiceWrapper));

        var home = _configuration.GetHomePath();

        string folderName;
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            folderName = "winsw_windows_x64";
        }
        else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            folderName = "winsw_windows_arm64";
        }
        else
        {
            throw new NotSupportedException();
        }

        var winswSvcPath = await _serviceDownloader.GetServicePathOrDownload(
            "winsw",
            $"https://github.com/Kiryuumaru/winsw-modded/releases/download/{WinswBuild}/{folderName}.zip",
            WinswBuild,
            async (extractFactory) =>
            {
                var extractTemp = _configuration.GetTempPath() / $"winsw-{Guid.NewGuid()}";
                await extractFactory.DownloadedFilePath.UnZipTo(extractTemp, cancellationToken);
                await (extractTemp / folderName / "winsw.exe").CopyTo(extractFactory.ExtractDirectory / "winsw.exe");
            },
            cancellationToken);

        return winswSvcPath / "winsw.exe";
    }

    public async Task Install(
        string id,
        string name,
        string description,
        AbsolutePath execPath,
        string args,
        string? username,
        string? password,
        Dictionary<string, string> envVars,
        CancellationToken cancellationToken)
    {
        var idLower = id.ToLowerInvariant();

        using var _ = _logger.BeginScopeMap(nameof(DaemonManagerService), nameof(Install), new()
        {
            ["ServiceId"] = idLower,
        });

        _logger.LogInformation("Installing {ServiceId} service...", idLower);

        var winswExecPath = await PrepareServiceWrapper(cancellationToken);

        string envStr = "";

        foreach (var env in envVars)
        {
            if (!string.IsNullOrEmpty(envStr))
            {
                envStr += "\n";
            }
            envStr += $"  <env name=\"{env.Key}\" value=\"{env.Value}\" />";
        }

        var config = $"""
            <service>
              <id>{id}</id>
              <name>{name}</name>
              <description>{description}</description>
              <executable>{execPath}</executable>
              <arguments>{args}</arguments>
              <log mode="roll"></log>
              <startmode>Automatic</startmode>
              <onfailure action="restart" delay="2 sec"/>
              <outfilepattern>.output.log</outfilepattern>
              <errfilepattern>.error.log</errfilepattern>
              <combinedfilepattern>.combined.log</combinedfilepattern>
              <workingdirectory>{execPath.Parent}</workingdirectory>
            {envStr}
            </service>
            """;

        var serviceConfig = _configuration.GetDaemonsPath() / idLower / "svc.xml";

        serviceConfig.Parent?.CreateDirectory();

        await File.WriteAllTextAsync(serviceConfig, config, cancellationToken);

        try
        {
            await Cli.RunListenAndLog(_logger, $"{winswExecPath} stop {serviceConfig} --force", stoppingToken: cancellationToken);
            _logger.LogDebug("Existing {ServiceId} service stopped", idLower);
            await Cli.RunListenAndLog(_logger, $"{winswExecPath} uninstall {serviceConfig}", stoppingToken: cancellationToken);
            _logger.LogDebug("Existing {ServiceId} service uninstalled", idLower);
        }
        catch { }
        if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password))
        {
            if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
            {
                throw new Exception("Both username and password must be specified");
            }

            await Cli.RunListenAndLog(_logger, $"{winswExecPath} install {serviceConfig} --username \"{username}\" --password \"{password}\"", stoppingToken: cancellationToken);
        }
        else
        {
            await Cli.RunListenAndLog(_logger, $"{winswExecPath} install {serviceConfig}", stoppingToken: cancellationToken);
        }

        _logger.LogInformation("Service {ServiceId} installed", idLower);
    }

    public async Task Start(string id, CancellationToken cancellationToken)
    {
        var idLower = id.ToLowerInvariant();

        using var _ = _logger.BeginScopeMap(nameof(DaemonManagerService), nameof(Start), new()
        {
            ["ServiceId"] = idLower,
        });

        _logger.LogInformation("Starting {ServiceId} service...", idLower);

        var winswExecPath = await PrepareServiceWrapper(cancellationToken);
        var serviceConfig = _configuration.GetDaemonsPath() / idLower / "svc.xml";

        if (!serviceConfig.IsExists())
        {
            throw new Exception($"Error starting {idLower} service: Does not exists.");
        }

        try
        {
            await Cli.RunListenAndLog(_logger, $"{winswExecPath} stop {serviceConfig} --force", stoppingToken: cancellationToken);
            _logger.LogDebug("Existing {ServiceId} service stopped", idLower);
        }
        catch { }
        await Cli.RunListenAndLog(_logger, $"{winswExecPath} start {serviceConfig}", stoppingToken: cancellationToken);

        _logger.LogInformation("Service {ServiceId} started", idLower);
    }

    public async Task Stop(string id, CancellationToken cancellationToken)
    {
        var idLower = id.ToLowerInvariant();

        using var _ = _logger.BeginScopeMap(nameof(DaemonManagerService), nameof(Stop), new()
        {
            ["ServiceId"] = idLower,
        });

        _logger.LogInformation("Stopping {ServiceId} service...", idLower);

        var winswExecPath = await PrepareServiceWrapper(cancellationToken);
        var serviceConfig = _configuration.GetDaemonsPath() / idLower / "svc.xml";

        if (!serviceConfig.IsExists())
        {
            throw new Exception($"Error stopping {idLower} service: Does not exists.");
        }

        await Cli.RunListenAndLog(_logger, $"{winswExecPath} stop {serviceConfig} --force", stoppingToken: cancellationToken);

        _logger.LogInformation("Service {ServiceId} stopped", idLower);
    }

    public async Task Uninstall(string id, CancellationToken cancellationToken)
    {
        var idLower = id.ToLowerInvariant();

        using var _ = _logger.BeginScopeMap(nameof(DaemonManagerService), nameof(Uninstall), new()
        {
            ["ServiceId"] = idLower,
        });

        _logger.LogInformation("Uninstalling {ServiceId} service...", idLower);

        var winswExecPath = await PrepareServiceWrapper(cancellationToken);
        var serviceConfig = _configuration.GetDaemonsPath() / idLower / "svc.xml";

        if (!serviceConfig.IsExists())
        {
            throw new Exception($"Error uninstalling {idLower} service: Does not exists.");
        }

        try
        {
            await Cli.RunListenAndLog(_logger, $"{winswExecPath} stop {serviceConfig} --force", stoppingToken: cancellationToken);
            _logger.LogDebug("Existing {ServiceId} service stopped", idLower);
        }
        catch { }
        await Cli.RunListenAndLog(_logger, $"{winswExecPath} uninstall {serviceConfig}", stoppingToken: cancellationToken);

        _logger.LogInformation("Service {ServiceId} uninstalled", idLower);
    }
}
