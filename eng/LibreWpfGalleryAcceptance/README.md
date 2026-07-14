# LibreWPF Gallery acceptance gate

This nonshipping `DOTNET_STARTUP_HOOKS` harness validates the real, unchanged
WPFGallery process through public typed WPF and `ProGpuWpfDiagnostics` APIs. It
does not use reflection, private fields, runtime type lookup, or Gallery
implementation types.

The scenario waits for a first ProGPU frame, moves the real window away from
the desktop origin, traverses all 52 navigation targets, mutates representative
controls, and checks retained composition and GPU hit-test state after each
page. It also sends typed ProGPU mouse and keyboard input, renders and closes a
real `ComboBox` popup, then opens a real `ContextMenu` with a nested submenu and
invokes its leaf command through the ProGPU input path. The popup input is
calculated from public `PointToScreen`/`PointFromScreen` coordinates after the
window move, so the gate catches owner-screen-origin regressions.

Build WPFGallery first, then run this from the repository root:

```bash
./eng/LibreWpfGalleryAcceptance/run.sh
```

Pass a Gallery output directory as the first argument when it differs from
`bin/Release/net10.0-windows`. Set `CONFIGURATION` for a non-Release build or
`LIBREWPF_GALLERY_ACCEPTANCE_LOG` to keep the detailed result elsewhere.
