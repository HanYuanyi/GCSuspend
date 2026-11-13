# GC Suspend

Suspends the managed (Boehm) garbage collector in Koikatsu to eliminate periodic GC stutters.

## The problem

Unity 5.6 uses Mono with the Boehm collector — non-generational and fully stop-the-world. In CharaStudio and H scenes the managed heap grows steadily, so every few seconds the game freezes for a complete collection. There is no incremental GC in this Unity version, and no way to tune it through the public API.

## What this plugin does

It locates the GC suspend counter inside `mono.dll` and keeps it above zero, which makes the collector skip its automatic runs. A watchdog checks the managed heap every few seconds and briefly re-enables collection only when the heap exceeds a configurable limit, then suspends it again.

Separately, and with no effect on the suspended state, it also keeps **process memory** in check: once system memory passes a threshold it trims our working set on a background thread, so the rest of the machine keeps running smoothly. See "Keeping process memory in check" below.

The H-scene detection in the plugin is intended for Koikatsu's FreeH. It enables GC suspension when entering FreeH is detected and automatically disables it upon exit. Pressing F8 opens the memory monitoring panel.

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
| Heap limit (GB) | `16` | Trigger one collection when the managed heap exceeds this size. Higher means fewer collections but longer freezes. |
| Check interval (s) | `3` | Seconds between heap checks. Also used for the system memory check below. |
| System memory trigger (%) | `75` | Trim our working set once system memory reaches this percentage. `0` turns it off. See below. |
| Process memory floor (MB) | `6144` | Skip the trim when our own process memory is below this. Stops the plugin from working when the pressure comes from other programs. |
| Min seconds between trims | `10` | Cooldown between two working set trims. |
| Show panel (F8) | `false` | On-screen memory monitor. |

## Keeping process memory in check

The heap limit above only handles the **managed heap**. Process memory (what the task manager shows) is a different number — it also contains native Unity assets — and on a long session it can climb until the system itself starts to struggle: everything else on the machine loads slowly because Windows is running out of free pages.

`System memory trigger (%)` deals with that, and it is deliberately independent of GC:

- It fires on the **system** percentage, not on the heap, so it works even while GC is not suspended.
- The trim runs on a **worker thread**, so the main thread never stalls. No freeze.
- It calls `EmptyWorkingSet`, which is the same thing Windows does under memory pressure — it just happens earlier, at a threshold you pick instead of at the last moment. Our pages move to the standby list, which the system can reclaim instantly instead of paging out other programs.

Because the pages stay in RAM as standby, this is not "freeing memory" — it is handing it back early so the rest of the machine keeps running smoothly. A side effect is that the task manager number for the game drops sharply; that is expected.

## ResourceUnloadOptimizations

`KK_Fix_ResourceUnloadOptimizations` handles **native Unity asset** unloading. This plugin controls the **managed heap** and, separately, the process working set. They manage different things and do not conflict — you can leave RUO on.

Everything in this README was tested with both RUO options enabled, and it made no difference: RUO's `Disable Resource Unload` keeps native assets from being unloaded, but the working set trim above still hands those pages back to the system, so the machine does not run out of free memory and nothing else starts loading slowly.

## Diagnostics

Enable `Show panel (F8)` to see the resolved counter address, the managed heap size and the process memory in the top-left corner. The panel also shows system memory (`78% 25/32G`) and the last working set trim (trigger percentage, before → after, elapsed time), which is the quickest way to see whether the trim actually did anything.

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
