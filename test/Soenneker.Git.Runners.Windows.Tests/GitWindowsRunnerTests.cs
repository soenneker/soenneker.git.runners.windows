using Soenneker.Tests.FixturedUnit;
using Xunit;

namespace Soenneker.Git.Runners.Windows.Tests;

[Collection("Collection")]
public sealed class GitWindowsRunnerTests : FixturedUnitTest
{
    public GitWindowsRunnerTests(Fixture fixture, ITestOutputHelper output) : base(fixture, output)
    {
    }

    [Fact]
    public void Default()
    {

    }
}
