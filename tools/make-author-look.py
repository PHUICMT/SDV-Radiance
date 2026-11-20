"""Write assets/author-look.json: the look the author plays with, for the "Author's pick" preset.

    python tools/make-author-look.py [path to config.json]

Run it before a release, after the author has settled their own settings: it reads the config the
game is using (the deployed one by default), keeps the settings a share code calls part of a look
(the Look entries of src/Config/ShareCode.cs, plus the colour look's file), leaves out everything
about how the game runs (render scale and the rest are the player's own machine's business), and
records the machine it was played on, so the preset can say what it was tested on.
"""
import json
import os
import re
import subprocess
import sys
from datetime import date

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEPLOYED = r"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley\Mods\03_Graphics-FX\SDV-Radiance\config.json"


def look_settings():
    text = open(os.path.join(REPO, "src", "Config", "ShareCode.cs"), encoding="utf-8").read()
    names = [name for _, name, scope in re.findall(r"\((\d+), nameof\(ModConfig\.(\w+)\), (Look|Performance)\)", text) if scope == "Look"]
    return names + ["ColorGradeLut"]


def as_text(value):
    if isinstance(value, bool):
        return "True" if value else "False"
    if isinstance(value, float):
        return repr(value)
    return str(value)


def machine():
    def ask(command):
        out = subprocess.run(["powershell", "-NoProfile", "-Command", command], capture_output=True, text=True).stdout
        return out.strip()
    cpu = ask("(Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()")
    cpu = re.sub(r"\((R|TM)\)", "", cpu).replace("  ", " ").strip()
    ram = ask("[math]::Round((Get-CimInstance Win32_PhysicalMemory | Measure-Object -Property Capacity -Sum).Sum/1GB)")
    gpus = ask("Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name").splitlines()
    # The card the game draws on, not the processor's built-in graphics beside it.
    gpu = next((g.strip() for g in gpus if re.search(r"NVIDIA|Radeon|Arc", g)), gpus[0].strip() if gpus else "")
    return cpu, f"{ram} GB", gpu


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else DEPLOYED
    config = json.loads(open(path, encoding="utf-8-sig").read())
    values = {name: as_text(config[name]) for name in look_settings() if name in config}
    cpu, ram, gpu = machine()
    out = {
        "Cpu": cpu,
        "Ram": ram,
        "Gpu": gpu,
        "Taken": date.today().isoformat(),
        "Values": values,
    }
    target = os.path.join(REPO, "assets", "author-look.json")
    with open(target, "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print(f"{len(values)} look settings from {path}")
    print(f"tested on {cpu}, {ram}, {gpu}")
    print(f"wrote {target}")


if __name__ == "__main__":
    main()
