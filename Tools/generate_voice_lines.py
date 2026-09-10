#!/usr/bin/env python3
"""
Bake the professor's lines to audio, from any TTS engine.

Each engine writes to its own folder under Resources so several can coexist and
be compared back to back in the Editor:

    Assets/CAIVR/Resources/CAIVR/VO_sapi/
    Assets/CAIVR/Resources/CAIVR/VO_elevenlabs/
    Assets/CAIVR/Resources/CAIVR/VO_azure/
    Assets/CAIVR/Resources/CAIVR/VO_google/
    Assets/CAIVR/Resources/CAIVR/VO_piper/

This is an authoring step. Nothing here ships or runs on a player's machine -
the game only ever plays the resulting audio files, which is why swapping
engines needs no code change at all.

Keys come from the environment, never from a file in the repo:

    ELEVENLABS_API_KEY
    AZURE_SPEECH_KEY  +  AZURE_SPEECH_REGION   (e.g. australiaeast)
    GOOGLE_TTS_API_KEY

Usage:
    python generate_voice_lines.py --engine elevenlabs --voice Addison
    python generate_voice_lines.py --engine azure --voice en-AU-NatashaNeural
    python generate_voice_lines.py --engine google --voice en-AU-Chirp3-HD-Aoede
    python generate_voice_lines.py --list-voices --engine elevenlabs
    python generate_voice_lines.py --engine azure --only greeting   # one line, for A/B
"""

import argparse
import base64
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SCRIPT = REPO / "Assets/CAIVR/Resources/CAIVR/Conversations/consultation_demo.json"
VO_ROOT = REPO / "Assets/CAIVR/Resources/CAIVR"

FALLBACK_REPROMPT = "Sorry, I didn't catch that. Could you say it again?"


def die(message):
    print(f"ERROR: {message}", file=sys.stderr)
    sys.exit(1)


def post(url, data, headers, timeout=60):
    request = urllib.request.Request(url, data=data, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.read()
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")[:600]
        die(f"{e.code} from {urllib.parse.urlparse(url).netloc}\n{body}")
    except urllib.error.URLError as e:
        die(f"could not reach {urllib.parse.urlparse(url).netloc}: {e.reason}")


def get(url, headers, timeout=30):
    request = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.read()
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")[:600]
        die(f"{e.code} from {urllib.parse.urlparse(url).netloc}\n{body}")


def env(name):
    value = os.environ.get(name, "").strip()
    if not value:
        die(f"{name} is not set. Export it and re-run - do not put keys in the repo.")
    return value


def ssml_escape(text):
    return (text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;"))


# --- engines ---------------------------------------------------------------
# Each returns (audio_bytes, file_extension).


def speak_elevenlabs(text, voice):
    key = env("ELEVENLABS_API_KEY")
    voice_id = resolve_elevenlabs_voice(voice, key)

    url = (f"https://api.elevenlabs.io/v1/text-to-speech/{voice_id}"
           f"?output_format=mp3_44100_128")

    payload = json.dumps({
        "text": text,
        # v3 is the most expressive; multilingual_v2 is the safe fallback.
        "model_id": os.environ.get("ELEVENLABS_MODEL", "eleven_multilingual_v2"),
        "voice_settings": {
            "stability": 0.45,      # lower = more expressive, more variable
            "similarity_boost": 0.8,
            "style": 0.3,
            "use_speaker_boost": True,
        },
    }).encode("utf-8")

    audio = post(url, payload, {
        "xi-api-key": key,
        "Content-Type": "application/json",
        "Accept": "audio/mpeg",
    })
    return audio, ".mp3"


_elevenlabs_cache = {}


def resolve_elevenlabs_voice(name, key):
    """Accept either a raw voice id or a human name like 'Addison'."""
    if not name:
        die("--voice is required for elevenlabs (try --list-voices)")

    # Voice ids are opaque 20-char strings; names are not.
    if len(name) >= 20 and " " not in name:
        return name

    if not _elevenlabs_cache:
        data = json.loads(get("https://api.elevenlabs.io/v2/voices?page_size=100",
                              {"xi-api-key": key}))
        for v in data.get("voices", []):
            _elevenlabs_cache[v["name"].lower()] = v["voice_id"]

    found = _elevenlabs_cache.get(name.lower())
    if not found:
        die(f"voice '{name}' not in your ElevenLabs library. "
            f"Available: {', '.join(sorted(_elevenlabs_cache)) or '(none)'}")
    return found


def speak_azure(text, voice):
    key = env("AZURE_SPEECH_KEY")
    region = os.environ.get("AZURE_SPEECH_REGION", "australiaeast").strip()
    voice = voice or "en-AU-NatashaNeural"

    # Slightly slowed and softened: a consultation is considered speech, not
    # an announcement. Azure's SSML control is its main advantage here.
    ssml = (
        f'<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" '
        f'xmlns:mstts="https://www.w3.org/2001/mstts" xml:lang="en-AU">'
        f'<voice name="{voice}">'
        f'<mstts:express-as style="chat">'
        f'<prosody rate="-6%">{ssml_escape(text)}</prosody>'
        f'</mstts:express-as></voice></speak>'
    ).encode("utf-8")

    audio = post(
        f"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1",
        ssml,
        {
            "Ocp-Apim-Subscription-Key": key,
            "Content-Type": "application/ssml+xml",
            "X-Microsoft-OutputFormat": "riff-24khz-16bit-mono-pcm",
            "User-Agent": "CAIVR",
        },
    )
    return audio, ".wav"


def speak_google(text, voice):
    key = env("GOOGLE_TTS_API_KEY")
    voice = voice or "en-AU-Chirp3-HD-Aoede"

    payload = json.dumps({
        "input": {"text": text},
        "voice": {"languageCode": "en-AU", "name": voice},
        "audioConfig": {"audioEncoding": "LINEAR16", "sampleRateHertz": 24000,
                        "speakingRate": 0.95},
    }).encode("utf-8")

    body = post(
        f"https://texttospeech.googleapis.com/v1/text:synthesize?key={urllib.parse.quote(key)}",
        payload,
        {"Content-Type": "application/json"},
    )
    return base64.b64decode(json.loads(body)["audioContent"]), ".wav"


def speak_piper(text, voice):
    """Local, offline, no account. Quality baseline to compare the cloud against."""
    import subprocess
    import tempfile

    exe = os.environ.get("PIPER_EXE", "piper")
    model = voice or os.environ.get("PIPER_MODEL", "")
    if not model:
        die("piper needs a voice model: --voice path/to/model.onnx (or PIPER_MODEL)")

    with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
        out_path = tmp.name

    try:
        subprocess.run([exe, "--model", model, "--output_file", out_path],
                       input=text.encode("utf-8"), check=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        return Path(out_path).read_bytes(), ".wav"
    except FileNotFoundError:
        die(f"piper binary not found at '{exe}'. Set PIPER_EXE.")
    except subprocess.CalledProcessError as e:
        die(f"piper failed: {e.stderr.decode('utf-8', 'replace')[:400]}")
    finally:
        Path(out_path).unlink(missing_ok=True)


ENGINES = {
    "elevenlabs": speak_elevenlabs,
    "azure": speak_azure,
    "google": speak_google,
    "piper": speak_piper,
}


# --- voice discovery -------------------------------------------------------


def list_voices(engine):
    if engine == "elevenlabs":
        data = json.loads(get("https://api.elevenlabs.io/v2/voices?page_size=100",
                              {"xi-api-key": env("ELEVENLABS_API_KEY")}))
        for v in data.get("voices", []):
            labels = v.get("labels", {}) or {}
            accent = labels.get("accent", "?")
            gender = labels.get("gender", "?")
            star = "  <-- Australian" if "austral" in str(accent).lower() else ""
            print(f"  {v['name']:<22} {gender:<8} accent={accent:<12} {v['voice_id']}{star}")

    elif engine == "azure":
        region = os.environ.get("AZURE_SPEECH_REGION", "australiaeast").strip()
        data = json.loads(get(
            f"https://{region}.tts.speech.microsoft.com/cognitiveservices/voices/list",
            {"Ocp-Apim-Subscription-Key": env("AZURE_SPEECH_KEY")}))
        for v in data:
            if v.get("Locale", "").startswith("en-AU"):
                print(f"  {v['ShortName']:<32} {v.get('Gender','?'):<8} {v.get('VoiceType','')}")

    elif engine == "google":
        data = json.loads(get(
            "https://texttospeech.googleapis.com/v1/voices?languageCode=en-AU"
            f"&key={urllib.parse.quote(env('GOOGLE_TTS_API_KEY'))}", {}))
        for v in data.get("voices", []):
            print(f"  {v['name']:<32} {v.get('ssmlGender','?')}")

    else:
        die(f"--list-voices is not supported for '{engine}'")


# --- main ------------------------------------------------------------------


def collect_lines(only=None):
    """Every distinct piece of speech, keyed by the filename it becomes."""
    data = json.loads(SCRIPT.read_text(encoding="utf-8"))
    lines = {}

    for node in data["nodes"]:
        node_id = node["id"]
        if only and node_id != only:
            continue

        if (node.get("speakerLine") or "").strip():
            lines[node_id] = node["speakerLine"].strip()

        if (node.get("reprompt") or "").strip():
            lines[f"{node_id}_reprompt"] = node["reprompt"].strip()

    if not only:
        lines["_fallback_reprompt"] = FALLBACK_REPROMPT

    return lines


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--engine", required=True, choices=sorted(ENGINES))
    parser.add_argument("--voice", default=None, help="voice name/id (engine specific)")
    parser.add_argument("--only", default=None, help="generate one node id only, for A/B")
    parser.add_argument("--list-voices", action="store_true")
    parser.add_argument("--force", action="store_true", help="regenerate existing files")
    args = parser.parse_args()

    if args.list_voices:
        list_voices(args.engine)
        return

    if not SCRIPT.exists():
        die(f"conversation script not found: {SCRIPT}")

    out_dir = VO_ROOT / f"VO_{args.engine}"
    out_dir.mkdir(parents=True, exist_ok=True)

    speak = ENGINES[args.engine]
    lines = collect_lines(args.only)

    if not lines:
        die(f"no lines matched --only '{args.only}'")

    print(f"engine : {args.engine}")
    print(f"voice  : {args.voice or '(engine default)'}")
    print(f"out    : {out_dir}")
    print(f"lines  : {len(lines)}  ({sum(len(t) for t in lines.values())} chars)")
    print()

    written = 0
    for key, text in sorted(lines.items()):
        existing = list(out_dir.glob(f"{key}.*"))
        if existing and not args.force:
            print(f"  skip  {key} (exists, use --force)")
            continue

        audio, ext = speak(text, args.voice)

        for stale in existing:
            stale.unlink()

        (out_dir / f"{key}{ext}").write_bytes(audio)
        print(f"  ok    {key}{ext}  {len(audio):,}b")
        written += 1

    print(f"\nGENERATED={written} into {out_dir.name}")
    print("Switch engines in Unity: VoiceLinePlayer > Resource Folder.")


if __name__ == "__main__":
    main()
