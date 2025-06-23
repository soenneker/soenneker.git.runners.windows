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
        // MSYS2 root & bins
        private const string MsysRoot = @"C:\msys64";
        private static readonly string MsysBin = Path.Combine(MsysRoot, "usr", "bin");
        private static readonly string MingwBin = Path.Combine(MsysRoot, "mingw64", "bin");

        private const string InstallMsys2 = "choco install -y msys2";

        // Run under MINGW64 shell so pacman and build tools target mingw64
        private const string PacmanSync =
            @"bash -lc ""export MSYSTEM=MINGW64; pacman -Sy --noconfirm""";
        private const string PacmanUpgrade =
            @"bash -lc ""export MSYSTEM=MINGW64; MSYS2_ARG_CONV_EXCL='*' pacman -Su --noconfirm""";

        private const string PacmanDependencies =
            @"bash -lc ""export MSYSTEM=MINGW64; pacman -Sy --noconfirm --needed " +
            "mingw-w64-x86_64-toolchain base-devel mingw-w64-x86_64-curl " +
            "mingw-w64-x86_64-libiconv mingw-w64-x86_64-expat " +
            "mingw-w64-x86_64-openssl mingw-w64-x86_64-zlib " +
            "autoconf automake-wrapper libtool " +
            "mingw-w64-x86_64-pcre2\"";

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
            // 1) make a clean temp dir
            string tempDir = await _directoryUtil.CreateTempDirectory(cancellationToken);

            // 2) install msys2 if missing
            if (!File.Exists(Path.Combine(MsysBin, "bash.exe")))
            {
                _logger.LogInformation("Installing MSYS2 via Chocolatey...");
                await _processUtil.CmdRun(InstallMsys2, tempDir, cancellationToken);
            }

            // 3) ensure both usr/bin and mingw64/bin are on PATH for our child processes
            var path = Environment.GetEnvironmentVariable("PATH")!;
            var parts = path.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!parts.Contains(MsysBin, StringComparer.OrdinalIgnoreCase)) parts.Insert(0, MsysBin);
            if (!parts.Contains(MingwBin, StringComparer.OrdinalIgnoreCase)) parts.Insert(0, MingwBin);
            Environment.SetEnvironmentVariable("PATH", string.Join(';', parts));

            // 4) bootstrap MSYS2: sync DB, upgrade, then install build deps
            _logger.LogInformation("Synchronizing pacman database...");
            await _processUtil.CmdRun(PacmanSync, tempDir, cancellationToken);

            _logger.LogInformation("Upgrading MSYS2 core packages...");
            await _processUtil.CmdRun(PacmanUpgrade, tempDir, cancellationToken);

            _logger.LogInformation("Installing build dependencies...");
            await _processUtil.CmdRun(PacmanDependencies, tempDir, cancellationToken);

            // 5) fetch latest stable Git tag
            string latestVersion = await GetLatestStableGitTag(cancellationToken);
            _logger.LogInformation("Latest stable Git version: {version}", latestVersion);

            // 6) download & sanity-check the tarball
            string archivePath = Path.Combine(tempDir, "git.tar.gz");
            string downloadUrl = $"https://github.com/git/git/archive/refs/tags/{latestVersion}.tar.gz";
            _logger.LogInformation("Downloading Git source from {url}", downloadUrl);
            await DownloadWithRetry(downloadUrl, archivePath, cancellationToken);
            ValidateGzip(archivePath);

            // 7) extract
            string msysTemp = ToMsysPath(tempDir);
            string msysArchive = ToMsysPath(archivePath);
            _logger.LogInformation("Extracting Git source...");
            await _processUtil.CmdRun(
                $@"bash -lc ""export MSYSTEM=MINGW64; tar -xzf {msysArchive} -C {msysTemp}""",
                tempDir, cancellationToken);

            string gitSrcWin = Path.Combine(tempDir, $"git-{latestVersion.TrimStart('v')}");
            string gitSrcMsys = ToMsysPath(gitSrcWin);

            // 8) clean + create staging dir
            string distRoot = Path.Combine(tempDir, "dist");
            if (Directory.Exists(distRoot)) Directory.Delete(distRoot, recursive: true);
            Directory.CreateDirectory(distRoot);
            string distMsys = ToMsysPath(distRoot);

            // 9) configure → make → install
            _logger.LogInformation("Configuring & building Git from source...");
            string buildCmd =
                $@"export MSYSTEM=MINGW64; " +
                "export PKG_CONFIG='pkg-config --static'; " +
                "export LIBRARY_PATH=/mingw64/lib:$LIBRARY_PATH; " +
                "export CFLAGS='-O2 -pipe -static -static-libgcc -static-libstdc++ -DCURL_STATICLIB'; " +
                "export LDFLAGS='-static -static-libgcc -static-libstdc++ -s'; " +
                "export PATH=/mingw64/bin:/usr/bin:$PATH; " +
                $"cd {gitSrcMsys}; " +
                  // create config.mak so sub-makes keep the flags
                    "printf '%s\\n' " +
                 "'NO_TCLTK=YesPlease' " +
                 "'NO_GETTEXT=YesPlease' "+
                 "'USE_LIBPCRE2=Yes' "+
                     "'EXTLIBS += -lws2_32 -lcrypt32 -lbcrypt -lz -lshlwapi'\" "+
                     "'CFLAGS  += -O2 -pipe -static -static-libgcc -static-libstdc++ -DCURL_STATICLIB'\" "+
                     "'LDFLAGS += -static -static-libgcc -static-libstdc++ -s'\" "+
                "> config.mak; " +
                "make configure; " +
                "./configure --prefix=/mingw64 --with-openssl --with-curl --with-pcre2; " +
                $"make -j{Environment.ProcessorCount} V=1; " +
                $"make install DESTDIR={distMsys}";
            await _processUtil.CmdRun($@"bash -lc ""{buildCmd}""", tempDir, cancellationToken);

            // 10) grab the result
            string binDir = Path.Combine(distRoot, "mingw64", "bin");
            var gitExe = Directory
                .EnumerateFiles(binDir, "git.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();

            if (string.IsNullOrEmpty(gitExe))
                throw new FileNotFoundException(
                    "git.exe not found after building from source", binDir);

            _logger.LogInformation("Successfully built git.exe at {path}", gitExe);
            return gitExe;
        }

        private async Task DownloadWithRetry(
            string url, string dst, CancellationToken ct)
        {
            const int MAX = 3;
            var delay = TimeSpan.FromSeconds(2);
            for (int i = 1; i <= MAX; i++)
            {
                try
                {
                    await _fileDownloadUtil.Download(url, dst, cancellationToken: ct);
                    return;
                }
                catch (Exception ex) when (i < MAX)
                {
                    _logger.LogWarning(
                        ex,
                        "Download attempt {i}/{MAX} failed; retrying in {s}s",
                        i, MAX, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    delay *= 2;
                }
            }
            throw new InvalidOperationException($"Could not download {url} after {MAX} tries");
        }

        private static void ValidateGzip(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            if (fs.Length < 2 || fs.ReadByte() != 0x1F || fs.ReadByte() != 0x8B)
                throw new InvalidDataException($"{path} is not a valid gzip archive");
        }

        private static string ToMsysPath(string win)
        {
            // C:\… → /c/…
            var p = win.Replace('\\', '/');
            if (p.Length >= 2 && p[1] == ':')
            {
                var d = char.ToLowerInvariant(p[0]);
                p = $"/{d}{p.Substring(2)}";
            }
            return p;
        }

        public async ValueTask<string> GetLatestStableGitTag(
            CancellationToken cancellationToken = default)
        {
            var cli = await _httpClientCache.Get(
                nameof(BuildLibraryUtil),
                cancellationToken: cancellationToken);
            cli.DefaultRequestHeaders.UserAgent.ParseAdd("DotNetGitTool/1.0");

            var tags = await cli.GetFromJsonAsync<JsonElement[]>(
                "https://api.github.com/repos/git/git/tags",
                cancellationToken);

            if (tags == null) throw new InvalidOperationException("No tags from GitHub");

            foreach (var t in tags)
            {
                var n = t.GetProperty("name").GetString();
                if (n != null
                    && !n.Contains("-rc", StringComparison.OrdinalIgnoreCase)
                    && !n.Contains("-beta", StringComparison.OrdinalIgnoreCase)
                    && !n.Contains("-alpha", StringComparison.OrdinalIgnoreCase))
                {
                    return n;
                }
            }

            throw new InvalidOperationException("No stable Git tag found");
        }
    }
}
