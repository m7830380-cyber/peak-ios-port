# PEAK → iOS (personal port tooling)

Tools and code that turn a legally owned Windows copy of *PEAK* (Aggro Crab / Landfall) into an iOS app for one
person's own TrollStore iPhone. **This repository contains no game files** and nothing derived from them: the
patched assemblies, converted data and IPAs exist only on the owner's PC and in a private repository.

The game is not rebuilt: its own Mono assemblies are patched and AOT-compiled with IL2CPP in a minimal Unity
6000.3.15f1 project (the game's exact version) on GitHub's macOS runners, and its own data files are converted
for Metal / ASTC and merged in on the PC.

No Steam, Epic Online Services, Unity Gaming Services, Landfall or Photon Cloud contact from the iOS app: Steam is
stubbed (the game's own "NoSteam" mode), the EOS login and the version check are removed, and Photon either runs
in its offline mode (Play Solo) or only reaches the local network.

* `shim/` – `PeakShim.dll`: runtime the patched game calls instead of desktop APIs (Input, Cursor, PlayerPrefs,
  Microphone, Process, Screen), local player identity, the network guard.
* `platform/` – `PeakPlatform.dll`: the iOS `IPlatform` (replaces `SteamRuntimeManager`; achievements, stats and
  progress in Documents).
* `mod/` – `PeakIOS.dll`: log (`Documents/PEAK-iOS.log`, previous run `-prev`), on-screen log (tap the FPS
  counter), iOS settings, touch → virtual mouse / keyboard + analog stick.
* `tools/patcher` – Mono.Cecil patcher for the retail assemblies.
* `tools/build_plugins.py` – builds shim/platform/mod and runs the patcher into the private repo's `Plugins`.
* `shell/` – minimal Unity 6000.3.15f1 project; CI builds the unsigned, ldid-fakesigned shell IPA from it.
* `tools/convert_*.py`, `dx2metal.py`, `transcode_tex.py` – convert the Windows data for iOS (DX11 → Metal
  shaders, BC → ASTC textures, iOS platform tag).

Based on the Content Warning and Internet Cafe Simulator 2 ports' tooling.
