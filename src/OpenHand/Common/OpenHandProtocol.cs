using ProtoBuf;

namespace OpenHand.Common;

[ProtoContract]
public sealed class OpenHandSelectionRequest
{
    [ProtoMember(1)]
    public bool Selected { get; set; }

    [ProtoMember(2)]
    public int RememberedHotbarSlot { get; set; }

    [ProtoMember(3)]
    public int Revision { get; set; }
}

[ProtoContract]
public sealed class OpenHandSelectionUpdate
{
    [ProtoMember(1)]
    public string PlayerUid { get; set; } = string.Empty;

    [ProtoMember(2)]
    public bool Selected { get; set; }

    [ProtoMember(3)]
    public int RememberedHotbarSlot { get; set; }

    [ProtoMember(4)]
    public int Revision { get; set; }
}

// The empty-offhand toggle rides the same channel and revision scheme as the
// main-hand selection: the client requests and the server validates the
// revision, then settles the sender's own client with the authoritative
// state. Other clients do NOT substitute the sender's offhand from these
// messages — the client update handlers deliberately apply only the local
// player's state, so a remote observer holds no Open Hand state for anyone
// else. What other players actually see comes from vanilla's own held-item
// replication: the server reads the substituted getters and replicates the
// resulting empty stacks, pushed by
// OpenHandServerController.BroadcastHeldItems after every applied toggle.
[ProtoContract]
public sealed class OpenHandOffhandRequest
{
    [ProtoMember(1)]
    public bool IsEmpty { get; set; }

    [ProtoMember(2)]
    public int Revision { get; set; }
}

[ProtoContract]
public sealed class OpenHandOffhandUpdate
{
    [ProtoMember(1)]
    public string PlayerUid { get; set; } = string.Empty;

    [ProtoMember(2)]
    public bool IsEmpty { get; set; }

    [ProtoMember(3)]
    public int Revision { get; set; }
}
