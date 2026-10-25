# SpookyCam

A local Windows desktop app (C# / .NET 8 WinForms + OpenCvSharp) that shows your webcam
with Halloween effects. Press a number key to switch effects live.

## Run

```bash
dotnet run
```

Or build once and launch `bin\Debug\net8.0-windows\SpookyCam.exe`.

Requires the .NET 8 SDK. The OpenCV native libraries come from the NuGet packages, nothing else to install.

## Effects

| Key | Effect | Style | What it does |
|-----|--------|-------|--------------|
| 0 | Normal | – | Plain camera feed |
| 1 | Ghost Trails | Horror | Motion smears into pale blue phantoms with a breathing flicker |
| 2 | Night Vision | Horror | Grainy green security cam, scanlines, blinking REC timestamp |
| 3 | Blood Moon | Horror | Crimson, high contrast, pulsing heartbeat |
| 4 | Possessed TV | Horror | Glitch bands, RGB tearing, static, inversion flashes, vertical hold slips |
| 5 | Jump Scare | Scare | Flashes the scare image over the feed with a scream, zoom and shake for about two seconds, then returns to the current effect |
| 6 | Haunted Negative | Horror | Inverted, washed out, sickly tint, light-bulb flicker |
| 7 | Kaleidoscope | Psychedelic | Six-fold spinning mirror with cycling hue |
| 8 | Acid Rainbow | Psychedelic | Full spectrum sliding across the frame, posterized with glow |
| 9 | Liquid Dream | Psychedelic | Picture melts in slow waves with colour fringing |

A Thermal heat-map effect is also in `Effects.cs`; add it to `EffectCatalog.All` to put it on a key.

## Jump scare media

The image and sound for key 5 are not in the repository (the `assets` folder is gitignored).
Put your own files in `assets\` next to the project file, with these exact names:

| File | Purpose |
|------|---------|
| `assets\jumpscare.jpg` | The picture that fills the screen |
| `assets\jumpscare-scream.mp3` | The sound that plays with it |

They are copied next to the exe on build. If the image is missing the app says so on start
and key 5 does nothing. Cycling with Space or the arrow keys skips key 5 so it never fires by accident.

## Other keys

| Key | Action |
|-----|--------|
| Right / Space | Next effect (skips the jump scare) |
| Left | Previous effect (skips the jump scare) |
| F or F11 | Toggle fullscreen |
| Esc | Leave fullscreen, or quit |
| M | Toggle mirror |
| H | Hide / show the overlay |
| S | Save a snapshot to `Pictures\SpookyCam` |
| C | Switch to the next camera |

## Camera troubleshooting

The app opens camera index 0 first and cycles through indexes 0 to 3 every two seconds
until one opens. If it keeps saying "No camera":

- Check for a physical privacy shutter on the laptop lid or a keyboard camera toggle
  (often an F-key with a camera icon, sometimes needs Fn). When this is off, Windows
  reports the camera as "not connected" (Device Manager code 45).
- Windows Settings > Privacy & security > Camera: enable camera access and
  "Let desktop apps access your camera".
- Close other apps using the camera (Teams, Zoom, browser tabs).
- An external USB webcam works too; press C to cycle to it.

## Adding an effect

Add a class deriving from `Effect` in `Effects.cs` and append it to `EffectCatalog.All`.
The index in that array is the key that selects it.
