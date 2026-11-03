#!/usr/bin/env python3
"""O sony_ctl arranca com o SDK ao lado e responde: python3 ci/sony_ctl_smoke.py CAMINHO/sony_ctl[.exe]

Manda `models`, `enum` e `quit` e confirma: o evento `ready` (o SDK iniciou), os modelos (ILCE-7M4 entre eles) e um `found`
(a procura de camaras correu, mesmo sem nenhuma ligada). Corre igual no Linux e no Windows (o fluxo "Sony Windows").
"""
import json
import os
import subprocess
import sys


def main(exe):
    exe = os.path.abspath(exe)
    r = subprocess.run([exe], input="models\nenum\nquit\n", capture_output=True, text=True, timeout=90, cwd=os.path.dirname(exe))
    evs = []
    for linha in r.stdout.splitlines():
        linha = linha.strip()
        if linha.startswith("{"):
            evs.append(json.loads(linha))
    pronto = next((e for e in evs if e.get("ev") == "ready"), None)
    modelos = next((e.get("models") for e in evs if e.get("ev") == "models"), None) or []
    achou = next((e for e in evs if e.get("ev") == "found"), None)
    erros = []
    if not pronto:
        erros.append("sem o evento ready (rc %s)" % r.returncode)
    if "ILCE-7M4" not in modelos:
        erros.append("o ILCE-7M4 nao esta nos %d modelos" % len(modelos))
    if achou is None or not isinstance(achou.get("cameras"), list):
        erros.append("a procura de camaras (enum) nao deu o evento found")
    if erros:
        print("ERRO", "; ".join(erros))
        print("stdout:", r.stdout[-600:])
        print("stderr:", r.stderr[-600:])
        return 1
    print("ok: SDK %s, %d modelos, %d camaras encontradas" % (pronto.get("sdk"), len(modelos), len(achou["cameras"])))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
