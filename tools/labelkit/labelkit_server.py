"""The pieces every label tool's server shares: the page and the kit's files, JSON in and out,
live changes pushed to every open page, and files written so a crash never leaves half of one.

A job (tools/labelers/<job>/) builds its own data and routes and hands them to serve():

    from labelkit_server import Events, serve, write_json_atomic
    events = Events()
    serve(page="water.html", get_routes={"/api/mods": ...}, post_routes={"/api/tiles": ...},
          events=events, port=8766, title="Water labels")

A GET route takes the query (a dict of lists) and returns a value (sent as JSON) or a Reply. A
POST route takes the request body, already parsed from JSON. Anything a route raises is sent back
as {"error": ...} with status 500, so a bad request never kills the server.
"""
import json, os, sys, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

KIT = os.path.dirname(os.path.abspath(__file__))
KIT_FILES = {"labelkit.css": "text/css; charset=utf-8", "labelkit-ui.js": "text/javascript; charset=utf-8",
             "labelkit-editor.js": "text/javascript; charset=utf-8"}


class Reply:
    """A route's answer that is not JSON: a picture, or a JSON error with its own status."""
    def __init__(self, body, kind, code=200, cache=False):
        self.body, self.kind, self.code, self.cache = body, kind, code, cache


def error(text, code=400):
    return Reply(json.dumps({"error": text}).encode("utf-8"), "application/json; charset=utf-8", code)


class Events:
    """Every change, numbered, so each open page can be sent what it has not seen yet."""
    def __init__(self, keep=500):
        self.version = 0
        self.events = []
        self.keep = keep
        self.changed = threading.Condition()

    def publish(self, **event):
        with self.changed:
            self.version += 1
            event["version"] = self.version
            self.events.append(event)
            del self.events[:-self.keep]
            self.changed.notify_all()


def write_json_atomic(path, value):
    """Written beside, then moved over: a file is either the old one or the new one, never half."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path + ".tmp", "w", encoding="utf-8") as file:
        json.dump(value, file, ensure_ascii=False, separators=(",", ":"))
    os.replace(path + ".tmp", path)


def make_handler(page, get_routes, post_routes, events):
    page_folder = os.path.dirname(os.path.abspath(page))

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def _send(self, code, body, kind, cache=False):
            self.send_response(code)
            self.send_header("Content-Type", kind)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "max-age=86400" if cache else "no-store")
            self.end_headers()
            self.wfile.write(body)

        def _answer(self, value):
            if isinstance(value, Reply):
                self._send(value.code, value.body, value.kind, value.cache)
            else:
                self._send(200, json.dumps(value, ensure_ascii=False).encode("utf-8"), "application/json; charset=utf-8")

        def do_GET(self):
            url = urlparse(self.path)
            if url.path == "/favicon.ico":
                self.send_response(204)
                self.end_headers()
            elif url.path in ("/", "/index.html"):
                with open(page, "rb") as file:
                    self._send(200, file.read(), "text/html; charset=utf-8")
            elif url.path.startswith("/kit/") and url.path[5:] in KIT_FILES:
                with open(os.path.join(KIT, url.path[5:]), "rb") as file:
                    self._send(200, file.read(), KIT_FILES[url.path[5:]])
            elif url.path.startswith("/page/"):
                # files beside the job's page (its own script or style), never above it
                name = os.path.basename(url.path[6:])
                path = os.path.join(page_folder, name)
                if not os.path.isfile(path):
                    return self._answer(error("not found", 404))
                kind = "text/javascript; charset=utf-8" if name.endswith(".js") else "text/css; charset=utf-8"
                with open(path, "rb") as file:
                    self._send(200, file.read(), kind)
            elif url.path == "/api/events":
                self.stream_events()
            elif url.path in get_routes:
                try:
                    self._answer(get_routes[url.path](parse_qs(url.query)))
                except Exception as problem:
                    self._answer(error(f"{type(problem).__name__}: {problem}", 500))
            else:
                self._answer(error("not found", 404))

        def do_POST(self):
            url = urlparse(self.path)
            if url.path not in post_routes:
                return self._answer(error("not found", 404))
            try:
                length = int(self.headers.get("Content-Length", "0"))
                body = json.loads(self.rfile.read(length) or b"{}")
                self._answer(post_routes[url.path](body))
            except Exception as problem:
                self._answer(error(f"{type(problem).__name__}: {problem}", 500))

        def stream_events(self):
            """Server-sent events: every change pushed to the page as it happens."""
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "keep-alive")
            self.end_headers()
            seen = events.version
            try:
                # a dropped line is tried again after a second, not the browser's default three
                self.wfile.write(f"retry: 1000\nevent: hello\ndata: {seen}\n\n".encode())
                self.wfile.flush()
                while True:
                    with events.changed:
                        events.changed.wait_for(lambda: events.version > seen, timeout=15)
                        fresh = [event for event in events.events if event["version"] > seen]
                    if not fresh:
                        self.wfile.write(b": still here\n\n")
                        self.wfile.flush()
                        continue
                    for event in fresh:
                        self.wfile.write(f"data: {json.dumps(event, ensure_ascii=False)}\n\n".encode("utf-8"))
                        seen = event["version"]
                    self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, OSError):
                return

    return Handler


def serve(page, get_routes, post_routes, events, port, title):
    class LabelServer(ThreadingHTTPServer):
        def handle_error(self, request, client_address):
            # a page closed or reloaded mid-request is not an error worth a traceback
            if isinstance(sys.exc_info()[1], (ConnectionAbortedError, ConnectionResetError, BrokenPipeError)):
                return
            super().handle_error(request, client_address)

    server = LabelServer(("127.0.0.1", port), make_handler(page, get_routes, post_routes, events))
    server.daemon_threads = True    # open live streams never keep the server from stopping
    print(f"{title}: open http://127.0.0.1:{port}", flush=True)
    server.serve_forever()
