# welcome.mp4

Put a short screen recording here as `welcome.mp4` (H.264, 1280×720 or smaller, under ~5 MB, no audio
needed) showing an alert arriving on the iPhone and being mirrored to the Apple Watch. The build copies
it next to `EncyPulse.dll`; the Overview page and the Help page play it in a loop. Without the file the
window shows a built-in animated illustration instead.

A recording placed in `%APPDATA%\ENCY SOFTWARE\EncyPulse\welcome.mp4` overrides the shipped one.

How to record:
- iPhone: Control Center → Screen Recording, then trigger "Send test message" in ENCY Pulse.
- Apple Watch: watchOS has no screen recording; film the watch with a phone, or on a Mac use the
  Xcode simulators (`xcrun simctl io booted recordVideo watch.mp4`) with a pushed notification.
