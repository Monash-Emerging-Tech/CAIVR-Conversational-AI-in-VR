#!/usr/bin/env python3
"""
Local TTS service for CAIVR.

Unity POSTs text, gets MP3 back. Exists so lines that were never authored -
anything a language model generates mid-conversation - can still be spoken in
the professor's voice, without baking them ahead of time.

    python Tools/tts_server.py
    -> http://127.0.0.1:5111

Why a local server rather than calling the service straight from C#: the
upstream endpoint needs a signed, rotating token that the edge-tts library
already knows how to produce. Reimplementing that in C# would be a pile of
fragile crypto we would then have to maintain. A 100-line Python hop is the
cheaper trade.

Everything is cached to disk by (text, voice), so a line is slow exactly once.
The cache survives restarts, which means a conversation that has been played
before is effectively instant on every later run.

Endpoints
    POST /tts     {"text": "...", "voice": "en-AU-NatashaNeural"}  -> audio/mpeg
    GET  /health                                                    -> {"ok": true}
    GET  /warm                                                      -> pre-opens a connection
"""

import argparse
import asyncio
import hashlib
import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

try:
    import edge_tts
except ImportError:
    print("edge-tts missing. Run: python -m pip install edge-tts", file=sys.stderr)
    sys.exit(1)

DEFAULT_VOICE = "en-AU-NatashaNeural"
DEFAULT_RATE = "-5%"
CACHE_DIR = Path(__file__).resolve().parent.parent / "Temp" / "TtsCache"

_cache_lock = threading.Lock()


def cache_path(text, voice, rate):
    digest = hashlib.sha256(f"{voice}|{rate}|{text}".encode("utf-8")).hexdigest()[:32]
    return CACHE_DIR / f"{digest}.mp3"


def synthesize(text, voice, rate):
    """Return MP3 bytes, from cache when we have already said this line."""
    path = cache_path(text, voice, rate)

    if path.exists():
        return path.read_bytes(), True

    async def run():
        audio = bytearray()
        communicate = edge_tts.Communicate(text, voice, rate=rate)
        async for chunk in communicate.stream():
            if chunk["type"] == "audio":
                audio.extend(chunk["data"])
        return bytes(audio)

    data = asyncio.run(run())

    if data:
        with _cache_lock:
            CACHE_DIR.mkdir(parents=True, exist_ok=True)
            # Write-then-rename so a killed process cannot leave a half file
            # in the cache that we would happily serve forever afterwards.
            tmp = path.with_suffix(".part")
            tmp.write_bytes(data)
            tmp.replace(path)

    return data, False


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass  # the default logger prints a line per request; far too noisy

    def _send(self, code, body, content_type="application/json"):
        if isinstance(body, str):
            body = body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.startswith("/health"):
            self._send(200, json.dumps({"ok": True, "voice": DEFAULT_VOICE}))
        elif self.path.startswith("/warm"):
            # Pay the connection cost before the student is waiting on it.
            try:
                synthesize("Ready.", DEFAULT_VOICE, DEFAULT_RATE)
                self._send(200, json.dumps({"ok": True}))
            except Exception as e:
                self._send(500, json.dumps({"ok": False, "error": str(e)}))
        else:
            self._send(404, json.dumps({"error": "not found"}))

    def do_POST(self):
        if not self.path.startswith("/tts"):
            self._send(404, json.dumps({"error": "not found"}))
            return

        try:
            length = int(self.headers.get("Content-Length", 0))
            payload = json.loads(self.rfile.read(length) or b"{}")
        except Exception as e:
            self._send(400, json.dumps({"error": f"bad request: {e}"}))
            return

        text = (payload.get("text") or "").strip()
        if not text:
            self._send(400, json.dumps({"error": "text is required"}))
            return

        voice = payload.get("voice") or DEFAULT_VOICE
        rate = payload.get("rate") or DEFAULT_RATE

        try:
            audio, hit = synthesize(text, voice, rate)
        except Exception as e:
            self._send(502, json.dumps({"error": str(e)}))
            return

        if not audio:
            self._send(502, json.dumps({"error": "engine returned no audio"}))
            return

        self.send_response(200)
        self.send_header("Content-Type", "audio/mpeg")
        self.send_header("Content-Length", str(len(audio)))
        self.send_header("X-Cache", "hit" if hit else "miss")
        self.end_headers()
        self.wfile.write(audio)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5111)
    parser.add_argument("--warm", action="store_true", help="synthesize once at startup")
    args = parser.parse_args()

    CACHE_DIR.mkdir(parents=True, exist_ok=True)
    cached = len(list(CACHE_DIR.glob("*.mp3")))

    if args.warm:
        try:
            synthesize("Ready.", DEFAULT_VOICE, DEFAULT_RATE)
        except Exception as e:
            print(f"warm-up failed (continuing): {e}", file=sys.stderr)

    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"CAIVR TTS on http://{args.host}:{args.port}  voice={DEFAULT_VOICE}  cached={cached}")
    print("Ctrl+C to stop.")

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nstopped")


if __name__ == "__main__":
    main()
