# Changelog

## 1.2.1

- Reverted the heap limit slider to a plain whole-GB slider (1–32 GB). The custom drawer that also allowed decimals laid out incorrectly in the F1 panel

## 1.2.0

- Removed "Also release native memory", "Force collect key" and the manual trim hotkey. The asset unload only ran on the heap-over-limit path, which never triggers when the heap stays small, so it did nothing
- Heap limit is now in GB (1–32) instead of MB, so it still works on machines with plenty of RAM
- Renamed "Minimum occupancy (MB)" to "Process memory floor (MB)" so it is not mistaken for the heap limit
- Default cooldown between trims is now 10 s
- Monitor panel also shows system memory and the last trim (trigger percentage, before → after, elapsed time)

## 1.1.0

- Process memory cleanup is now fully independent of GC
  - Triggered by system memory percentage instead of the managed heap, so it also works while GC is not suspended
  - The working set is trimmed on a worker thread, so the main thread never stalls
  - It only moves our pages to the standby list - the same thing Windows does under pressure, just earlier - so other programs no longer get squeezed
- New options: "System memory trigger (%)", "Process memory floor (MB)", "Min seconds between trims"
- "Also release native memory" is no longer tied to the collection path

## 1.0.0

- Initial public release
- GC suspend counter located by byte signature instead of hardcoded offsets
- Counter address computed from the instruction displacement, cross-checked between `GC_enable` and `GC_disable`
- Supports Koikatsu, Koikatsu Party, CharaStudio and KoikatuVR
- Verified against retail, Party, VR `mono.dll` builds
- Falls back to `monoPatcher.dll` when the signature is not found
