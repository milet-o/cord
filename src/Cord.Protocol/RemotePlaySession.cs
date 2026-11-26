using System.Net;
using Cord.Protocol.Crypto;
using Cord.Protocol.Registration;
using Cord.Protocol.Session;
using Cord.Protocol.Stream;

namespace Cord.Protocol;

/// <summary>
/// Runs the complete connection sequence to a registered, awake console:
/// session request, ctrl channel, senkusha and stream connection (docs/protocol/README.md).
/// </summary>
public sealed class RemotePlaySession : IAsyncDisposable
{
    private static readonly TimeSpan SessionIdTimeout = TimeSpan.FromSeconds(10);

    private readonly RpCipher _cipher;

    private RemotePlaySession(CtrlChannel ctrl, StreamConnection stream, RpCipher cipher, NetworkProbe network)
    {
        Ctrl = ctrl;
        Stream = stream;
        _cipher = cipher;
        Network = network;
    }

    public CtrlChannel Ctrl { get; }

    public StreamConnection Stream { get; }

    public NetworkProbe Network { get; }

    /// <summary>Completes with a reason when either channel ends.</summary>
    public async Task<string> WaitForEndAsync()
    {
        var ctrlClosed = Ctrl.Closed.ContinueWith(_ => "The console closed the control channel.", TaskScheduler.Default);
        return await await Task.WhenAny(Stream.Disconnected, ctrlClosed);
    }

    public static async Task<RemotePlaySession> ConnectAsync(
        RpCryptTables tables,
        IPAddress console,
        RegisteredConsole credentials,
        StreamOptions options,
        IStreamSink sink,
        LoginPasscodeProvider? passcodeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var nonce = await SessionRequest.RequestNonceAsync(new IPEndPoint(console, SessionRequest.DefaultPort), credentials.RegistKey, cancellationToken);
        var cipher = RpCipher.ForSession(tables, nonce, credentials.RpKey);
        var secrets = SessionSecrets.Generate();

        CtrlChannel? ctrl = null;
        try
        {
            ctrl = await CtrlChannel.ConnectAsync(new IPEndPoint(console, CtrlChannel.DefaultPort), credentials.RegistKey, cipher, secrets, options.Codec, cancellationToken);
            var sessionId = await ctrl.WaitForSessionIdAsync(passcodeProvider, SessionIdTimeout, cancellationToken);
            await ctrl.EnableFeaturesAsync(options.DualSense, cancellationToken);

            var network = await ProbeNetworkAsync(console, cancellationToken);
            var stream = await StreamConnection.ConnectAsync(
                new IPEndPoint(console, StreamConnection.DefaultPort), options, cipher, secrets, sessionId, network, sink, cancellationToken);
            return new RemotePlaySession(ctrl, stream, cipher, network);
        }
        catch
        {
            if (ctrl is not null)
                await ctrl.DisposeAsync();
            cipher.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        await Task.WhenAny(Ctrl.Closed, Task.Delay(350));
        await Ctrl.DisposeAsync();
        _cipher.Dispose();
    }

    private static async Task<NetworkProbe> ProbeNetworkAsync(IPAddress console, CancellationToken cancellationToken)
    {
        try
        {
            return await Senkusha.RunAsync(new IPEndPoint(console, Senkusha.DefaultPort), cancellationToken);
        }
        catch (RemotePlayException)
        {
            return NetworkProbe.LanDefault;
        }
    }
}
