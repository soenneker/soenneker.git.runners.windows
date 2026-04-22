using Soenneker.Tests.HostedUnit;

namespace Soenneker.Git.Runners.Windows.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class GitWindowsRunnerTests : HostedUnitTest
{
    public GitWindowsRunnerTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
