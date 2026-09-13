using System.Text.Json;
using OpenHand.Common;

internal static class ConfigTests
{
    internal static void Run()
    {
        TestHarness.Equal(false, new OpenHandClientConfig().DoubleTapHotbarKey, "double tap defaults off");
        TestHarness.Equal(true, new OpenHandClientConfig().CenterHotbar, "centering defaults on");
        TestHarness.Equal(false, new OpenHandClientConfig().EmptyOffhandEnabled, "empty offhand defaults off");

        // Config anchor parsing: case-insensitive, trims, defaults on junk.
        TestHarness.Equal(IconAnchorMode.Auto, OpenHandClientConfig.ParseIconAnchor("auto"), "anchor auto");
        TestHarness.Equal(IconAnchorMode.OffhandGap, OpenHandClientConfig.ParseIconAnchor("OFFHANDGAP"), "anchor offhand gap");
        TestHarness.Equal(IconAnchorMode.Left, OpenHandClientConfig.ParseIconAnchor(" left "), "anchor left");
        TestHarness.Equal(IconAnchorMode.Right, OpenHandClientConfig.ParseIconAnchor("Right"), "anchor right");
        TestHarness.Equal(IconAnchorMode.Auto, OpenHandClientConfig.ParseIconAnchor("nope"), "anchor junk defaults to auto");
        TestHarness.Equal(IconAnchorMode.Auto, OpenHandClientConfig.ParseIconAnchor(null), "anchor null defaults to auto");
        TestHarness.Equal(true, OpenHandClientConfig.IsKnownIconAnchor("offhandgap"), "known anchor");
        TestHarness.Equal(false, OpenHandClientConfig.IsKnownIconAnchor("nope"), "unknown anchor");
        TestHarness.Equal(false, OpenHandClientConfig.IsKnownIconAnchor(null), "null anchor");
        TestHarness.Equal(true, new OpenHandClientConfig().ShowIndicator, "indicator defaults on");
        TestHarness.Equal(true, new OpenHandClientConfig().CenterHotbar, "centering defaults on");
        TestHarness.Equal(false, new OpenHandClientConfig { ShowIndicator = false }.ShowIndicator, "indicator can be disabled");
        TestHarness.Equal(true, new OpenHandClientConfig().ShowOffhandIndicator, "offhand indicator defaults on");
        TestHarness.Equal(false, new OpenHandClientConfig { ShowOffhandIndicator = false }.ShowOffhandIndicator, "offhand indicator can be disabled");
        // The two indicator visuals are independent settings.
        OpenHandClientConfig indicators = new() { ShowIndicator = false };
        TestHarness.Equal(true, indicators.ShowOffhandIndicator, "hiding the main indicator keeps the offhand visual");
        indicators = new OpenHandClientConfig { ShowOffhandIndicator = false };
        TestHarness.Equal(true, indicators.ShowIndicator, "hiding the offhand visual keeps the main indicator");

        // openhand.json stability: the POCO property names are the on-disk keys,
        // so the JSON round trip must preserve every property through a fully
        // customized config.
        OpenHandClientConfig customized = new()
        {
            IconAnchor = "left",
            IconOffsetX = -3,
            IconOffsetY = 7,
            ShowIndicator = false,
            ShowOffhandIndicator = false,
            CenterHotbar = false,
            DoubleTapHotbarKey = true,
            EmptyOffhandEnabled = true
        };
        string json = JsonSerializer.Serialize(customized);
        foreach (string name in new[]
        {
            "IconAnchor", "IconOffsetX", "IconOffsetY", "ShowIndicator",
            "ShowOffhandIndicator", "CenterHotbar", "DoubleTapHotbarKey", "EmptyOffhandEnabled"
        })
        {
            if (!json.Contains($"\"{name}\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"config JSON lost the pinned property name {name}");
            }
        }
        OpenHandClientConfig roundTrip = JsonSerializer.Deserialize<OpenHandClientConfig>(json)!;
        TestHarness.Equal(customized.IconAnchor, roundTrip.IconAnchor, "config round trip IconAnchor");
        TestHarness.Equal(customized.IconOffsetX, roundTrip.IconOffsetX, "config round trip IconOffsetX");
        TestHarness.Equal(customized.IconOffsetY, roundTrip.IconOffsetY, "config round trip IconOffsetY");
        TestHarness.Equal(customized.ShowIndicator, roundTrip.ShowIndicator, "config round trip ShowIndicator");
        TestHarness.Equal(customized.ShowOffhandIndicator, roundTrip.ShowOffhandIndicator, "config round trip ShowOffhandIndicator");
        TestHarness.Equal(customized.CenterHotbar, roundTrip.CenterHotbar, "config round trip CenterHotbar");
        TestHarness.Equal(customized.DoubleTapHotbarKey, roundTrip.DoubleTapHotbarKey, "config round trip DoubleTapHotbarKey");
        TestHarness.Equal(customized.EmptyOffhandEnabled, roundTrip.EmptyOffhandEnabled, "config round trip EmptyOffhandEnabled");

        // An empty object restores every default, and defaults serialize back.
        OpenHandClientConfig empty = JsonSerializer.Deserialize<OpenHandClientConfig>("{}")!;
        TestHarness.Equal(true, empty.ShowIndicator, "empty JSON restores indicator default");
        TestHarness.Equal(true, empty.ShowOffhandIndicator, "empty JSON restores offhand indicator default");
        TestHarness.Equal(true, empty.CenterHotbar, "empty JSON restores centering default");
        TestHarness.Equal(false, empty.DoubleTapHotbarKey, "empty JSON restores double-tap default");
        TestHarness.Equal(false, empty.EmptyOffhandEnabled, "empty JSON restores empty-offhand default");
        TestHarness.Equal("auto", JsonSerializer.Deserialize<OpenHandClientConfig>(JsonSerializer.Serialize(new OpenHandClientConfig()))!.IconAnchor, "defaults serialize and round trip");
    }
}
