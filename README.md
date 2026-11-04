# GC Suspend

Suspends the managed (Boehm) garbage collector in Koikatsu to eliminate periodic GC stutters.

## The problem

Unity 5.6 uses Mono with the Boehm collector — non-generational and fully stop-the-world. In CharaStudio and H scenes the managed heap grows steadily, so every few seconds the game freezes for a complete collection. There is no incremental GC in this Unity version, and no way to tune it through the public API.

## What this plugin does

It locates the GC suspend counter inside `mono.dll` and keeps it above zero, which makes the collector skip its automatic runs. A watchdog checks the managed heap every few seconds and briefly re-enables collection only when the heap exceeds a configurable limit, then suspends it again.

## How the counter is located

**No hardcoded offsets.** The plugin scans the `.text` section for a byte signature:

```
48 83 EC 28 48 8D 0D ?? ?? ?? ??   …   FF 0D ?? ?? ?? ??
```

This is the prologue of `GC_enable` followed by its `dec dword [rip+disp32]`. The counter address is computed from the displacement, and the same address must also be produced by the matching `GC_disable` path (`FF 05`) — otherwise the result is rejected.

Verified against:

| Build | Function offset | Counter |
|---|---|---|
| Koikatsu / CharaStudio | `0x157308` | `0x266BF4` |
| Koikatsu Party | `0x157308` | `0x266BF4` |
| KoikatsuVR / Party VR | `0x15730C` | `0x266BF4` |
| debug mono | `0x1A4414` | `0x2AD57C` |

Four different layouts, one signature. The counter is written directly with `Marshal.WriteInt32`, so `monoPatcher.dll` is not required.

## Installation

1. Install BepInEx 5.4.x (64-bit)
2. Drop `GCSuspend.dll` into `BepInEx/plugins/`
3. Launch Koikatsu, CharaStudio or KoikatuVR

## Configuration

| Setting | Default | Description |
|---|---|---|
| Enabled | `true` | Master switch. Turning it off immediately re-enables GC, no restart needed. |
| Only in H scene | `true` | Suspend only in H scenes. CharaStudio always suspends regardless of this setting. |
| Heap limit (MB) | `16384` | Trigger one collection when the managed heap exceeds this size. |
| Check interval (s) | `3` | Seconds between heap checks. |
| Also release native memory | `false` | Also calls `Resources.UnloadUnusedAssets` and trims the working set. Causes a long freeze — not recommended. |
| Force collect key | `None` | Hotkey for one manual collection. |
| Show panel (F8) | `false` | On-screen memory monitor. |

## Keep ResourceUnloadOptimizations installed

`KK_Fix_ResourceUnloadOptimizations` handles **native Unity asset** unloading. This plugin only controls the **managed heap**. They manage different pools of memory and do not conflict.

Note that RUO's `Disable Resource Unload` option disables native asset unloading entirely — that is a separate concern and this plugin cannot compensate for it. If your process memory keeps climbing to the physical limit, check that option first.

## Diagnostics

Enable `Show panel (F8)` to see the resolved counter address, the managed heap size and the process memory in the top-left corner.

The counter address is also logged on startup, which is the quickest way to confirm the signature matched:

```
[Info   : GC Suspend] [GCSuspend] GC counter located at mono+0x266BF4
```

If the signature does not match, a warning is logged instead and the plugin falls back to `monoPatcher.dll`.

## Compatibility

- Koikatsu, Koikatsu Party, CharaStudio, KoikatuVR
- BepInEx 5.4.x

## License

MIT
