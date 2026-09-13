using System.IO;
using OpenHand.Common;
using ProtoBuf;

internal static class ProtocolTests
{
    internal static void Run()
    {
        // Selection request, fully populated: the exact wire bytes pin the
        // ProtoMember tags (field 1 varint tag 0x08, field 2 tag 0x10, field
        // 3 tag 0x18) so the game's own protobuf-net always parses what we
        // serialize. protobuf-net omits type-default values.
        OpenHandSelectionRequest filled = new() { Selected = true, RememberedHotbarSlot = 4, Revision = 7 };
        Bytes(new byte[] { 0x08, 0x01, 0x10, 0x04, 0x18, 0x07 }, Serialize(filled), "selection request wire bytes");
        OpenHandSelectionRequest parsed = RoundTrip(filled);
        TestHarness.Equal(true, parsed.Selected, "selection request round trip Selected");
        TestHarness.Equal(4, parsed.RememberedHotbarSlot, "selection request round trip slot");
        TestHarness.Equal(7, parsed.Revision, "selection request round trip Revision");

        // Defaults collapse to an empty payload.
        TestHarness.Equal(0, Serialize(new OpenHandSelectionRequest()).Length, "default selection request serializes empty");

        // A negative slot sign-extends to a 10-byte varint but must round trip.
        OpenHandSelectionRequest negative = RoundTrip(new OpenHandSelectionRequest { RememberedHotbarSlot = -1 });
        TestHarness.Equal(-1, negative.RememberedHotbarSlot, "negative slot round trips");

        // Selection update: string field 1 tags 0x0A, the bool field 2 tags
        // 0x10, revision field 4 tags 0x20; the int.MaxValue revision is what
        // the player-leave broadcast rides.
        OpenHandSelectionUpdate update = new() { PlayerUid = "p", Selected = true, Revision = int.MaxValue };
        Bytes(new byte[] { 0x0A, 0x01, 0x70, 0x10, 0x01, 0x20, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 },
            Serialize(update), "selection update wire bytes");
        OpenHandSelectionUpdate parsedUpdate = RoundTrip(update);
        TestHarness.Equal("p", parsedUpdate.PlayerUid, "selection update round trip uid");
        TestHarness.Equal(true, parsedUpdate.Selected, "selection update round trip Selected");
        TestHarness.Equal(int.MaxValue, parsedUpdate.Revision, "selection update round trip Revision");

        // An empty UID is the unset case and must survive the round trip.
        OpenHandSelectionUpdate emptyUid = RoundTrip(new OpenHandSelectionUpdate());
        TestHarness.Equal(string.Empty, emptyUid.PlayerUid, "empty uid round trips");

        // Offhand messages ride the same scheme on their own field numbers.
        OpenHandOffhandRequest offhandFilled = new() { IsEmpty = true, Revision = 3 };
        Bytes(new byte[] { 0x08, 0x01, 0x10, 0x03 }, Serialize(offhandFilled), "offhand request wire bytes");
        OpenHandOffhandRequest parsedOffhand = RoundTrip(offhandFilled);
        TestHarness.Equal(true, parsedOffhand.IsEmpty, "offhand request round trip IsEmpty");
        TestHarness.Equal(3, parsedOffhand.Revision, "offhand request round trip Revision");
        TestHarness.Equal(0, Serialize(new OpenHandOffhandRequest()).Length, "default offhand request serializes empty");

        OpenHandOffhandUpdate offhandUpdate = RoundTrip(new OpenHandOffhandUpdate
        {
            PlayerUid = "uid",
            IsEmpty = true,
            Revision = int.MaxValue
        });
        TestHarness.Equal("uid", offhandUpdate.PlayerUid, "offhand update round trip uid");
        TestHarness.Equal(true, offhandUpdate.IsEmpty, "offhand update round trip IsEmpty");
        TestHarness.Equal(int.MaxValue, offhandUpdate.Revision, "offhand update round trip Revision");
    }

    private static byte[] Serialize<T>(T message) where T : class
    {
        using MemoryStream stream = new();
        Serializer.Serialize(stream, message);
        return stream.ToArray();
    }

    private static T RoundTrip<T>(T message) where T : class
    {
        return Serializer.Deserialize<T>(new MemoryStream(Serialize(message)));
    }

    private static void Bytes(byte[] expected, byte[] actual, string name)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"{name}: expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}.");
        }
    }
}
