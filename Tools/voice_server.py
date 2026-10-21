#!/usr/bin/env python3
"""
Just-in-time voice for CAIVR.

Every line the professor says, scripted or generated, is spoken live by a neural voice on
this machine's GPU. Nothing has to be baked ahead of time. Audio is streamed while it is
still being produced, so she starts talking about a quarter of a second after the request.

    python Tools/voice_server.py --prewarm
    -> http://127.0.0.1:5111          (--host 0.0.0.0 to let a Quest on the same network reach it)

Setup (once, in a venv with a CUDA build of PyTorch):
    python -m pip install -r Tools/voice_server_requirements.txt

Why a cache: a line is only ever synthesised once per voice. The cache lives on disk and
survives restarts, so a conversation that has been played before is instant. --prewarm
synthesises the scripted lines in the background at startup, in conversation order, and steps
aside the moment a live request arrives.

Endpoints
    POST /tts/stream  {"text": "...", "voice": "voxcpm_warm"}
        -> application/octet-stream, chunked. First 8 bytes: uint32 LE sample rate, uint32 LE
           channels. Then signed 16-bit little-endian PCM, as it is generated.
    POST /tts         same body -> audio/wav, whole clip (for clients that cannot stream)
    GET  /voices      -> voices and the default
    GET  /health      -> {"ok": true, ...cache and warm-up progress}
"""

import argparse
import hashlib
import importlib.util
import io
import json
import os
import re
import struct
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

sys.dont_write_bytecode = True

import numpy as np
import soundfile as sf

REPO = Path(__file__).resolve().parent.parent
HERE = Path(__file__).resolve().parent
# Not under the project's Temp/: Unity empties that folder every time it opens the project, which
# would throw the whole cache away. This lives in the user's profile instead, outside the repo.
CACHE_DIR = Path(os.environ.get("LOCALAPPDATA") or (Path.home() / ".cache")) / "CAIVR" / "VoiceCache"
CONVERSATION = REPO / "Assets/CAIVR/Resources/CAIVR/Conversations/consultation_demo.json"

WARM = ("A warm, calm woman in her early forties with a soft Australian accent, a kind university "
        "lecturer speaking naturally and unhurried")

# gain_db brings the raw output to about -20.6 LUFS, the level of the baked voice sets, so
# switching voice never changes the volume. Streaming cannot loudness-normalise afterwards.
VOICES = {
    # The voice from the bake-off, described in words. Identity can wander a little line to line.
    "voxcpm_warm": dict(desc=WARM, ref=None, gain_db=-1.9, steps=10, seed=7),
    # The same voice pinned to the exact take that was approved, so every line is the same person.
    "voxcpm_warm_locked": dict(desc=WARM, ref=HERE / "voices" / "warm_reference.wav",
                               gain_db=-1.9, steps=10, seed=7),
    # Same as voxcpm_warm with fewer refinement steps: about a third faster, for headroom when the
    # GPU is also drawing the headset view.
    "voxcpm_warm_fast": dict(desc=WARM, ref=None, gain_db=-1.9, steps=6, seed=7),
    # The "soft" voice from the bake-off (designed with Qwen3-TTS, too slow to run live), re-created
    # by pinning VoxCPM2 to the approved take.
    "voxcpm_soft": dict(desc=("A friendly middle-aged woman with a soft Australian accent. Reassuring and "
                              "thoughtful, with natural pauses and a gentle, slightly husky tone."),
                        ref=HERE / "voices" / "soft_reference.wav", gain_db=-1.9, steps=10, seed=7),
}
DEFAULT_VOICE = "voxcpm_warm"
CACHE_VERSION = "1"  # bump to invalidate every cached line when synthesis settings change

_sr = 48000
_model = None


def normalize_text(text):
    return re.sub(r"\s+", " ", text).strip()


def cache_key(voice, text):
    v = VOICES[voice]
    sig = f"{CACHE_VERSION}|{voice}|{v['steps']}|{v['seed']}|{v['gain_db']}|{normalize_text(text)}"
    return hashlib.sha256(sig.encode("utf-8")).hexdigest()[:32]


def cache_path(voice, text):
    return CACHE_DIR / f"{cache_key(voice, text)}.wav"


def to_pcm16(audio, gain_db):
    gain = 10 ** (gain_db / 20.0)
    return (np.clip(np.asarray(audio, dtype=np.float32) * gain, -1.0, 1.0) * 32767.0).astype("<i2")


# --- GPU arbitration -------------------------------------------------------
# One model, one GPU. A live request always wins: the warm-up checks for waiting requests
# between audio chunks (about 0.15 s apart) and gives up the line to let them through.

class Gpu:
    def __init__(self):
        self.lock = threading.Lock()
        self.cv = threading.Condition()
        self.live = 0

    def live_enter(self):
        with self.cv:
            self.live += 1
        self.lock.acquire()

    def live_exit(self):
        self.lock.release()
        with self.cv:
            self.live -= 1
            self.cv.notify_all()

    def background_acquire(self):
        while True:
            with self.cv:
                while self.live > 0:
                    self.cv.wait(timeout=0.5)
            if self.lock.acquire(blocking=False):
                return

    def background_release(self):
        self.lock.release()


gpu = Gpu()
_inflight = {}
_inflight_lock = threading.Lock()
stats = dict(hits=0, misses=0, warm_done=0, warm_total=0, warm_running=False)


def synth_chunks(voice, text):
    """Yield float32 audio chunks as the model produces them. Caller holds the GPU."""
    import torch
    v = VOICES[voice]
    torch.manual_seed(v["seed"])
    kwargs = dict(text=f"({v['desc']}){normalize_text(text)}", cfg_value=2.0,
                  inference_timesteps=v["steps"], normalize=True)
    if v["ref"]:
        kwargs["reference_wav_path"] = str(v["ref"])
    for chunk in _model.generate_streaming(**kwargs):
        yield np.asarray(chunk, dtype=np.float32).reshape(-1)


def save_cache(voice, text, pcm):
    CACHE_DIR.mkdir(parents=True, exist_ok=True)
    path = cache_path(voice, text)
    tmp = path.with_suffix(".part")
    # Write-then-rename so a killed process can never leave half a line that is served forever.
    sf.write(str(tmp), pcm, _sr, format="WAV", subtype="PCM_16")
    tmp.replace(path)


def read_cache(voice, text):
    path = cache_path(voice, text)
    if not path.exists():
        return None
    try:
        data, sr = sf.read(str(path), dtype="int16")
        return data.reshape(-1)
    except Exception:
        path.unlink(missing_ok=True)
        return None


def key_lock(key):
    with _inflight_lock:
        return _inflight.setdefault(key, threading.Lock())


def stream_pcm(voice, text, emit):
    """
    Send PCM for this line through emit(bytes). From cache if we have it, otherwise live,
    chunk by chunk, saving to the cache when (and only when) the line completes.
    Raises if emit fails, which is how a cancelled request stops the GPU work.
    """
    pcm = read_cache(voice, text)
    if pcm is not None:
        stats["hits"] += 1
        emit(pcm.tobytes())
        return "hit"

    with key_lock(cache_key(voice, text)):
        pcm = read_cache(voice, text)  # someone else may have just finished this exact line
        if pcm is not None:
            stats["hits"] += 1
            emit(pcm.tobytes())
            return "hit"

        stats["misses"] += 1
        parts = []
        gpu.live_enter()
        try:
            for chunk in synth_chunks(voice, text):
                block = to_pcm16(chunk, VOICES[voice]["gain_db"])
                parts.append(block)
                emit(block.tobytes())
        finally:
            gpu.live_exit()

        if parts:
            save_cache(voice, text, np.concatenate(parts))
        return "miss"


# --- warm-up ---------------------------------------------------------------

def scripted_lines():
    """Every scripted line, speaker lines first so the conversation's main path is ready first."""
    spec = importlib.util.spec_from_file_location("gvl", HERE / "generate_voice_lines.py")
    gvl = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(gvl)
    lines = gvl.collect_lines(None)
    ordered = [t for k, t in lines.items() if not k.endswith("_reprompt") and not k.startswith("_")]
    ordered += [t for k, t in lines.items() if k.endswith("_reprompt") or k.startswith("_")]
    seen, out = set(), []
    for t in ordered:
        if t not in seen:
            seen.add(t)
            out.append(t)
    return out


def prewarm(voice):
    try:
        lines = scripted_lines()
    except Exception as e:
        print(f"warm-up: cannot read the script ({e})", file=sys.stderr)
        return
    todo = [t for t in lines if not cache_path(voice, t).exists()]
    stats["warm_total"] = len(lines)
    stats["warm_done"] = len(lines) - len(todo)
    stats["warm_running"] = bool(todo)
    print(f"warm-up: {len(lines)} scripted lines for {voice}, {len(todo)} to synthesise")

    for text in todo:
        while True:
            gpu.background_acquire()
            parts, yielded = [], True
            try:
                for chunk in synth_chunks(voice, text):
                    parts.append(to_pcm16(chunk, VOICES[voice]["gain_db"]))
                    if gpu.live > 0:          # a live request is waiting: drop this line, retry later
                        yielded = False
                        break
            except Exception as e:
                print(f"warm-up failed on a line: {e}", file=sys.stderr)
                parts, yielded = [], True
            finally:
                gpu.background_release()
            if yielded:
                break
        if parts and yielded:
            save_cache(voice, text, np.concatenate(parts))
        stats["warm_done"] += 1

    stats["warm_running"] = False
    print("warm-up: done")


# --- HTTP ------------------------------------------------------------------

class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def _json(self, code, obj):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.startswith("/health"):
            cached = len(list(CACHE_DIR.glob("*.wav"))) if CACHE_DIR.exists() else 0
            self._json(200, dict(ok=True, voice=DEFAULT_VOICE, cached=cached, **stats))
        elif self.path.startswith("/voices"):
            self._json(200, dict(default=DEFAULT_VOICE, voices=sorted(VOICES)))
        else:
            self._json(404, dict(error="not found"))

    def _read_request(self):
        try:
            length = int(self.headers.get("Content-Length", 0))
            payload = json.loads(self.rfile.read(length) or b"{}")
        except Exception as e:
            self._json(400, dict(error=f"bad request: {e}"))
            return None
        text = normalize_text(payload.get("text") or "")
        if not text:
            self._json(400, dict(error="text is required"))
            return None
        if len(text) > 700:
            self._json(400, dict(error="text too long (700 characters max)"))
            return None
        voice = payload.get("voice") or DEFAULT_VOICE
        if voice not in VOICES:
            self._json(400, dict(error=f"unknown voice '{voice}'", voices=sorted(VOICES)))
            return None
        return voice, text

    def do_POST(self):
        if self.path.startswith("/tts/stream"):
            req = self._read_request()
            if req:
                self._stream(*req)
        elif self.path.startswith("/tts"):
            req = self._read_request()
            if req:
                self._whole(*req)
        else:
            self._json(404, dict(error="not found"))

    def _stream(self, voice, text):
        started = time.perf_counter()
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Transfer-Encoding", "chunked")
        self.send_header("Cache-Control", "no-store")
        self.end_headers()

        def emit(data):
            if data:
                self.wfile.write(b"%x\r\n" % len(data) + data + b"\r\n")
                self.wfile.flush()

        try:
            emit(struct.pack("<II", _sr, 1))
            how = stream_pcm(voice, text, emit)
            self.wfile.write(b"0\r\n\r\n")
            self.wfile.flush()
            print(f"{how:<4} {time.perf_counter() - started:5.2f}s  {voice}  {text[:60]!r}")
        except (BrokenPipeError, ConnectionResetError, OSError):
            print(f"cancelled by client  {text[:60]!r}")
            self.close_connection = True

    def _whole(self, voice, text):
        buf = []
        try:
            stream_pcm(voice, text, buf.append)
        except Exception as e:
            self._json(502, dict(error=str(e)))
            return
        pcm = np.frombuffer(b"".join(buf), dtype="<i2")
        out = io.BytesIO()
        sf.write(out, pcm, _sr, format="WAV", subtype="PCM_16")
        body = out.getvalue()
        self.send_response(200)
        self.send_header("Content-Type", "audio/wav")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def load_model():
    global _model, _sr
    from voxcpm import VoxCPM
    print("loading VoxCPM2 ...")
    _model = VoxCPM.from_pretrained("openbmb/VoxCPM2", load_denoiser=False)
    _sr = int(_model.tts_model.sample_rate)
    # First calls pay one-off costs (kernels, reference encoding). Pay them now, not mid-conversation.
    for voice in VOICES:
        gpu.live_enter()
        try:
            for _ in synth_chunks(voice, "Ready."):
                pass
        finally:
            gpu.live_exit()
    print(f"ready  sample rate {_sr} Hz  voices: {', '.join(sorted(VOICES))}")


def main():
    global DEFAULT_VOICE, CACHE_DIR
    # Without this, status lines sit in a buffer when output is redirected to a file or a launcher.
    sys.stdout.reconfigure(line_buffering=True)
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5111)
    parser.add_argument("--voice", default=DEFAULT_VOICE, choices=sorted(VOICES))
    parser.add_argument("--prewarm", action="store_true", help="synthesise the scripted lines in the background")
    parser.add_argument("--steps", type=int, help="override quality/speed for every voice (6 faster, 10 default)")
    parser.add_argument("--cache-dir", type=Path, help=f"where finished lines are kept (default {CACHE_DIR})")
    args = parser.parse_args()

    if args.cache_dir:
        CACHE_DIR = args.cache_dir
    DEFAULT_VOICE = args.voice
    if args.steps:
        for v in VOICES.values():
            v["steps"] = args.steps

    CACHE_DIR.mkdir(parents=True, exist_ok=True)
    load_model()

    if args.prewarm:
        threading.Thread(target=prewarm, args=(DEFAULT_VOICE,), daemon=True).start()

    server = ThreadingHTTPServer((args.host, args.port), Handler)
    server.daemon_threads = True
    print(f"CAIVR voice on http://{args.host}:{args.port}  default={DEFAULT_VOICE}  "
          f"cached={len(list(CACHE_DIR.glob('*.wav')))}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nstopped")


if __name__ == "__main__":
    main()
