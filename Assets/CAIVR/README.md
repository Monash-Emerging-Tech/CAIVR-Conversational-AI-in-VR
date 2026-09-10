# CAIVR conversation framework — flat-screen demo

Proves the conversation loop from the Workerbee notes end to end, without VR:

> voice → text → pick a branch → continue → next question

No headset, no environment, no rigged professor. Those slot in later without
changing anything in here.

**Everything in this folder is free.** No API keys, no accounts, no paid
services, no usage caps.

---

## Run it

1. In Unity: **CAIVR → Create Speech Demo Scene**
2. Press **Play**
3. Read the background context, press **SPACE** to start
4. Talk (or type — see below). **R** repeats the professor's question.

That's the whole demo.

---

## Speech-to-text

| Backend | Cost | Where it works | Setup |
|---|---|---|---|
| **Windows Dictation** (default) | Free | Windows only — Editor + PC build | Enable *Settings → Privacy → Speech → Online speech recognition* |
| **Keyboard** (fallback) | Free | Everywhere | None — auto-selected when the mic is unavailable |

`SpeechService` probes for the microphone and silently falls back to typing, so
the demo never dead-ends in front of a stakeholder. Force either one with the
**Backend** dropdown on the component.

### The Quest problem

`UnityEngine.Windows.Speech` **does not exist on Android**, so it will not work
in a standalone Quest build. This is known and deliberate — it was the fastest
way to prove the loop works, not the shipping answer.

Free options for Quest when we get there, all of which sit behind the existing
`ISpeechRecognizer` interface and change nothing downstream:

- **Vosk** — offline, open source, small models, runs on-device
- **whisper.cpp / whisper.unity** — offline, open source, better accuracy, heavier
- **Unity Inference Engine** — run an ONNX speech model in-engine

---

## The System 1 vs System 2 decision

Still open in the notes, so nothing here depends on the answer. Both implement
`IBranchSelector` and swap via the **Selector Mode** dropdown on
`ConversationRunner`.

| | System 1 — `KeywordBranchSelector` | System 2 — `LocalLlmBranchSelector` |
|---|---|---|
| How | Keyword matching | A local LLM picks the branch |
| Cost | Free | Free |
| Speed | Instant | ~1s on a normal laptop |
| Offline | Yes | Yes — model runs on your machine |
| Handles unexpected phrasing | Poorly | Well |
| Deterministic | Yes | Mostly (temperature 0) |
| Setup | None | Install Ollama, pull a model |

Run the same script through both and judge them side by side — that comparison
is the point, and it's why the seam exists.

### Enabling System 2 (free, ~5 minutes, one time)

1. Install **Ollama**: <https://ollama.com/download>
2. `ollama pull llama3.2`
3. Leave it running (it serves on `localhost:11434`)
4. Set **Selector Mode → Ai Assisted** on `ConversationRunner`

No key, no account, no cost, and **student speech never leaves the machine** —
which matters given this goes in front of Monash students.

If Ollama isn't running, the HUD says so per-turn and the conversation
re-prompts rather than dying.

**Quest note:** a standalone headset can't host Ollama. Either point
`ollamaEndpoint` at a PC on the same network, or move to an on-device model via
Unity's Inference Engine. Both sit behind `IBranchSelector`.

---

## Writing conversations

Scenarios are JSON under `Resources/CAIVR/Conversations/`, deliberately **not**
ScriptableObjects or scene data — writers can edit them without opening Unity,
and they don't merge-conflict.

```
nodes[]
  id             unique key
  speakerLine    what the professor says
  expectation    designer note — also fed to System 2 as context
  reprompt       said when nothing matched
  isEnd          terminates the conversation
  branches[]
    label        shown in the debug HUD
    intent       plain English — this is what System 2 reasons over
    keywords[]   what System 1 matches on
    nextNodeId   where this branch goes
```

`contextVariants[]` holds the premade background blurbs from Workerbee #2, so
repeat runs of the same scenario don't start identically. One is picked at
random and shown before the conversation starts.

Run **CAIVR → Validate Conversation Scripts** to catch dangling `nextNodeId`s
and unreachable nodes before playing. The runner also validates on load and
refuses to start a broken script rather than failing halfway through.

---

## What's implemented from the meeting notes

- [x] Voice-to-text on the user end
- [x] Branch selection, both proposed systems, swappable
- [x] Back-and-forth Q&A rather than a linear script
- [x] Background context before starting, start prompt after a delay
- [x] Random premade context per run
- [x] "Ask to repeat that" (**R**)
- [x] Subtitles (the professor's line is always on screen)
- [ ] Toggleable subtitles — trivial once there's a settings menu
- [ ] Real-time translated subtitles
- [ ] Lipsync — `ProfessorLine` is the hook to drive it from
- [ ] Talking to multiple people at once — would need a speaker field per node
- [ ] In-world context reference (laptop / sticky note / notebook)

---

## Layout

```
Scripts/Speech/
  ISpeechRecognizer.cs          the swap point for STT backends
  WindowsDictationRecognizer.cs Windows only, free, no setup
  KeyboardRecognizer.cs         fallback, always works
  SpeechService.cs              picks a backend, filters low confidence

Scripts/Dialogue/
  ConversationData.cs           JSON model + validation
  IBranchSelector.cs            the System 1 / System 2 swap point
  KeywordBranchSelector.cs      System 1
  LocalLlmBranchSelector.cs     System 2, free + local
  ConversationRunner.cs         the state machine

Scripts/Demo/
  ConversationDemoHud.cs        throwaway flat-screen harness

Editor/
  DemoSceneBuilder.cs           generates the scene, validates scripts
```

`ConversationRunner` knows nothing about VR or UI — it raises events. The VR
scene will subscribe to the same events the demo HUD does.
