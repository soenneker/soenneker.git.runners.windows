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
        private const string InstallMsys2 = "choco install -y msys2";
        private const string PacmanUpgrade = @"bash -lc ""pacman -Syu --noconfirm""";

        private const string PacmanDependencies = @"bash -lc ""pacman -Sy --noconfirm mingw-w64-x86_64-toolchain base-devel " +
                                                  "mingw-w64-x86_64-curl mingw-w64-x86_64-iconv mingw-w64-x86_64-expat " +
                                                  "mingw-w64-x86_64-openssl mingw-w64-x86_64-zlib";

        private readonly ILogger<BuildLibraryUtil> _logger;
        private readonly IDirectoryUtil _directoryUtil;
        private readonly IHttpClientCache _httpClientCache;
        private readonly IFileDownloadUtil _fileDownloadUtil;
        private readonly IProcessUtil _processUtil;

        public BuildLibraryUtil(ILogger<BuildLibraryUtil> logger, IDirectoryUtil directoryUtil, IHttpClientCache httpClientCache,
            IFileDownloadUtil fileDownloadUtil, IProcessUtil processUtil)
        {
            _logger = logger;
            _directoryUtil = directoryUtil;
            _httpClientCache = httpClientCache;
            _fileDownloadUtil = fileDownloadUtil;
            _processUtil = processUtil;
        }

        public async ValueTask<string> Build(CancellationToken cancellationToken)
        {
            // 1) Create temp directory
            string tempDir = await _directoryUtil.CreateTempDirectory(cancellationToken);

            // 2) Ensure MSYS2 is installed
            var msysBin = @"C:\msys64\usr\bin";
            var bashExe = Path.Combine(msysBin, "bash.exe");
            if (!File.Exists(bashExe))
            {
                _logger.LogInformation("Installing MSYS2 via Chocolatey...");
                await _processUtil.CmdRun(InstallMsys2, tempDir, cancellationToken);
            }

            // 3) Add MSYS2 to PATH for this process
            var currentPath = Environment.GetEnvironmentVariable("PATH")!;
            if (!currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries).Contains(msysBin, StringComparer.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("PATH", $"{msysBin};{currentPath}");
            }

            // 4) Update pacman core & install dependencies
            _logger.LogInformation("Upgrading MSYS2 and installing build dependencies...");
            await _processUtil.CmdRun(PacmanUpgrade, tempDir, cancellationToken);
            await _processUtil.CmdRun(PacmanDependencies, tempDir, cancellationToken);

            // 5) Fetch latest stable Git tag
            string latestVersion = await GetLatestStableGitTag(cancellationToken);
            _logger.LogInformation("Latest stable Git version: {version}", latestVersion);

            // 6) Download source archive with retry + validation
            string archivePath = Path.Combine(tempDir, "git.tar.gz");
            string downloadUrl = $"https://github.com/git/git/archive/refs/tags/{latestVersion}.tar.gz";
            _logger.LogInformation("Downloading Git source from {url}", downloadUrl);
            await DownloadWithRetry(downloadUrl, archivePath, cancellationToken);
            ValidateGzip(archivePath);

            // 7) Extract & build under MSYS2 MinGW64
            string msysTemp = ToMsysPath(tempDir);
            string msysArchive = ToMsysPath(archivePath);
            _logger.LogInformation("Extracting Git source...");
            await _processUtil.CmdRun($@"bash -lc ""tar -xzf {msysArchive} -C {msysTemp}""", tempDir, cancellationToken);

            string gitSrcWin = Path.Combine(tempDir, $"git-{latestVersion.TrimStart('v')}");
            string gitSrcMsys = ToMsysPath(gitSrcWin);

            // ensure dist folder exists
            string distRoot = Path.Combine(tempDir, "dist");
            Directory.CreateDirectory(distRoot);
            string distMsys = ToMsysPath(distRoot);

            _logger.LogInformation("Configuring & building Git...");
            var buildScript = $@"
cd {gitSrcMsys}
make configure
./configure --prefix=/mingw64 --with-openssl --with-curl
make -j{Environment.ProcessorCount}
make install DESTDIR={distMsys}";
            await _processUtil.CmdRun($@"bash -lc ""{buildScript}""", tempDir, cancellationToken);

            // 8) Locate resulting git.exe
            string distBin = Path.Combine(distRoot, "mingw64", "bin");
            var gitExe = Directory.EnumerateFiles(distBin, "git.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (gitExe == null)
                throw new FileNotFoundException("git.exe not found after build", distBin);

            _logger.LogInformation("Built git.exe at {path}", gitExe);
            return gitExe;
        }

        public async Task DownloadWithRetry(string url, string destinationPath, CancellationToken cancellationToken)
        {
            const int maxAttempts = 3;
            var delay = TimeSpan.FromSeconds(2);
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await _fileDownloadUtil.Download(url, destinationPath, cancellationToken: cancellationToken);
                    return;
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    _logger.LogWarning(ex, "Download failed (attempt {attempt}/{max}). Retrying in {delay}…", attempt, maxAttempts, delay);
                    await Task.Delay(delay, cancellationToken);
                    delay *= 2;
                }
            }
        }

        private static void ValidateGzip(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            if (fs.Length < 2 || fs.ReadByte() != 0x1F || fs.ReadByte() != 0x8B)
            {
                throw new InvalidDataException($"{path} is not a valid gzip archive.");
            }
        }

        private static string ToMsysPath(string winPath)
        {
            // C:\foo\bar → /c/foo/bar
            var p = winPath.Replace('\\', '/');
            if (p.Length >= 2 && p[1] == ':')
            {
                var drive = char.ToLowerInvariant(p[0]);
                p = $"/{drive}{p.Substring(2)}";
            }

            return p;
        }

        public async ValueTask<string> GetLatestStableGitTag(CancellationToken cancellationToken = default)
        {
            var client = await _httpClientCache.Get(nameof(BuildLibraryUtil), cancellationToken: cancellationToken);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DotNetGitTool/1.0");

            var tags = await client.GetFromJsonAsync<JsonElement[]>("https://api.github.com/repos/git/git/tags", cancellationToken);

            if (tags == null)
                throw new InvalidOperationException("Could not fetch tags from GitHub API.");

            foreach (var tag in tags)
            {
                var name = tag.GetProperty("name").GetString();
                if (name != null && !name.Contains("-rc", StringComparison.OrdinalIgnoreCase) && !name.Contains("-beta", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("-alpha", StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }

            throw new InvalidOperationException("No stable Git version found.");
        }
    }
}