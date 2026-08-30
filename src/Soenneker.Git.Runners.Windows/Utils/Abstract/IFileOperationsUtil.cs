using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Git.Runners.Windows.Utils.Abstract;

/// <summary>
/// Downloads and extracts the Windows Git distribution consumed by the runner.
/// </summary>
public interface IFileOperationsUtil
{
    /// <summary>
    /// Downloads and extracts the selected Git for Windows release asset.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The path to the extracted distribution directory.</returns>
    ValueTask<string> Process(CancellationToken cancellationToken = default);
}
