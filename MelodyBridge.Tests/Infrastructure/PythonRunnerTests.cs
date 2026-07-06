using MelodyBridge.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class PythonRunnerTests
{
    [Test]
    public void Constructor_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => new PythonRunner(NullLogger<PythonRunner>.Instance));
    }

    [Test]
    public void RunPythonScript_PythonNotFound_Throws()
    {
        var runner = new PythonRunner(NullLogger<PythonRunner>.Instance);

        // Should throw since "python" may not be available or script doesn't exist
        Assert.Throws<Exception>(() =>
            runner.RunPythonScript("/nonexistent/script.py", ""));
    }

    [Test]
    public void RunPythonScript_NullArguments_Throws()
    {
        var runner = new PythonRunner(NullLogger<PythonRunner>.Instance);

        Assert.Throws<Exception>(() =>
            runner.RunPythonScript("", "test"));
    }
}
