# Changelog

## 1.0.0

- Initial public release
- GC suspend counter located by byte signature instead of hardcoded offsets
- Counter address computed from the instruction displacement, cross-checked between `GC_enable` and `GC_disable`
- Supports Koikatsu, Koikatsu Party, CharaStudio and KoikatuVR
- Verified against retail, Party, VR `mono.dll` builds
- Falls back to `monoPatcher.dll` when the signature is not found
