using System.Diagnostics;
using System.Net.Sockets;

namespace Suru.Compiler.Testing;

/// <summary>What one execution of a test build reported.</summary>
/// <param name="Frames">Every frame that came off the channel, in the order it arrived.</param>
/// <param name="Output">The program's own stdout, verbatim — no records in it to sieve out.</param>
/// <param name="ExitCode">The process's exit code, which is the truth about the process.</param>
public sealed record ChannelRun(IReadOnlyList<Frame> Frames, string Output, int ExitCode);

/// <summary>
/// Runs a test build and reads what it says.
/// <para>
/// The driver listens first and starts the child second, handing it the socket path in
/// <see cref="RuntimeShim.ChannelVariable"/>. The program's own output stays on stdout and its
/// records travel the socket, so the two no longer have to be told apart — which is the whole
/// reason the channel exists.
/// </para>
/// <para>
/// POSIX only, and said out loud: <c>AF_UNIX</c> on both sides. <c>suru build</c> is unaffected,
/// so the only claim narrowed is that <c>suru test</c> needs a POSIX host with a <c>cc</c>,
/// which it already needed for the <c>cc</c>.
/// </para>
/// </summary>
public static class TestChannel
{
    /// <summary>
    /// A conservative bound on a unix socket path. <c>sun_path</c> is 108 bytes on Linux and
    /// 104 on macOS, and the shim's own over-length guard is a <i>silent</i> return — so a path
    /// too long would surface as "never connected" and send you looking at the linker. Checked
    /// here instead, and named.
    /// </summary>
    private const int SocketPathMax = 100;

    private const int ChunkSize = 8192;

    /// <summary>
    /// Runs the executable to completion and returns what it reported.
    /// <para>
    /// Synchronous, because the pipeline around it is. The work underneath is async and is
    /// entered through <see cref="Task.Run(Func{Task{ChannelRun}})"/> rather than blocked on
    /// directly: a test host installs a synchronization context, and blocking on async under
    /// one is the classic deadlock.
    /// </para>
    /// </summary>
    /// <exception cref="TestChannelException">The channel failed; the message says how.</exception>
    /// <exception cref="FrameException">The channel said something that is not a frame.</exception>
    public static ChannelRun Run(string executablePath, TestOptions options) =>
        Task.Run(() => RunAsync(executablePath, options)).GetAwaiter().GetResult();

    private static async Task<ChannelRun> RunAsync(string executablePath, TestOptions options)
    {
        var socketPath = SocketPath();

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);

            // Accepting is armed before the child exists, so a shim that connects the instant it
            // starts is met rather than raced. A backlog of one is enough: there is one child.
            using var connect = new CancellationTokenSource();
            var accepting = listener.AcceptAsync(connect.Token).AsTask();

            using var process = Start(executablePath, socketPath);

            // The deadline starts when the child does, so 200 ms means 200 ms of its life.
            connect.CancelAfter(options.Connect);

            using var channel = await Accept(accepting, process, options);
            return await Drain(channel, process, options);
        }
        finally
        {
            // Disposing a socket does not unlink its path, and a temp file we failed to remove
            // must never replace the diagnostic that brought us here.
            try { File.Delete(socketPath); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Short, and deliberately not nested in a directory of its own: every byte counts against
    /// <see cref="SocketPathMax"/>, and <see cref="Path.GetTempPath"/> honours <c>TMPDIR</c>,
    /// which on macOS is already <c>/var/folders/…/T/</c>. The guid is what lets two runs — and
    /// two test threads — share a temp directory.
    /// </summary>
    private static string SocketPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"suru-{Guid.NewGuid():N}"[..17] + ".sock");
        var bytes = System.Text.Encoding.UTF8.GetByteCount(path);

        if (bytes > SocketPathMax)
            throw new TestChannelException(
                $"The test channel's socket path is too long for this platform: '{path}' is " +
                $"{bytes} bytes and the limit is {SocketPathMax}. Set TMPDIR to a shorter directory.");

        return path;
    }

    private static Process Start(string executablePath, string socketPath)
    {
        var info = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        info.Environment[RuntimeShim.ChannelVariable] = socketPath;

        return Process.Start(info)!;
    }

    /// <summary>
    /// Waits for the child to reach the channel. A child that has already exited and one that is
    /// still running are different failures — a shim that did not link versus a program stuck
    /// before its first statement — and they get different sentences.
    /// </summary>
    private static async Task<Socket> Accept(Task<Socket> accepting, Process process, TestOptions options)
    {
        try
        {
            return await accepting;
        }
        catch (OperationCanceledException)
        {
            if (process.HasExited)
                throw new TestChannelException(
                    $"The program exited with {process.ExitCode} without ever connecting to " +
                    "the test channel.");

            Kill(process);
            throw new TestChannelException(
                $"The program did not reach the test channel within " +
                $"{options.Connect.TotalMilliseconds:0}ms. Raise it with --connect-timeout=<ms>.");
        }
    }

    /// <summary>
    /// Reads the socket, the child's stdout and its exit status <b>at the same time</b>.
    /// <para>
    /// Reading one to the end before starting the other deadlocks the moment the other fills at
    /// around 64 KB: the child blocks writing the pipe nobody is draining, and we block reading
    /// the one it is no longer writing. This is the first place the compiler has had two pipes
    /// to lose that race with.
    /// </para>
    /// </summary>
    private static async Task<ChannelRun> Drain(Socket channel, Process process, TestOptions options)
    {
        using var deadline = new CancellationTokenSource(options.Run);

        var frames = ReadFrames(channel, deadline.Token);
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var exited = process.WaitForExitAsync(deadline.Token);

        try
        {
            await Task.WhenAll(frames, output, exited);
        }
        catch (OperationCanceledException)
        {
            // Killing it closes our end under a child that may be mid-write. The shim ignores
            // SIGPIPE for exactly this: a harness that has gone away must not kill the child
            // with a signal the driver would then read as a crash.
            Kill(process);
            throw new TestChannelException(
                $"The program connected but did not finish within " +
                $"{options.Run.TotalMilliseconds:0}ms, and was killed. Raise it with --timeout=<ms>.");
        }

        return new ChannelRun(await frames, await output, process.ExitCode);
    }

    private static async Task<IReadOnlyList<Frame>> ReadFrames(Socket channel, CancellationToken token)
    {
        var reader = new FrameReader();
        var frames = new List<Frame>();
        var buffer = new byte[ChunkSize];

        while (true)
        {
            var read = await channel.ReceiveAsync(buffer, SocketFlags.None, token);
            if (read == 0)
                break;

            frames.AddRange(reader.Feed(buffer.AsSpan(0, read)));
        }

        // A clean end of stream that is not a frame boundary is a program that stopped
        // mid-sentence — the one thing Content-Length lets a reader notice.
        if (!reader.AtFrameBoundary)
            throw new TestChannelException(
                "The test channel ended in the middle of a frame: the program stopped mid-message.");

        return frames;
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }
}
