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

/// <inheritdoc cref="IBuildLibraryUtil"/>
public sealed class BuildLibraryUtil : IBuildLibraryUtil
{
    private const string ReproEnv = "SOURCE_DATE_EPOCH=1620000000 TZ=UTC LC_ALL=C";

    private const string InstallScript = "sudo apt-get update && sudo apt-get install -y " +
                                         "build-essential pkg-config ccache perl gettext autoconf automake intltool libtool libtool-bin " +
                                         "bison bzip2 flex gperf lzip openssl patch python3 python3-mako ruby sed unzip wget xz-utils p7zip-full autopoint " +
                                         "libcurl4-openssl-dev libgdk-pixbuf-2.0-dev";

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
        string tempDir = await _directoryUtil.CreateTempDirectory(cancellationToken);

        // 2) fetch latest Git tag
        string latestVersion = await GetLatestStableGitTag(cancellationToken);
        _logger.LogInformation("Latest stable Git version: {version}", latestVersion);

        // 3) download Git source & install deps
        string archivePath = Path.Combine(tempDir, "git.tar.gz");
        string downloadUrl = $"https://github.com/git/git/archive/refs/tags/{latestVersion}.tar.gz";
        _logger.LogInformation("Downloading Git source from {url}", downloadUrl);

        await _processUtil.BashRun(InstallScript, "", tempDir, cancellationToken);

        await _fileDownloadUtil.Download(downloadUrl, archivePath, cancellationToken: cancellationToken);

        // 4) prepare or reuse MXE cache
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string mxeCache = Path.Combine(home, ".cache", "mxe");

        if (!Directory.Exists(mxeCache))
        {
            _logger.LogInformation("Cloning MXE into cache at {path}", mxeCache);
            string cloneSnippet = $"{ReproEnv} git clone --depth 1 https://github.com/mxe/mxe.git {mxeCache}";
            await _processUtil.BashRun(cloneSnippet, "", tempDir, cancellationToken);
        }
        else
        {
            _logger.LogInformation("MXE cache found at {path}, skipping clone.", mxeCache);
        }

        string mxeEnv = $"cd {mxeCache} && export MXE_USE_CCACHE=1 && export CC=ccache\\ x86_64-w64-mingw32.static-gcc";
        string mxeMakeCommand = $"make -j{Environment.ProcessorCount} MXE_TARGETS=x86_64-w64-mingw32.static";

        _logger.LogInformation("Building MXE toolchain and libraries (gcc, curl, openssl, etc.)...");
        string buildToolchainAndLibsCmd = $"{mxeEnv} && {mxeMakeCommand} binutils gcc curl openssl pcre zlib expat";
        await _processUtil.BashRun(buildToolchainAndLibsCmd, "", tempDir, cancellationToken);

        // 5) extract Git source
        _logger.LogInformation("Extracting Git source...");
        string tarSnippet = $"{ReproEnv} tar --sort=name --mtime=@1620000000 --owner=0 --group=0 --numeric-owner -xzf {archivePath}";
        await _processUtil.BashRun(tarSnippet, "", tempDir, cancellationToken);

        var candidates = Directory.GetDirectories(tempDir, "git-*");

        if (candidates.Length != 1)
            throw new Exception($"Expected exactly one git-<tag> folder in {tempDir}, found {candidates.Length}");

        string extractPath = candidates[0];
        _logger.LogInformation("Extracted Git source to {path}", extractPath);

        // 6) patch config.mak
        _logger.LogInformation("Patching config.mak.sample to fold helpers into built-in git.exe...");
        string gitDir = extractPath.Replace(':', '/');

        string patchSnippet = $"cd {gitDir} && cp config.mak.dev config.mak && sed -i -E 's/^BUILTIN_LIST = (.*)$/BUILTIN_LIST = \\1 remote-https remote-ssh credential-manager http-backend/' config.mak";
        await _processUtil.BashRun(patchSnippet, "", tempDir, cancellationToken);

        // 7) generate configure script
        _logger.LogInformation("Generating configure script...");
        string makeConfigureSnippet = $"{ReproEnv} cd {gitDir} && make configure";
        await _processUtil.BashRun(makeConfigureSnippet, "", tempDir, cancellationToken);

        // 8) configure for Windows cross-compile
        _logger.LogInformation("Configuring for Windows cross-compile…");

        string mxeBin = Path.Combine(mxeCache, "usr", "bin");
        string mxeTargetRoot = Path.Combine(mxeCache, "usr", "x86_64-w64-mingw32.static");
        string includePath = Path.Combine(mxeTargetRoot, "include");
        string libPath = Path.Combine(mxeTargetRoot, "lib");

        // START >> THE DEFINITIVE FIX
        // The root cause is the `socklen_t` configure test being incompatible with cross-compilation.
        // The correct fix is to bypass this specific test by providing a cached answer: `ac_cv_type_socklen_t=yes`.
        // We still provide the other flags to ensure the rest of the configure script and the final compile work correctly.
        string configureSnippet =
            $"cd {gitDir} && " +
            $"PATH=\"{mxeBin}:$PATH\" " +
            "ac_cv_iconv_omits_bom=no " +
            "ac_cv_fread_reads_directories=yes " +
            "ac_cv_snprintf_returns_bogus=no " +
            "ac_cv_type_socklen_t=yes " + // <-- This is the definitive fix for the blocker.
            "./configure --host=x86_64-w64-mingw32.static " +
            "--prefix=/usr " +
            "CC=x86_64-w64-mingw32.static-gcc " +
            "CFLAGS=\"-static -O2 -pipe -DNO_POSIX_SOCKETS\" " +
            $"LDFLAGS=\"-static -L{libPath}\" " +
            $"LIBS=\"-lcurl -lssl -lcrypto -lz -lws2_32 -lpsapi -lcrypt32 -lsecur32\"";
        // END >> THE DEFINITIVE FIX

        await _processUtil.BashRun(configureSnippet, "", tempDir, cancellationToken);

        // 9) compile
        _logger.LogInformation("Building Git for Windows...");
        string compileSnippet = $"{ReproEnv} cd {gitDir} && make -j{Environment.ProcessorCount}";
        await _processUtil.BashRun(compileSnippet, "", tempDir, cancellationToken);

        // 10) install into staging directory
        _logger.LogInformation("Installing Git into staging dir…");
        string stagingDir = Path.Combine(tempDir, "install");
        string installSnippet = $"{ReproEnv} cd {gitDir} && make install DESTDIR={stagingDir}";
        await _processUtil.BashRun(installSnippet, "", tempDir, cancellationToken);

        _logger.LogInformation("Locating the 'git' executable in the staging directory…");
        var foundFiles = Directory.GetFiles(stagingDir, "git", SearchOption.AllDirectories)
            .Where(f => !new FileInfo(f).Attributes.HasFlag(FileAttributes.Directory) && Path.GetFileName(Path.GetDirectoryName(f)) == "bin")
            .ToArray();

        if (foundFiles.Length == 0)
            throw new FileNotFoundException("The executable 'git' was not found in any 'bin' sub-directory of the staging area.", stagingDir);

        string originalGitPath = foundFiles[0];
        string gitExe = originalGitPath + ".exe";

        _logger.LogInformation("Renaming '{original}' to '{newName}'", originalGitPath, gitExe);
        File.Move(originalGitPath, gitExe);

        // 11) strip the installed exe
        _logger.LogInformation("Stripping git.exe at {path}", gitExe);
        string crossStrip = Path.Combine(mxeBin, "x86_64-w64-mingw32.static-strip");
        string stripSnippet = $"{ReproEnv} {crossStrip} {gitExe}";
        await _processUtil.BashRun(stripSnippet, "", tempDir, cancellationToken);

        if (!File.Exists(gitExe))
            throw new FileNotFoundException("git.exe not found after install and strip", gitExe);

        _logger.LogInformation("Built static git.exe at {path}", gitExe);
        return gitExe;
    }

    public async ValueTask<string> GetLatestStableGitTag(CancellationToken cancellationToken = default)
    {
        HttpClient client = await _httpClientCache.Get(nameof(BuildLibraryUtil), cancellationToken: cancellationToken);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DotNetGitTool/1.0");

        JsonElement[]? tags = await client.GetFromJsonAsync<JsonElement[]>("https://api.github.com/repos/git/git/tags", cancellationToken);

        if (tags == null)
            throw new InvalidOperationException("Could not fetch tags from GitHub API.");

        foreach (var tag in tags)
        {
            string? name = tag.GetProperty("name").GetString();

            if (name != null && !name.Contains("-rc", StringComparison.OrdinalIgnoreCase) && !name.Contains("-beta", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("-alpha", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        throw new InvalidOperationException("No stable Git version found.");
    }
}