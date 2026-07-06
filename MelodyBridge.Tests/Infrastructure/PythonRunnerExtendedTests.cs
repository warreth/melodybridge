using MelodyBridge.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class PythonRunnerExtendedTests
{
    [Test]
    public void RunPythonScript_NonExistentScript_Throws()
    {
        var runner = new PythonRunner(NullLogger<PythonRunner>.Instance);
        var scriptPath = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid() + ".py");

        Assert.Throws<Exception>(() => runner.RunPythonScript(scriptPath, ""));
    }

    [Test]
    public void RunPythonScript_NullArguments_Throws()
    {
        var runner = new PythonRunner(NullLogger<PythonRunner>.Instance);
        var scriptPath = Path.Combine(Path.GetTempPath(), "script_" + Guid.NewGuid() + ".py");

        Assert.Throws<Exception>(() => runner.RunPythonScript(scriptPath, null!));
    }

    [Test]
    public void Constructor_WithLogger_Succeeds()
    {
        var runner = new PythonRunner(NullLogger<PythonRunner>.Instance);
        Assert.That(runner, Is.Not.Null);
    }
}
