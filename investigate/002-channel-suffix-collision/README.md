# BUG: Channel Suffix Collision — Transit IDs Containing ">>" or "<<" Produce Ambiguous Channel Names

## Severity: MEDIUM

## Summary

`TransitExtensions` appends `">>"` and `"<<"` suffixes to user-provided channel IDs
when creating duplex transits. If the user's channel ID already contains these suffixes,
the resulting channel IDs are ambiguous and can collide.

## Evidence

### Code Location

[src/NetConduit/Transits/TransitExtensions.cs](../../src/NetConduit/Transits/TransitExtensions.cs):

```csharp
public const string OutboundSuffix = ">>";
public const string InboundSuffix = "<<";

// OpenDuplexStreamAsync:
var writeChannel = await mux.OpenChannelAsync(
    new ChannelOptions { ChannelId = channelId + OutboundSuffix }, ...);
var readChannel = await mux.AcceptChannelAsync(
    channelId + InboundSuffix, ...);
```

### Collision Example

| User ChannelId | Write Channel ID | Read Channel ID |
|---|---|---|
| `"data"` | `"data>>"` | `"data<<"` |
| `"data>>"` | `"data>>>>"` | `"data>><<"` |
| `"data<<"` | `"data<<>>"` | `"data<<<<<"` |

If one transit uses `"data"` and another uses `"data>>"`:
- Transit A write channel: `"data>>"`
- Transit B read channel: `"data>><<"` — but the OPENER of "data>>" sees write channel `"data>>>>"`

There is NO validation that `channelId` does not contain the suffix characters.

## CWE Reference

- [CWE-20: Improper Input Validation](https://cwe.mitre.org/data/definitions/20.html)

## Recommended Fix

Validate channel IDs at the point of creation. Reject IDs that contain the reserved
suffix sequences, or use a delimiter that cannot appear in user input:

**Option A — Input validation (minimal change):**

```csharp
public static void ValidateChannelId(string channelId)
{
    if (channelId.Contains(OutboundSuffix) || channelId.Contains(InboundSuffix))
        throw new ArgumentException(
            $"Channel ID must not contain reserved suffixes '{OutboundSuffix}' or '{InboundSuffix}'.",
            nameof(channelId));
}
```

**Option B — Use a non-ambiguous separator (structural fix):**

Replace string concatenation with a format that uses a separator character that is
validated out of channel IDs (e.g., `\0`):

```csharp
public const char Separator = '\0';
var writeChannelId = $"{channelId}{Separator}out";
var readChannelId = $"{channelId}{Separator}in";
```

Option B is stronger because it eliminates the collision class entirely rather than
relying on runtime validation.

## Reproduction

See `ChannelSuffixCollisionTest.cs` — test proves suffix collision creates unusable channels.
