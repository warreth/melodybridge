using System.Diagnostics;
using System.Runtime.InteropServices;

Console.WriteLine("Starting MelodyBridge desktop wrapper...");

var serverProject = Path.Combine("..", "MelodyBridge.Server");

var psi = new ProcessStartInfo
{
    FileName = "dotnet",
    Arguments = $"run --project {serverProject} --no-launch-profile",
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = false,
};

var proc = Process.Start(psi);
if (proc == null)
{
    Console.WriteLine("Failed to start server process");
    return;
}

_ = Task.Run(() =>
{
    while (!proc.StandardOutput.EndOfStream)
    {
        var line = proc.StandardOutput.ReadLine();
        if (line != null) Console.WriteLine(line);
    }
});

_ = Task.Run(() =>
{
    while (!proc.StandardError.EndOfStream)
    {
        var line = proc.StandardError.ReadLine();
        if (line != null) Console.Error.WriteLine(line);
    }
});

// Wait a bit for the server to start then open browser
await Task.Delay(1500);
var url = "http://localhost:5000";
try
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        Process.Start("xdg-open", url);
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        Process.Start("open", url);
}
catch (Exception ex)
{
    Console.WriteLine($"Failed to open browser: {ex.Message}");
}

Console.WriteLine("Desktop wrapper started. Press Ctrl+C to exit.");
await proc.WaitForExitAsync();