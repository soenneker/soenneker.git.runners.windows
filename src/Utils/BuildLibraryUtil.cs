using System;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Git.Runners.Windows.Utils.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Download.Abstract;
using Soenneker.Utils.HttpClientCache.Abstract;
using Soenneker.Utils.Process.Abstract;

namespace Soenneker.Git.Runners.Windows.Utils
{
    /// <inheritdoc cref="IBuildLibraryUtil"/>
    public sealed class BuildLibraryUtil : IBuildLibraryUtil
    {
        // MSYS2 install & update steps
        private const string InstallMsys2 = "choco install -y msys2";
        private const string PacmanSync = @"bash -lc ""pacman -Sy --noconfirm""";
        private const string PacmanUpgrade = @"bash -lc ""MSYS2_ARG_CONV_EXCL='*' pacman -Su --noconfirm""";
        private const string PacmanDependencies =
            @"bash -lc ""pacman -Sy --noconfirm --needed " +
             "mingw-w64-x86_64-gcc mingw-w64-x86_64-make mingw-w64-x86_64-autoconf " +
             "mingw-w64-x86_64-automake mingw-w64-x86_64-libtool mingw-w64-x86_64-pkgconf " +
             "mingw-w64-x86_64-curl mingw-w64-x86_64-libiconv mingw-w64-x86_64-expat " +
             "mingw-w64-x86_64-openssl mingw-w64-x86_64-zlib mingw-w64-x86_64-bzip2 " +
             "mingw-w64-x86_64-xz mingw-w64-x86_64-patch mingw-w64-x86_64-perl " +
             "mingw-w64-x86_64-python3 mingw-w64-x86_64-ruby\"";

        private readonly ILogger<BuildLibraryUtil> _logger;
        private readonly IDirectoryUtil _directoryUtil;
        private readonly IHttpClientCache _httpClientCache;
        private readonly IFileDownloadUtil _fileDownloadUtil;
        private readonly IProcessUtil _processUtil;

        public BuildLibraryUtil(
            ILogger<BuildLibraryUtil> logger,
            IDirectoryUtil directoryUtil,
            IHttpClientCache httpClientCache,
            IFileDownloadUtil fileDownloadUtil,
            IProcessUtil processUtil)
        {
            _logger = logger;
            _directoryUtil = directoryUtil;
            _httpClientCache = httpClientCache;
            _fileDownloadUtil = fileDownloadUtil;
            _processUtil = processUtil;
        }

        public async ValueTask<string> Build(CancellationToken cancellationToken)
        {
            // 1) prepare temp dir
            string tempDir = await _directoryUtil.CreateTempDirectory(cancellationToken);

            // 2) install MSYS2 if needed
            var msysBin = @"C:\msys64\usr\bin";
            var bashExe = Path.Combine(msysBin, "bash.exe");
            if (!File.Exists(bashExe))
            {
                try
                {
                    _logger.LogInformation("Installing MSYS2 via Chocolatey...");
                    await _processUtil.CmdRun(InstallMsys2, tempDir, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MSYS2 installation failed");
                    throw new InvalidOperationException("Failed to install MSYS2", ex);
                }
            }

            // 3) ensure msys2 bin on PATH for child processes
            var currentPath = Environment.GetEnvironmentVariable("PATH")!;
            if (!currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries)
                            .Contains(msysBin, StringComparer.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", $"{msysBin};{currentPath}");
            }

            // 4a) sync pacman DB
            try
            {
                _logger.LogInformation("Synchronizing pacman database...");
                await _processUtil.CmdRun(PacmanSync, tempDir, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "pacman -Sy failed");
                throw new InvalidOperationException("Failed to sync pacman database", ex);
            }

            // 4b) upgrade core system
            try
            {
                _logger.LogInformation("Upgrading MSYS2 core system...");
                await _processUtil.CmdRun(PacmanUpgrade, tempDir, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "pacman -Su failed");
                throw new InvalidOperationException("Failed to upgrade MSYS2 core", ex);
            }

            // 4c) install MinGW dependencies
            try
            {
                _logger.LogInformation("Installing build dependencies via pacman...");
                await _processUtil.CmdRun(PacmanDependencies, tempDir, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "pacman dependency installation failed");
                throw new InvalidOperationException("Failed to install build dependencies", ex);
            }

            // 5) fetch latest Git tag
            string latestVersion = await GetLatestStableGitTag(cancellationToken);
            _logger.LogInformation("Latest stable Git version: {version}", latestVersion);

            // 6) download & validate source archive
            string archivePath = Path.Combine(tempDir, "git.tar.gz");
            string downloadUrl = $"https://github.com/git/git/archive/refs/tags/{latestVersion}.tar.gz";
            _logger.LogInformation("Downloading Git source from {url}", downloadUrl);
            await DownloadWithRetry(downloadUrl, archivePath, cancellationToken);
            ValidateGzip(archivePath);

            // 7) extract & build under MSYS2
            string msysTemp = ToMsysPath(tempDir);
            string msysArchive = ToMsysPath(archivePath);
            try
            {
                _logger.LogInformation("Extracting Git source...");
                await _processUtil.CmdRun(
                    $@"bash -lc ""tar -xzf {msysArchive} -C {msysTemp}""",
                    tempDir, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract Git source");
                throw;
            }

            string gitSrcWin = Path.Combine(tempDir, $"git-{latestVersion.TrimStart('v')}");
            string gitSrcMsys = ToMsysPath(gitSrcWin);

            // prepare clean staging dir
            string distRoot = Path.Combine(tempDir, "dist");
            if (Directory.Exists(distRoot))
                Directory.Delete(distRoot, recursive: true);
            Directory.CreateDirectory(distRoot);
            string distMsys = ToMsysPath(distRoot);

            // configure & make
            var buildScript = $@"
cd {gitSrcMsys}
make configure
./configure --prefix=/mingw64 --with-openssl --with-curl
make -j{Environment.ProcessorCount}
make install DESTDIR={distMsys}";
            try
            {
                _logger.LogInformation("Configuring & building Git...");
                await _processUtil.CmdRun(
                    $@"bash -lc ""{buildScript}""",
                    tempDir, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Git build failed");
                throw;
            }

            // 8) locate git.exe
            string distBin = Path.Combine(distRoot, "mingw64", "bin");
            var gitExe = Directory
                .EnumerateFiles(distBin, "git.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (gitExe == null)
                throw new FileNotFoundException("git.exe not found after build", distBin);

            _logger.LogInformation("Built git.exe at {path}", gitExe);
            return gitExe;
        }

        private async Task DownloadWithRetry(
            string url,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            const int maxAttempts = 3;
            var delay = TimeSpan.FromSeconds(2);
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await _fileDownloadUtil
                        .Download(url, destinationPath, cancellationToken: cancellationToken);
                    return;
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    _logger.LogWarning(
                        ex,
                        "Download failed (attempt {Attempt}/{Max}). Retrying in {Delay}s...",
                        attempt, maxAttempts, delay.TotalSeconds);
                    await Task.Delay(delay, cancellationToken);
                    delay *= 2;
                }
            }
        }

        private static void ValidateGzip(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            if (fs.Length < 2 || fs.ReadByte() != 0x1F || fs.ReadByte() != 0x8B)
                throw new InvalidDataException($"{path} is not a valid gzip archive.");
        }

        private static string ToMsysPath(string winPath)
        {
            var p = winPath.Replace('\\', '/');
            if (p.Length >= 2 && p[1] == ':')
            {
                var drive = char.ToLowerInvariant(p[0]);
                p = $"/{drive}{p.Substring(2)}";
            }
            return p;
        }

        public async ValueTask<string> GetLatestStableGitTag(
            CancellationToken cancellationToken = default)
        {
            var client = await _httpClientCache
                .Get(nameof(BuildLibraryUtil), cancellationToken: cancellationToken);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DotNetGitTool/1.0");

            var tags = await client
                .GetFromJsonAsync<JsonElement[]>(
                    "https://api.github.com/repos/git/git/tags",
                    cancellationToken);

            if (tags == null)
                throw new InvalidOperationException("Could not fetch tags from GitHub API.");

            foreach (var tag in tags)
            {
                var name = tag.GetProperty("name").GetString();
                if (name != null
                    && !name.Contains("-rc", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("-beta", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("-alpha", StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }

            throw new InvalidOperationException("No stable Git version found.");
        }
    }
}