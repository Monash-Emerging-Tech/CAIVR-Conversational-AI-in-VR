# CAIVR-Conversational-AI-in-VR
Creating virtual scenarios that prompt converstations with player, developing social skills and confidence when talk with a wide range of people.

## Getting started

1. Install **Unity 6000.3.9f1** (Unity Hub will offer it when you add the project) and open this folder as a project.
   The first open imports everything and takes a while.
2. Open `Assets/CAIVR/Scenes/ConsultationVR.unity` and press **Play**. Play always starts in the consultation
   room, where the menu is an object on the table.
3. For the professor's **live voice**, start the voice server (next section). Without it the game still works
   and uses the baked fallback voice.
4. For the AI dialogue and the microphone, see `Assets/CAIVR/README.md` (a free Groq key goes in the
   `GROQ_API_KEY` environment variable, never in the repo).

## Live voice (the professor's speech)

Every line the professor says, scripted or written by the AI, is spoken just in time by a neural voice
(VoxCPM2) running on your own GPU. Nothing is pre-recorded, and it is all free and open source: no API key,
no account, no per-use cost. Audio streams while it is still being generated, so she starts talking in under a
second. Each line is cached on disk after it is first spoken, and the scripted
lines are voiced in the background at startup, so repeat and scripted lines are instant.

**You need:** Windows, an NVIDIA GPU with a current driver (needs roughly 6 to 8 GB of VRAM; tested on an RTX
4080 SUPER 16 GB), Python 3.10 to 3.12 (**not 3.13**), and about 10 GB of free disk (PyTorch ~3 GB, voice model
~5 GB).

```
powershell -ExecutionPolicy Bypass -File Tools\voice_server.ps1 setup   # once, about 10 minutes
powershell -ExecutionPolicy Bypass -File Tools\voice_server.ps1 run     # every session; leave it open
```

The first `run` downloads the voice model from Hugging Face (no login needed) and loads it, which takes a
few minutes; later starts take about a minute. When it prints `CAIVR voice on http://127.0.0.1:5111`, press
Play in Unity. `... voice_server.ps1 check` shows whether it is up and how far the background warm-up is.

**How the game uses it.** `VoiceLinePlayer` asks the server for each line and plays the stream from the
professor's head. The main menu's voice picker lists the live voices first (`Warm`, `Warm, steady`). If the
server cannot be reached, the line falls back to the baked clip in `Assets/CAIVR/Resources/CAIVR/VO_edge` and
the Unity console says `Voice server unavailable ... Using baked clips meanwhile`. So **a robotic voice means
the server is not running.** Do not restart the server while someone is playing.

**Good to know**
- **Shared GPU.** On one GPU the voice alone runs a little faster than real time, but while the game is
  rendering it falls to roughly half speed, so a long AI-written reply can pause and stutter. Scripted and
  repeated lines are cached and unaffected. `run --steps 6` is faster at some cost in quality.
- **No NVIDIA GPU.** `setup` stops with a clear message. The game keeps working on the baked voice.
- **Quest / other machines.** The server listens on localhost only. For a Quest on the same network run it
  with `-Lan` (Windows will ask to allow it through the firewall), set the PC's address in `CaivrSettings.VoiceServer` and allow insecure HTTP in Player Settings.
  A WebGL page served over https cannot call it. A Quest cannot host the voice itself.
- **Voices** are defined at the top of `Tools/voice_server.py`. The picker list is `LiveVoiceSets` in
  `MainMenuController.cs`. `Tools/voices/` holds the reference takes that keep a voice consistent.
- **Cache** lives in `%LOCALAPPDATA%\CAIVR\VoiceCache` (not in the project: Unity empties `Temp/` on every
  open). Delete it to re-voice everything.
- **Manual setup / other platforms:** see `Tools/voice_server_requirements.txt` and run
  `python Tools/voice_server.py --prewarm` from a Python 3.10 to 3.12 environment with a CUDA build of PyTorch.
- **Baked fallback voice:** `Tools/generate_voice_lines.py` regenerates it. `Tools/tts_server.py` is the old
  edge-tts server, superseded by `voice_server.py` (it needs `python -m pip install -U edge-tts` to work at all).
