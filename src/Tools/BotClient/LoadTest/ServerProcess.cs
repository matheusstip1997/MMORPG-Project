using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace BotClient.LoadTest;

/// <summary>
/// A GameServer or Auth API child process of the load test: the built DLL run with <c>dotnet</c> from the repository root,
/// its settings in the process environment only (secrets are never written), stdout and stderr to log files. Lines
/// that report errors are counted for the pass criteria.
/// </summary>
public sealed class ServerProcess : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _out;
    private readonly StreamWriter _err;
    private readonly Func<string, bool> _isError;
    private readonly ManualResetEventSlim _ready = new();
    private readonly string _readyLine;
    private int _errorLines;

    private ServerProcess(string name, Process process, string logDirectory, string readyLine, Func<string, bool> isError)
    {
        Name = name;
        _process = process;
        _readyLine = readyLine;
        _isError = isError;
        _out = new StreamWriter(Path.Combine(logDirectory, name + ".log")) { AutoFlush = true };
        _err = new StreamWriter(Path.Combine(logDirectory, name + ".err")) { AutoFlush = true };
    }

    public string Name { get; }

    public int ErrorLines => Volatile.Read(ref _errorLines);

    public bool HasExited => _process.HasExited;

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public List<string> FirstErrors { get; } = [];

    public static ServerProcess Start(string name, string dll, string workingDirectory, string logDirectory,
        IReadOnlyDictionary<string, string> environment, string readyLine, Func<string, bool> isError)
    {
        if (!File.Exists(dll))
            throw new FileNotFoundException($"{name} is not built: {dll} (dotnet build it first).", dll);
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(dll);
        foreach (var (key, value) in environment)
            info.Environment[key] = value;
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var server = new ServerProcess(name, process, logDirectory, readyLine, isError);
        process.OutputDataReceived += (_, e) => server.OnLine(e.Data, error: false);
        process.ErrorDataReceived += (_, e) => server.OnLine(e.Data, error: true);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return server;
    }

    /// <summary>Waits for the ready line on stdout; false if the process exits or the time runs out first.</summary>
    public bool WaitReady(TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (_ready.Wait(100))
                return true;
            if (_process.HasExited)
                return false;
        }
        return _ready.IsSet;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }
        }
        catch (InvalidOperationException)
        {
            // never started
        }
        _process.Dispose();
        lock (_out)
        {
            _out.Dispose();
            _err.Dispose();
        }
        _ready.Dispose();
    }

    private void OnLine(string? line, bool error)
    {
        if (line == null)
            return;
        lock (_out)
        {
            try
            {
                (error ? _err : _out).WriteLine(line);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            if (_isError(line))
            {
                _errorLines++;
                if (FirstErrors.Count < 10)
                    FirstErrors.Add(line);
            }
        }
        if (!error && line.Contains(_readyLine, StringComparison.Ordinal))
            _ready.Set();
    }

    /// <summary>A free TCP port on the loopback (for the Auth API).</summary>
    public static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A free UDP port (for the GameServer, which binds every address).</summary>
    public static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
