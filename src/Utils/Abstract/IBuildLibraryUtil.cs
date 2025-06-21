using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Git.Runners.Windows.Utils.Abstract;

public interface IBuildLibraryUtil
{
    ValueTask<string> Build(CancellationToken cancellationToken);
}