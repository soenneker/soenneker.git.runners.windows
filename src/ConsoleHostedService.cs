using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Soenneker.Git.Runners.Windows.Utils.Abstract;
using Soenneker.Managers.Runners.Abstract;
using Soenneker.Utils.FileSync.Abstract;

namespace Soenneker.Git.Runners.Windows;

public sealed class ConsoleHostedService : IHostedService
{
    private readonly ILogger<ConsoleHostedService> _logger;

    private readonly IHostApplicationLifetime _appLifetime;
    private readonly IRunnersManager _runnersManager;
    private readonly IFileOperationsUtil _fileOperationsUtil;
    private readonly IFileUtilSync _fileUtilSync;

    private int? _exitCode;

    public ConsoleHostedService(ILogger<ConsoleHostedService> logger, IHostApplicationLifetime appLifetime, IRunnersManager runnersManager,
        IFileOperationsUtil fileOperationsUtil, IFileUtilSync fileUtilSync)
    {
        _logger = logger;
        _appLifetime = appLifetime;
        _runnersManager = runnersManager;
        _fileOperationsUtil = fileOperationsUtil;
        _fileUtilSync = fileUtilSync;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _appLifetime.ApplicationStarted.Register(() =>
        {
            Task.Run(async () =>
            {
                _logger.LogInformation("Running console hosted service ...");

                try
                {
                    string? extractionDir = await _fileOperationsUtil.Process(cancellationToken);

                    if (extractionDir != null)
                    {
                        var unnecessaryBinFiles = new List<string>
                        {
                            "Avalonia.Base.dll",
                            "Avalonia.Controls.dll",
                            "Avalonia.Win32.dll",
                            "Avalonia.Themes.Fluent.dll",
                            "Avalonia.Dialogs.dll",
                            "Avalonia.DesignerSupport.dll",
                            "libSkiaSharp.dll",
                            "SkiaSharp.dll",
                            "libHarfBuzzSharp.dll",
                            "HarfBuzzSharp.dll",
                            "System.Text.Json.dll",
                            "System.CommandLine.dll"
                        };

                        foreach (string file in unnecessaryBinFiles)
                        {
                            string filePath = Path.Combine(extractionDir, "bin", file);

                            _fileUtilSync.TryDeleteIfExists(filePath);
                        }

                        await _runnersManager.PushIfChangesNeededForDirectory(Path.Combine("win-x64", "git"), extractionDir, Constants.Library,
                            $"https://github.com/soenneker/{Constants.Library}", cancellationToken);
                    }

                    _logger.LogInformation("Complete!");

                    _exitCode = 0;
                }
                catch (Exception e)
                {
                    if (Debugger.IsAttached)
                        Debugger.Break();

                    _logger.LogError(e, "Unhandled exception");

                    await Task.Delay(2000, cancellationToken);
                    _exitCode = 1;
                }
                finally
                {
                    // Stop the application once the work is done
                    _appLifetime.StopApplication();
                }
            }, cancellationToken);
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Exiting with return code: {exitCode}", _exitCode);

        // Exit code may be null if the user cancelled via Ctrl+C/SIGTERM
        Environment.ExitCode = _exitCode.GetValueOrDefault(-1);
        return Task.CompletedTask;
    }
}