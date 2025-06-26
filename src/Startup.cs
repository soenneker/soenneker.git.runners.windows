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
    public static void ConfigureServices(IServiceCollection services)
    {
        services.SetupIoC();
    }

    public static IServiceCollection SetupIoC(this IServiceCollection services)
    {
        services.AddHostedService<ConsoleHostedService>()
                .AddScoped<IFileOperationsUtil, FileOperationsUtil>()
                .AddFileDownloadUtilAsScoped()
                .AddGitHubRepositoriesReleasesUtilAsScoped()
                .AddRunnersManagerAsScoped().
                AddSevenZipCompressionUtilAsScoped();

        return services;
    }
}
