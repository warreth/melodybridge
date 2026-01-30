using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure;

public class PythonRunner
{
    private readonly ILogger<PythonRunner> _logger;

    public PythonRunner(ILogger<PythonRunner> logger)
    {
        _logger = logger;
    }

    public string RunPythonScript(string scriptPath, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"{scriptPath} {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    errorBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                _logger.LogError("Python script error: {Error}", errorBuilder.ToString());
                throw new Exception($"Python script failed with exit code {process.ExitCode}");
            }

            return outputBuilder.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run Python script");
            throw;
        }
    }
}