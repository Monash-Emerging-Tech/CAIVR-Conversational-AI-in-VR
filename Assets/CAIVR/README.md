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

| | System 1 — `KeywordBranchSelector` | System 2 — `LlmBranchSelector` |
|---|---|---|
| How | Keyword matching | A model reads intent and picks the branch |
| Cost | Free | Free (local model, or a free cloud tier) |
| Speed | Instant | ~1s local, ~0.5s on Groq |
| Runs on Quest / WebGL | Yes | Only via a cloud endpoint |
| Handles unexpected phrasing | Poorly | Well |
| Deterministic | Yes | Mostly (temperature 0) |
| Setup | None | An endpoint, and a key if it's a cloud one |

Run the same script through both and judge them side by side — that comparison
is the point, and it's why the seam exists.

**Why System 1 loses:** a student said *"I left it too late"* while the keyword
read `"left it late"`. One inserted word, no match. Keyword lists only ever
understand phrasings somebody typed in advance, so they need patching forever.

### Configuring System 2

`LlmBranchSelector` speaks the **OpenAI chat-completions format**, which nearly
every provider implements. The same code runs against a local model and a cloud
one — only the URL changes. That matters because **neither a standalone Quest
nor a WebGL build can host a model**, and both are shipping targets.

| Target | Endpoint | Model | Key |
|---|---|---|---|
| Local dev | `http://localhost:11434/v1/chat/completions` | `qwen2.5:7b` | none |
| Quest / WebGL | `https://api.groq.com/openai/v1/chat/completions` | `llama-3.3-70b-versatile` | free tier, no card |

For local: install [Ollama](https://ollama.com/download), `ollama pull qwen2.5:7b`,
leave it running. Nothing leaves the machine.

If the endpoint is unreachable, the runner says so once, **drops to System 1,
and redoes that turn** rather than silently taking a wrong branch.

**Never commit a key.** A key inside a build can be extracted from it — before
students see this, the request must go through a small server of ours that holds
the key instead.

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
  LlmBranchSelector.cs          System 2, local OR cloud (OpenAI-compatible)
  ConversationRunner.cs         the state machine

Scripts/Demo/
  ConversationDemoHud.cs        throwaway flat-screen harness

Editor/
  DemoSceneBuilder.cs           generates the scene, validates scripts
```

`ConversationRunner` knows nothing about VR or UI — it raises events. The VR
scene will subscribe to the same events the demo HUD does.
