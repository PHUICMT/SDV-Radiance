"""Load the save and call dump, printing the SERVER'S error body rather than just the status.

urllib raises on a 500 and drive.py let the traceback swallow the message, so three passes
failed with nothing but "HTTP Error 500: Error" to go on.
"""
import json, sys, time, urllib.error, urllib.request
sys.stdout.reconfigure(encoding="utf-8")

PORT_FILE = (r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
             r"\Mods\00_Frameworks\SDV-AgentBridge\port.txt")


def rpc(tool, args=None, timeout=1800, tries=40):
    """One call, retried while the game's MAIN THREAD is still busy.

    The bridge answers `ping` from its own listener thread, so a bridge that responds does not
    mean the game is ready to run anything: with 100 mods the main thread stays busy loading
    for a minute, and every job queued onto it comes back 500 "main-thread job timed out".
    Treating that as fatal is what killed all three dump passes.
    """
    port = int(open(PORT_FILE).read().strip())
    body = json.dumps({"tool": tool, "args": args or {}}).encode()
    last = ""
    for _ in range(tries):
        req = urllib.request.Request(f"http://127.0.0.1:{port}/rpc", data=body,
                                     headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return json.load(r)
        except urllib.error.HTTPError as e:
            last = e.read().decode("utf-8", errors="replace")
            if "main-thread job timed out" in last:
                time.sleep(5)
                continue
            print(f"  HTTP {e.code} from {tool}:\n{last[:3000]}")
            raise SystemExit(1)
    print(f"  {tool}: main thread never freed up. last: {last[:300]}")
    raise SystemExit(1)


t0 = time.time()
while time.time() - t0 < 300:
    try:
        rpc("ping", timeout=5)
        break
    except SystemExit:
        raise
    except Exception:
        time.sleep(3)
print(f"bridge up on port {open(PORT_FILE).read().strip()} after {time.time()-t0:.0f}s")

st = rpc("state", timeout=20).get("result", {})
if not st.get("ready"):
    saves = rpc("load").get("result", {}).get("saves", [])
    print("loading", saves[0])
    rpc("load", {"save": saves[0]})
    t0 = time.time()
    while time.time() - t0 < 600:
        st = rpc("state", timeout=20).get("result", {})
        if st.get("ready"):
            break
        time.sleep(5)
print("state:", json.dumps(st, ensure_ascii=False)[:300])

print("\ncalling dump all=True ...", flush=True)
t0 = time.time()
res = rpc("dump", {"all": True})
print(f"dump ok after {time.time()-t0:.0f}s: {json.dumps(res, ensure_ascii=False)[:400]}")
