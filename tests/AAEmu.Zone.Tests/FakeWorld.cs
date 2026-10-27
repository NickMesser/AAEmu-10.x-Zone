using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AAEmu.Zone.Protocol;
using AAEmu.Zone.Protocol.Messages;

namespace AAEmu.Zone.Tests;

/// <summary>What the fake World sends after it decodes ZWJoin.</summary>
public sealed record FakeWorldScript
{
    public ushort JoinReason { get; init; }
    public IReadOnlyList<ZoneFaction> Factions { get; init; } = [];
    public IReadOnlyList<ZoneFactionRelation> Relations { get; init; } = [];
    public IReadOnlyList<PersistentSpawner> Spawners { get; init; } = [];
    /// <summary>Hour for the ToD seed pair; null mirrors an instance with no authored time_of_day.xml.</summary>
    public float? SeedHour { get; init; } = 12.5f;
    public uint WorldGameSeconds { get; init; } = 3600;
    /// <summary>Close the socket after this many post-join frames (null: send everything and keep the socket open).</summary>
    public int? CloseAfterFrames { get; init; }
    /// <summary>With CloseAfterFrames, also write half of the next frame before closing.</summary>
    public bool TruncateNextFrame { get; init; }
    /// <summary>Accept the connection and read ZWJoin, but never answer.</summary>
    public bool Silent { get; init; }
}

/// <summary>
/// In-process stand-in for AAEmu.World's zone listener. It decodes ZWJoin and replays World's real
/// post-join sequence (ZoneProtocolHandler.HandleJoin, AAEmu-10.x
/// AAEmu.WorldServer/AAEmu.World/Core/Network/ZoneProtocolHandler.cs:184-270). Bodies are serialized
/// here from World's layouts, independently of the zone's own encoders, so a decoder drift fails a test.
/// </summary>
public sealed class FakeWorld : IAsyncDisposable
{
    private const int FactionChunk = 20;      // WZFactionListPacket.cs:15 MaxEntriesPerPacket
    private const int RelationChunk = 200;    // WZFactionRelationListPacket.cs:15
    private const int SpawnerChunk = 100;     // WZSpawnerListPacket.cs:21

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<Task> _connections = new();
    private readonly Task _accept;

    public FakeWorld(FakeWorldScript? script = null)
    {
        Script = script ?? new FakeWorldScript();
        _listener.Start();
        _accept = AcceptLoopAsync();
    }

    public FakeWorldScript Script { get; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public ConcurrentQueue<ZoneJoin> Joins { get; } = new();
    /// <summary>Opcodes of every frame this World wrote, in order.</summary>
    public ConcurrentQueue<ushort> SentOpcodes { get; } = new();
    public ConcurrentQueue<Exception> Errors { get; } = new();

    public async Task<TcpClient> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port);
        return client;
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Enqueue(ServeAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using var _ = client;
            var stream = client.GetStream();
            var frame = await ZoneFrame.ReadAsync(stream, _stop.Token);
            if (frame is not { Opcode: ZwOpcodes.Join } join)
                throw new InvalidDataException("Fake World expected ZWJoin as the first frame.");
            Joins.Enqueue(ZoneJoin.Read(join.Body.Span));
            if (Script.Silent)
            {
                await Task.Delay(Timeout.Infinite, _stop.Token);
                return;
            }

            var sent = 0;
            foreach (var packet in BuildJoinSequence())
            {
                if (Script.CloseAfterFrames is { } limit && sent >= limit)
                {
                    if (Script.TruncateNextFrame)
                    {
                        var bytes = packet.Encode();
                        await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2 + 1), _stop.Token);
                    }
                    return; // disposing the client closes the socket
                }
                SentOpcodes.Enqueue(packet.Opcode);
                await stream.WriteAsync(packet.Encode(), _stop.Token);
                sent++;
            }

            // World keeps the session open after the join; wait for the zone or the test to end it.
            await Task.Delay(Timeout.Infinite, _stop.Token);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex) { Errors.Enqueue(ex); }
    }

    /// <summary>World order: ZoneProtocolHandler.cs:202-258.</summary>
    private IEnumerable<ZoneFrame> BuildJoinSequence()
    {
        yield return JoinResponse(Script.JoinReason);                       // :202-203
        if (Script.JoinReason != 0) yield break;                            // zone gives up; World would not send more
        foreach (var f in FactionFrames(Script.Factions)) yield return f;   // :208
        foreach (var f in RelationFrames(Script.Relations)) yield return f; // :209
        foreach (var f in SpawnerFrames(Script.Spawners)) yield return f;   // :218
        if (Script.SeedHour is { } hour)                                    // :248-256
        {
            yield return new ZoneFrame(WzOpcodes.TimeOfDay, F32(hour));
            var w = new ZoneWriter();                                       // WZGimmickAndOpsPackets.cs:29-39
            w.WriteSingle(hour); w.WriteSingle(1.0f); w.WriteSingle(0.0f); w.WriteSingle(24.0f);
            yield return new ZoneFrame(WzOpcodes.DetailedTimeOfDay, w.ToArray());
        }
        var gt = new ZoneWriter();                                          // :258; WZGimmickAndOpsPackets.cs:24-27
        gt.WriteUInt32(Script.WorldGameSeconds);
        yield return new ZoneFrame(WzOpcodes.WorldGameTime, gt.ToArray());
    }

    private static byte[] F32(float value)
    {
        var w = new ZoneWriter();                                           // WZTimeOfDayPacket.cs:8-11
        w.WriteSingle(value);
        return w.ToArray();
    }

    /// <summary>WZJoinResponsePacket.cs:81-93: u16 reason, u16-prefixed 31-byte fset, then fixed zeros.</summary>
    private static ZoneFrame JoinResponse(ushort reason)
    {
        var fset = new byte[31];                                            // WZJoinResponsePacket.cs:48-65
        fset[0] = (byte)'1';
        fset[60 / 8] |= 1 << (60 % 8);                                      // PhysicsDamage
        fset[177 / 8] |= 1 << (177 % 8);                                    // ServerSideShipSim
        var w = new ZoneWriter();
        w.WriteUInt16(reason);
        w.WriteUInt16((ushort)fset.Length);
        w.WriteBytes(fset);
        w.WriteByte(0);        // bf
        w.WriteUInt16(0);      // type
        w.WriteByte(0);        // pvp
        w.WriteByte(0);        // duel
        w.WriteUInt16(0);      // type (second)
        w.WriteUInt32(0);      // peaceMin
        w.WriteUInt32(0);      // filterBufferSize
        return new ZoneFrame(WzOpcodes.JoinResponse, w.ToArray());
    }

    /// <summary>WZFactionListPacket.cs:31-68. An empty list is one {total 0, count 0} chunk.</summary>
    private static IEnumerable<ZoneFrame> FactionFrames(IReadOnlyList<ZoneFaction> all)
    {
        if (all.Count == 0) { yield return FactionChunkFrame(0, []); yield break; }
        for (var offset = 0; offset < all.Count; offset += FactionChunk)
            yield return FactionChunkFrame((uint)all.Count, all.Skip(offset).Take(FactionChunk).ToList());
    }

    private static ZoneFrame FactionChunkFrame(uint total, IReadOnlyList<ZoneFaction> chunk)
    {
        var w = new ZoneWriter();
        w.WriteUInt32(total);
        w.WriteByte((byte)chunk.Count);
        foreach (var f in chunk)                                            // SystemFaction.Write, SystemFaction.cs:~131-146
        {
            w.WriteUInt32(f.Id); w.WriteUInt32(f.MotherId);
            Str(w, f.Name); w.WriteUInt64((ulong)f.OwnerId); Str(w, f.OwnerName);
            w.WriteByte(f.UnitOwnerType); w.WriteByte(f.PoliticalSystem);
            w.WriteUInt64(f.CreatedTime);
            w.WriteBoolean(f.AggroLink); w.WriteBoolean(f.DiplomacyTarget);
            w.WriteByte(f.AllowChangeName); w.WriteUInt64(f.RenameTime); w.WriteBoolean(f.IntegrationFaction);
        }
        return new ZoneFrame(WzOpcodes.FactionList, w.ToArray());
    }

    /// <summary>WZFactionRelationListPacket.cs:33-97, sorted by (min,max) before chunking by 200.</summary>
    private static IEnumerable<ZoneFrame> RelationFrames(IReadOnlyList<ZoneFactionRelation> input)
    {
        var all = input.OrderBy(r => Math.Min(r.FirstId, r.SecondId)).ThenBy(r => Math.Max(r.FirstId, r.SecondId)).ToList();
        if (all.Count == 0) { yield return RelationChunkFrame(0, []); yield break; }
        for (var offset = 0; offset < all.Count; offset += RelationChunk)
            yield return RelationChunkFrame((uint)all.Count, all.Skip(offset).Take(RelationChunk).ToList());
    }

    private static ZoneFrame RelationChunkFrame(uint total, IReadOnlyList<ZoneFactionRelation> chunk)
    {
        var w = new ZoneWriter();
        w.WriteUInt32(total);
        w.WriteByte((byte)chunk.Count);
        foreach (var r in chunk)
        {
            w.WriteUInt32(Math.Min(r.FirstId, r.SecondId)); w.WriteUInt32(Math.Max(r.FirstId, r.SecondId));
            w.WriteByte(r.State); w.WriteByte(r.NextState);
            w.WriteUInt64(r.UpdateTime); w.WriteUInt64(r.ChangeTime);
            w.WriteUInt64((ulong)r.UpdaterId); Str(w, r.UpdaterName);
            w.WriteUInt64((ulong)r.ConfirmerId); Str(w, r.ConfirmerName);
        }
        return new ZoneFrame(WzOpcodes.FactionRelationList, w.ToArray());
    }

    /// <summary>WZSpawnerListPacket.cs:39-76: empty is one {last=1,count=0}; the final chunk always has last=1.</summary>
    private static IEnumerable<ZoneFrame> SpawnerFrames(IReadOnlyList<PersistentSpawner> all)
    {
        if (all.Count == 0) { yield return SpawnerChunkFrame([], true); yield break; }
        for (var offset = 0; offset < all.Count; offset += SpawnerChunk)
        {
            var take = Math.Min(SpawnerChunk, all.Count - offset);
            yield return SpawnerChunkFrame(all.Skip(offset).Take(take).ToList(), offset + take >= all.Count);
        }
    }

    private static ZoneFrame SpawnerChunkFrame(IReadOnlyList<PersistentSpawner> chunk, bool last)
    {
        var w = new ZoneWriter();
        w.WriteByte((byte)(last ? 1 : 0));
        w.WriteByte((byte)chunk.Count);
        foreach (var s in chunk) { w.WriteUInt32(s.Id); w.WriteUInt32(s.TemplateId); w.WriteByte(s.State); }
        return new ZoneFrame(WzOpcodes.SpawnerList, w.ToArray());
    }

    /// <summary>PacketStream.Write(string): u16 byte length, UTF-8 (PacketStream.cs:1490).</summary>
    private static void Str(ZoneWriter w, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        w.WriteUInt16((ushort)bytes.Length);
        w.WriteBytes(bytes);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_connections);
        _stop.Dispose();
    }
}
