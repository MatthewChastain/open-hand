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
