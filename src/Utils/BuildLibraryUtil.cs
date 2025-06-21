using System;
using System.IO;
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

/// <inheritdoc cref="IBuildLibraryUtil"/>
public sealed class BuildLibraryUtil : IBuildLibraryUtil
{
    private const string ReproEnv = "SOURCE_DATE_EPOCH=1620000000 TZ=UTC LC_ALL=C";

    private const string InstallScript = "sudo apt-get update && sudo apt-get install -y build-essential pkg-config ccache " + "perl " +
                                         "gettext autoconf automake intltool libtool libtool-bin bison bzip2 flex gperf lzip " +
                                         "openssl patch python3 python3-mako ruby sed unzip wget xz-utils p7zip-full autopoint";

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

        // 3) download Git source & install deps
        string archivePath = Path.Combine(tempDir, "git.tar.gz");
        string downloadUrl = $"https://github.com/git/git/archive/refs/tags/{latestVersion}.tar.gz";
        _logger.LogInformation("Downloading Git source from {url}", downloadUrl);
        await _processUtil.ShellRun(InstallScript, tempDir, cancellationToken);
        await _fileDownloadUtil.Download(downloadUrl, archivePath, cancellationToken: cancellationToken);

        // 4) prepare or reuse MXE cache
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string mxeCache = Path.Combine(home, ".cache", "mxe");

        _logger.LogInformation("Cloning MXE into cache at {path}", mxeCache);
        await _processUtil.ShellRun($"{ReproEnv} git clone --depth 1 https://github.com/mxe/mxe.git {mxeCache}", tempDir, cancellationToken);

        _logger.LogInformation("Building MXE toolchain (static Win64) in cache...");
        string buildCacheCmd = $"cd {mxeCache} && export MXE_USE_CCACHE=1 && export CC=ccache\\ x86_64-w64-mingw32.static-gcc && " +
                               $"make -j{Environment.ProcessorCount} MXE_TARGETS=x86_64-w64-mingw32.static gcc curl openssl pcre zlib expat";
        await _processUtil.BashRun(cmd: "bash", args: $"-lc \"{ReproEnv} {buildCacheCmd}\"", workingDir: tempDir, cancellationToken);


        // 5) extract Git source
        _logger.LogInformation("Extracting Git source...");
        await _processUtil.ShellRun($"{ReproEnv} tar --sort=name --mtime=@1620000000 --owner=0 --group=0 --numeric-owner -xzf {archivePath}", tempDir,
            cancellationToken);

        var candidates = Directory.GetDirectories(tempDir, "git-*");

        if (candidates.Length != 1)
            throw new Exception($"Expected exactly one git-<tag> folder in {tempDir}, found {candidates.Length}");

        string extractPath = candidates[0];

        _logger.LogInformation("Extracted Git source to {path}", extractPath);

        // 6) patch config.mak
        _logger.LogInformation("Patching config.mak.sample to fold helpers into built-in git.exe...");
        string gitDir = extractPath.Replace(':', '/');
        string snippet = $"cd {gitDir} && " + "cp config.mak.dev config.mak && " +
                         // wrap the sed script in '\'' … '\''
                         "sed -i -E '\\''s/^BUILTIN_LIST = (.*)$/BUILTIN_LIST = \\1 remote-https remote-ssh credential-manager http-backend/'\\'' config.mak";
        await _processUtil.ShellRun(snippet, tempDir, cancellationToken);

        // 7) generate configure script
        _logger.LogInformation("Generating configure script...");
        await _processUtil.ShellRun($"{ReproEnv} cd {extractPath.Replace(':', '/')} && make configure", tempDir, cancellationToken);

        // 8) configure for Windows cross-compile
        _logger.LogInformation("Configuring for Windows cross-compile…");
        string mxeBin = Path.Combine(mxeCache, "usr", "bin");

        // Build a snippet that uses double-quotes for the flag values
        string configureArgs =
            $"-lc \"export PATH={mxeBin}:$PATH && " +
            $"cd {gitDir} && " +
            "./configure " +
            "--host=x86_64-w64-mingw32.static " +
            "--prefix=/usr " +
            "CC=x86_64-w64-mingw32.static-gcc " +
            "CFLAGS=\\\"-static -O2 -pipe\\\" " +
            "LDFLAGS=\\\"-static\\\"\"";

        // Directly call BashRun so you’re not wrapped in extra single-quotes
        await _processUtil.BashRun(
            cmd: "bash",
            args: configureArgs,
            workingDir: tempDir,
            cancellationToken: cancellationToken
        );

        // 9) compile
        _logger.LogInformation("Building Git for Windows...");
        await _processUtil.ShellRun($"{ReproEnv} cd {extractPath.Replace(':', '/')} && make -j{Environment.ProcessorCount}", tempDir, cancellationToken);

        // 10) strip binary
        string gitExe = Path.Combine(extractPath, "git.exe");
        _logger.LogInformation("Stripping git.exe...");
        await _processUtil.ShellRun($"{ReproEnv} strip {gitExe}", tempDir, cancellationToken);

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
            string name = tag.GetProperty("name").GetString()!;
            if (!name.Contains("-rc", StringComparison.OrdinalIgnoreCase) && !name.Contains("-beta", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("-alpha", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        throw new InvalidOperationException("No stable Git version found.");
    }
}