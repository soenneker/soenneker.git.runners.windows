using System;
using System.IO;
using System.Linq;
using System.Net.Http;
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

namespace Soenneker.Git.Runners.Windows.Utils;

///<inheritdoc cref="IBuildLibraryUtil"/>
public sealed class BuildLibraryUtil : IBuildLibraryUtil
{
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
        // 1) prepare temp dir
        string tempDir = _directoryUtil.CreateTempDirectory();

        // 2) fetch latest Git tag
        string latestVersion = await GetLatestStableGitTag(cancellationToken);
        _logger.LogInformation("Latest stable Git version: {version}", latestVersion);

        // 3) download Git source & install deps in parallel
        string archivePath = Path.Combine(tempDir, "git.tar.gz");
        string downloadUrl = $"https://github.com/git/git/archive/refs/tags/{latestVersion}.tar.gz";
        _logger.LogInformation("Downloading Git source from {url}", downloadUrl);

        var installScript = "sudo apt-get update && " + "sudo apt-get install -y " + "build-essential musl-tools pkg-config ccache " +
                            "libcurl4-openssl-dev libssl-dev libexpat1-dev zlib1g-dev " + "tcl-dev tk-dev perl libperl-dev libreadline-dev " +
                            "gettext autoconf automake intltool libtool libtool-bin " + "bison bzip2 flex gperf libgdk-pixbuf2.0-dev lzip " +
                            "openssl patch python3 python3-mako ruby sed unzip wget xz-utils p7zip-full autopoint";

        await _processUtil.ShellRun(installScript, tempDir, cancellationToken);
        await _fileDownloadUtil.Download(downloadUrl, archivePath, cancellationToken: cancellationToken);


        // 4) prepare or reuse MXE cache
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string mxeCache = Path.Combine(home, ".cache", "mxe");
        if (!Directory.Exists(mxeCache))
        {
            _logger.LogInformation("Cloning MXE into cache at {path}", mxeCache);
            await _processUtil.ShellRun($"git clone --depth 1 https://github.com/mxe/mxe.git {mxeCache}", tempDir, cancellationToken);

            _logger.LogInformation("Building MXE toolchain (static Win64) in cache...");
            string buildCacheCmd = $"cd {mxeCache} && " + "export MXE_USE_CCACHE=1 && " + "export CC=\"ccache x86_64-w64-mingw32.static-gcc\" && " +
                                   $"make -j{Environment.ProcessorCount} MXE_TARGETS=\"x86_64-w64-mingw32.static\" gcc curl openssl pcre zlib expat";
            await _processUtil.ShellRun(buildCacheCmd, tempDir, cancellationToken);
        }
        else
        {
            _logger.LogInformation("Reusing cached MXE at {path}", mxeCache);
        }

        // 5) extract Git source
        _logger.LogInformation("Extracting Git source...");
        string extractTarCmd = $"tar -xzf {archivePath}";
        await _processUtil.ShellRun(extractTarCmd, tempDir, cancellationToken);

        var versionTrimmed = latestVersion.TrimStart('v');
        var extractPath = Path.Combine(tempDir, $"git-{versionTrimmed}");

        // 6) patch config.mak to include helpers
        _logger.LogInformation("Patching config.mak.sample...");
        string sedExpr = "\"s/^BUILTIN_LIST = (.*)$/BUILTIN_LIST = \\1 remote-https remote-ssh credential-manager http-backend/\"";
        string patchCmd = $"cd {extractPath.Replace(':', '/')} && cp config.mak.sample config.mak && sed -i -E {sedExpr} config.mak";
        await _processUtil.ShellRun(patchCmd, tempDir, cancellationToken);

        // 7) generate configure script
        _logger.LogInformation("Generating configure script...");
        string genConfigureCmd = $"cd {extractPath.Replace(':', '/')} && make configure";
        await _processUtil.ShellRun(genConfigureCmd, tempDir, cancellationToken);

        // 8) configure for static Windows
        _logger.LogInformation("Configuring for Windows cross-compile...");
        var mxeBin = Path.Combine(mxeCache, "usr", "bin");
        string configureCmd = $"export PATH={mxeBin}:$PATH && cd {extractPath.Replace(':', '/')} && " +
                              "./configure --host=x86_64-w64-mingw32.static --prefix=/usr " +
                              "CC=x86_64-w64-mingw32.static-gcc CFLAGS='-static -O2 -pipe' LDFLAGS='-static'";
        await _processUtil.ShellRun(configureCmd, tempDir, cancellationToken);

        // 9) compile
        _logger.LogInformation("Building Git for Windows...");
        string makeCmd = $"cd {extractPath.Replace(':', '/')} && make -j{Environment.ProcessorCount}";
        await _processUtil.ShellRun(makeCmd, tempDir, cancellationToken);

        // 10) strip binary
        var gitExe = Path.Combine(extractPath, "git.exe");
        _logger.LogInformation("Stripping git.exe...");
        string stripCmd = $"strip {gitExe}";
        await _processUtil.ShellRun(stripCmd, tempDir, cancellationToken);

        if (!File.Exists(gitExe))
            throw new FileNotFoundException("git.exe not found after build", gitExe);

        _logger.LogInformation("Built static git.exe at {path}", gitExe);
        return gitExe;
    }

    public async ValueTask<string> GetLatestStableGitTag(CancellationToken cancellationToken = default)
    {
        HttpClient client = await _httpClientCache.Get(nameof(BuildLibraryUtil), cancellationToken: cancellationToken);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DotNetGitTool/1.0");

        JsonElement[]? tags = await client.GetFromJsonAsync<JsonElement[]>("https://api.github.com/repos/git/git/tags", cancellationToken);

        foreach (var tag in tags!)
        {
            var name = tag.GetProperty("name").GetString()!;
            if (!name.Contains("-rc", StringComparison.OrdinalIgnoreCase) && !name.Contains("-beta", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("-alpha", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        throw new Exception("No stable Git version found.");
    }
}