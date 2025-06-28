using Microsoft.Extensions.Logging;
using Soenneker.Compression.SevenZip.Abstract;
using Soenneker.Git.Runners.Windows.Utils.Abstract;
using Soenneker.GitHub.Repositories.Releases.Abstract;
using Soenneker.Utils.Directory.Abstract;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Git.Runners.Windows.Utils;

///<inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IGitHubRepositoriesReleasesUtil _releasesUtil;
    private readonly ISevenZipCompressionUtil _sevenZipCompressionUtil;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IDirectoryUtil directoryUtil, IGitHubRepositoriesReleasesUtil releasesUtil,
        ISevenZipCompressionUtil sevenZipCompressionUtil)
    {
        _logger = logger;
        _directoryUtil = directoryUtil;
        _releasesUtil = releasesUtil;
        _sevenZipCompressionUtil = sevenZipCompressionUtil;
    }

    public async ValueTask<string?> Process(CancellationToken cancellationToken)
    {
        string downloadDir = await _directoryUtil.CreateTempDirectory(cancellationToken);

        string? asset = await _releasesUtil.DownloadReleaseAssetByNamePattern("git-for-windows", "git", downloadDir, ["MinGit", "64-bit"], cancellationToken);

        if (asset == null)
            throw new FileNotFoundException("Could not find the required Git for Windows Portable asset.");

        return await _sevenZipCompressionUtil.Extract(asset, cancellationToken);
    }
}