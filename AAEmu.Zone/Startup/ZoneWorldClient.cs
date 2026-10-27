using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Startup;

public static class ZoneWorldClient
{
    /// <summary>
    /// Performs the native join and initial snapshot exchange on the caller-owned connection.
    /// Each connection gets fresh state. This does not announce a loaded simulation.
    /// </summary>
    public static async Task<ZoneStartupState> SynchronizeAsync(
        Stream connection, ZoneJoin join, TimeSpan timeout, CancellationToken cancellationToken = default,
        WorldContentNameComparer? contentNameComparer = null, Func<uint>? localGameMilliseconds = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var state = new ZoneStartupState(connection, join, contentNameComparer, localGameMilliseconds);
        try
        {
            await connection.WriteAsync(join.ToFrame().Encode(), deadline.Token).ConfigureAwait(false);
            while (!state.IsComplete)
            {
                var frame = await ZoneFrame.ReadAsync(connection, deadline.Token).ConfigureAwait(false);
                if (frame == null)
                    throw new EndOfStreamException("World disconnected before the Zone startup snapshot completed.");
                state.Receive(frame.Value);
            }
            return state;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            state.CloseClocks();
            throw new TimeoutException("World did not complete the Zone startup snapshot before the deadline.", ex);
        }
        catch
        {
            state.CloseClocks();
            throw;
        }
    }
}
