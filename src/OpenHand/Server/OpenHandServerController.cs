using OpenHand.Common;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace OpenHand.Server;

internal sealed class OpenHandServerController : IDisposable
{
    private const string ChannelName = "openhand";
    private readonly IServerNetworkChannel channel;
    private readonly ICoreServerAPI sapi;
    private bool disposed;

    public OpenHandServerController(ICoreServerAPI sapi)
    {
        this.sapi = sapi;
        channel = sapi.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<OpenHandSelectionRequest>()
            .RegisterMessageType<OpenHandSelectionUpdate>()
            .SetMessageHandler<OpenHandSelectionRequest>(OnSelectionRequest);

        sapi.Event.PlayerJoin += OnPlayerJoin;
        sapi.Event.PlayerLeave += OnPlayerLeave;
    }

    private void OnSelectionRequest(IServerPlayer player, OpenHandSelectionRequest request)
    {
        OpenHandSelectionState current = OpenHandRuntime.Get(player);
        if (request.Revision <= current.Revision)
        {
            Send(player, current);
            return;
        }

        OpenHandSelectionState state = OpenHandRuntime.Set(
            player,
            request.Selected,
            request.RememberedHotbarSlot,
            request.Revision);
        Send(player, state);
        channel.BroadcastPacket(ToUpdate(player.PlayerUID, state), player);
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        foreach ((string uid, OpenHandSelectionState state) in OpenHandRuntime.Snapshot())
        {
            channel.SendPacket(ToUpdate(uid, state), player);
        }
    }

    private void OnPlayerLeave(IServerPlayer player)
    {
        OpenHandRuntime.Clear(player);
        channel.BroadcastPacket(new OpenHandSelectionUpdate
        {
            PlayerUid = player.PlayerUID,
            Selected = false,
            RememberedHotbarSlot = OpenHandSelectionState.PhysicalHotbarSlots - 1,
            Revision = int.MaxValue
        });
    }

    private void Send(IServerPlayer player, OpenHandSelectionState state) =>
        channel.SendPacket(ToUpdate(player.PlayerUID, state), player);

    private static OpenHandSelectionUpdate ToUpdate(string playerUid, OpenHandSelectionState state) =>
        new()
        {
            PlayerUid = playerUid,
            Selected = state.IsSelected,
            RememberedHotbarSlot = state.RememberedHotbarSlot,
            Revision = state.Revision
        };

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        sapi.Event.PlayerJoin -= OnPlayerJoin;
        sapi.Event.PlayerLeave -= OnPlayerLeave;
        OpenHandRuntime.ClearAll();
    }
}
