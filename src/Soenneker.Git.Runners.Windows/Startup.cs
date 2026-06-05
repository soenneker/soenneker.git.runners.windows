using Microsoft.Extensions.DependencyInjection;
using Soenneker.Compression.SevenZip.Registrars;
using Soenneker.Git.Runners.Windows.Utils;
using Soenneker.Git.Runners.Windows.Utils.Abstract;
using Soenneker.GitHub.Repositories.Releases.Registrars;
using Soenneker.Managers.Runners.Registrars;
using Soenneker.Utils.File.Download.Registrars;

namespace Soenneker.Git.Runners.Windows;

/// <summary>
/// Console type startup
/// </summary>
public static class Startup
{
    // This method gets called by the runtime. Use this method to add services to the container.
    /// <summary>
    /// Configures services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static void ConfigureServices(IServiceCollection services)
    {
        services.SetupIoC();
    }

    /// <summary>
    /// Sets up io c.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The result of the operation.</returns>
    public static IServiceCollection SetupIoC(this IServiceCollection services)
    {
        services.AddHostedService<ConsoleHostedService>()
                .AddSingleton<IFileOperationsUtil, FileOperationsUtil>()
                .AddFileDownloadUtilAsSingleton()
                .AddGitHubRepositoriesReleasesUtilAsSingleton()
                .AddRunnersManagerAsSingleton().
                AddSevenZipCompressionUtilAsSingleton();

        return services;
    }
}
