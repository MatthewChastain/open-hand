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
// main-hand selection: the client requests, the server validates the revision
// and broadcasts the settled state to everyone (other clients substitute the
// sender's offhand in their own entity reads).
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
