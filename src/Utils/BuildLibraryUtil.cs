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

        // 3) download Git source tarball
        string archivePath = Path.Combine(tempDir, "git.tar.gz");
        string downloadUrl = $"https://github.com/git/git/archive/refs/tags/{latestVersion}.tar.gz";
        _logger.LogInformation("Downloading Git source from {url}", downloadUrl);
        await _fileDownloadUtil.Download(downloadUrl, archivePath, cancellationToken: cancellationToken);

        // 4) install host-side build deps
        _logger.LogInformation("Installing native build dependencies…");
        var installScript =
            "sudo apt-get update && " +
            "sudo apt-get install -y " +
            "build-essential musl-tools pkg-config " +
            "libcurl4-openssl-dev libssl-dev libexpat1-dev zlib1g-dev " +
            "tcl-dev tk-dev perl libperl-dev libreadline-dev " +
            "gettext autoconf automake intltool libtool libtool-bin " +
            "bison bzip2 flex gperf libgdk-pixbuf2.0-dev lzip " +
            "openssl patch python3 python3-mako ruby sed unzip wget xz-utils p7zip-full";
        await _processUtil.ShellRun(installScript, tempDir, cancellationToken);

        // 5) clone MXE cross-toolchain
        _logger.LogInformation("Cloning MXE cross toolchain...");
        var mxeDir = Path.Combine(tempDir, "mxe");
        var cloneCmd = $"git clone --depth 1 https://github.com/mxe/mxe.git {mxeDir}";
        await _processUtil.ShellRun(cloneCmd, tempDir, cancellationToken);

        // 6) build MXE for static Win64 target
        _logger.LogInformation("Building MXE toolchain (static Win64)...");
        var buildMxeCmd =
            "cd mxe && " +
            "make gcc curl openssl pcre zlib expat " +
            "MXE_TARGETS='x86_64-w64-mingw32.static' " +
            $"-j{Environment.ProcessorCount}";
        await _processUtil.ShellRun(buildMxeCmd, tempDir, cancellationToken);

        // 7) extract Git source
        _logger.LogInformation("Extracting Git source...");
        var extractTarCmd = $"tar -xzf {archivePath}";
        await _processUtil.ShellRun(extractTarCmd, tempDir, cancellationToken);

        var versionTrimmed = latestVersion.TrimStart('v');
        var extractPath = Path.Combine(tempDir, $"git-{versionTrimmed}");

        // 8) patch config.mak.sample to include helpers as builtins
        _logger.LogInformation("Patching config.mak.sample to fold helpers into built-in git.exe...");
        var patchCmd = $"cd {extractPath.Replace(':', '/')} && " + "cp config.mak.sample config.mak && " +
                       "sed -i -E 's/^BUILTIN_LIST = (.*)$/BUILTIN_LIST = \\1 remote-https remote-ssh credential-manager http-backend/' config.mak";
        await _processUtil.ShellRun(patchCmd, tempDir, cancellationToken);

        // 9) generate configure script
        _logger.LogInformation("Generating configure script...");
        var genConfigureCmd = $"cd {extractPath.Replace(':', '/')} && make configure";
        await _processUtil.ShellRun(genConfigureCmd, tempDir, cancellationToken);

        // 10) configure for Windows static build
        _logger.LogInformation("Configuring cross-compile for Windows...");
        var mxeBin = Path.Combine(tempDir, "mxe", "usr", "bin");
        var configureCmd = $"export PATH={mxeBin}:$PATH && " + $"cd {extractPath.Replace(':', '/')} && " + "./configure " +
                           "--host=x86_64-w64-mingw32.static " + "--prefix=/usr " + "CC=x86_64-w64-mingw32.static-gcc " + "CFLAGS='-static -O2 -pipe' " +
                           "LDFLAGS='-static'";
        await _processUtil.ShellRun(configureCmd, tempDir, cancellationToken);

        // 11) build
        _logger.LogInformation("Building Git for Windows (cross-compile)...");
        var makeCmd = $"cd {extractPath.Replace(':', '/')} && make -j{Environment.ProcessorCount}";
        await _processUtil.ShellRun(makeCmd, tempDir, cancellationToken);

        // 12) strip to shrink size
        var gitExe = Path.Combine(extractPath, "git.exe");
        _logger.LogInformation("Stripping git.exe...");
        var stripCmd = $"strip {gitExe}";
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