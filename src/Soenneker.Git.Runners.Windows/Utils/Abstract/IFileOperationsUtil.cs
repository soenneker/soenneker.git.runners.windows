using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Git.Runners.Windows.Utils.Abstract;

/// <summary>
/// Defines the file operations util contract.
/// </summary>
public interface IFileOperationsUtil
{
    /// <summary>
    /// Processes the pending work managed by the file operations.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task whose result is the text returned by process.</returns>
    ValueTask<string?> Process(CancellationToken cancellationToken = default);
}
