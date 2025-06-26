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

    public async ValueTask Process(CancellationToken cancellationToken)
    {
        string gitDirectory =
            await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}", cancellationToken);

        string downloadDir = await _directoryUtil.CreateTempDirectory(cancellationToken);

        string? asset = await _releasesUtil.DownloadReleaseAssetByNamePattern("git-for-windows", "git", downloadDir, ["MinGit", "64-bit"], cancellationToken);

        if (asset == null)
            throw new FileNotFoundException("Could not find the required Git for Windows Portable asset.");

        string extractionDir = await _sevenZipCompressionUtil.Extract(asset, cancellationToken);

        bool needToUpdate = await CheckForHashDifferences(extractionDir, gitDirectory, cancellationToken);

        if (!needToUpdate)
            return;

        await BuildPackAndPush(gitDirectory, extractionDir, cancellationToken);

        await SaveHashToGitRepo(gitDirectory, cancellationToken);
    }

    private async ValueTask BuildPackAndPush(string gitDirectory, string extractionDir, CancellationToken cancellationToken)
    {
        string destinationDir = Path.Combine(gitDirectory, "src", "Resources", "win-x64", "git");

        _directoryUtil.CreateIfDoesNotExist(destinationDir);

        _fileUtilSync.DeleteAll(destinationDir);

        await _fileUtil.CopyRecursively(extractionDir, destinationDir, cancellationToken: cancellationToken);

        string projFilePath = Path.Combine(gitDirectory, "src", $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            _logger.LogError("Build was not successful, exiting...");
            return;
        }

        string version = EnvironmentUtil.GetVariableStrict("BUILD_VERSION");

        await _dotnetUtil.Pack(projFilePath, version, true, "Release", false, false, gitDirectory, cancellationToken: cancellationToken);

        string apiKey = EnvironmentUtil.GetVariableStrict("NUGET__TOKEN");

        string nuGetPackagePath = Path.Combine(gitDirectory, $"{Constants.Library}.{version}.nupkg");

        await _dotnetNuGetUtil.Push(nuGetPackagePath, apiKey: apiKey, cancellationToken: cancellationToken);
    }

    private async ValueTask<bool> CheckForHashDifferences(string inputDirectory, string gitDirectory, CancellationToken cancellationToken)
    {
        string? oldHash = await _fileUtil.TryRead(Path.Combine(gitDirectory, "hash.txt"), true, cancellationToken);

        if (oldHash == null)
        {
            _logger.LogDebug("Could not read hash from repository, proceeding to update...");
            return true;
        }

        _newHash = await _sha3Util.HashDirectory(inputDirectory, true, cancellationToken);

        if (oldHash == _newHash)
        {
            _logger.LogInformation("Hashes are equal, no need to update, exiting...");
            return false;
        }

        return true;
    }

    private async ValueTask SaveHashToGitRepo(string gitDirectory, CancellationToken cancellationToken)
    {
        string targetHashFile = Path.Combine(gitDirectory, "hash.txt");

        _fileUtilSync.DeleteIfExists(targetHashFile);

        await _fileUtil.Write(targetHashFile, _newHash!, true, cancellationToken);

        _fileUtilSync.DeleteIfExists(Path.Combine(gitDirectory, "src", "Resources", Constants.FileName));

        _gitUtil.AddIfNotExists(gitDirectory, targetHashFile);

        if (_gitUtil.IsRepositoryDirty(gitDirectory))
        {
            _logger.LogInformation("Changes have been detected in the repository, commiting and pushing...");

            string name = EnvironmentUtil.GetVariableStrict("NAME");
            string email = EnvironmentUtil.GetVariableStrict("EMAIL");
            string username = EnvironmentUtil.GetVariableStrict("USERNAME");
            string token = EnvironmentUtil.GetVariableStrict("GH__TOKEN");

            _gitUtil.Commit(gitDirectory, "Updates hash for new version", name, email);

            await _gitUtil.Push(gitDirectory, token, cancellationToken);
        }
        else
        {
            _logger.LogInformation("There are no changes to commit");
        }
    }
}