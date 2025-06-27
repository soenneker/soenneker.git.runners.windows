using Microsoft.Extensions.Logging;
using Soenneker.Compression.SevenZip.Abstract;
using Soenneker.Extensions.String;
using Soenneker.Git.Runners.Windows.Utils.Abstract;
using Soenneker.Git.Util.Abstract;
using Soenneker.GitHub.Repositories.Releases.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Dotnet.NuGet.Abstract;
using Soenneker.Utils.Environment;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.FileSync.Abstract;
using Soenneker.Utils.SHA3.Abstract;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Git.Runners.Windows.Utils;

///<inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IDotnetNuGetUtil _dotnetNuGetUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IFileUtilSync _fileUtilSync;
    private readonly ISha3Util _sha3Util;
    private readonly IGitHubRepositoriesReleasesUtil _releasesUtil;
    private readonly ISevenZipCompressionUtil _sevenZipCompressionUtil;

    private string? _newHash;

    public FileOperationsUtil(IFileUtil fileUtil, ILogger<FileOperationsUtil> logger, IGitUtil gitUtil, IDotnetUtil dotnetUtil,
        IDotnetNuGetUtil dotnetNuGetUtil, IDirectoryUtil directoryUtil, IFileUtilSync fileUtilSync, ISha3Util sha3Util,
        IGitHubRepositoriesReleasesUtil releasesUtil, ISevenZipCompressionUtil sevenZipCompressionUtil)
    {
        _fileUtil = fileUtil;
        _logger = logger;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _dotnetNuGetUtil = dotnetNuGetUtil;
        _directoryUtil = directoryUtil;
        _fileUtilSync = fileUtilSync;
        _sha3Util = sha3Util;
        _releasesUtil = releasesUtil;
        _sevenZipCompressionUtil = sevenZipCompressionUtil;
    }

    public async ValueTask<string?> Process(CancellationToken cancellationToken)
    {
        string gitDirectory =
            await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}", cancellationToken);

        string downloadDir = await _directoryUtil.CreateTempDirectory(cancellationToken);

        string? asset = await _releasesUtil.DownloadReleaseAssetByNamePattern("git-for-windows", "git", downloadDir, ["MinGit", "64-bit"], cancellationToken);

        if (asset == null)
            throw new FileNotFoundException("Could not find the required Git for Windows Portable asset.");

        return await _sevenZipCompressionUtil.Extract(asset, cancellationToken);
    }
}