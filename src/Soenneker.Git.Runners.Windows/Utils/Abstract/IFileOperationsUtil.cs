using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Git.Runners.Windows.Utils.Abstract;

/// <summary>
/// Defines the file operations util contract.
/// </summary>
public interface IFileOperationsUtil
{
    /// <summary>
    /// Executes the process operation.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task containing the result of the operation.</returns>
    ValueTask<string?> Process(CancellationToken cancellationToken = default);
}