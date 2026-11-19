"""Uma instalacao nova comeca sem aparelhos; uma que ja existia fica como estava.

O bridge trazia de fabrica aparelhos de um rig concreto: as Canon R10 (192.168.4.100) e R6 (192.168.4.75), o telemovel "Mobile A"
(192.168.4.11) e o endereco Bluetooth de uma BMPCC. Quem instalava de novo via-os todos, sem os ter acrescentado. Agora o codigo
nao traz nenhum: os aparelhos e os gateways acrescentam-se em DEVICES (consola ou pagina) e ficam nos ficheiros do operador
(devices.json, iphones.json, gopros.json, c3.json, bmpccs.json), que o bridge le ao arrancar.

Um Atom que ja corria nao pode perder os aparelhos que tinha sem os ter guardado (eram implicitos). Por isso, na primeira vez que
arranca com este codigo, escreve nesses ficheiros o que o codigo antigo trazia de fabrica, so onde o operador nao tinha decidido
(uma Canon que retirou continua retirada, na lista das guardadas; um ficheiro que ja existe nao se mexe). Numa instalacao nova nao
escreve nada. Quem decide e a marca padroes.json: sem ela o Atom e "existente" se ja tem algum dos ficheiros de dados do bridge,
senao e "novo". Corre uma so vez (a marca fica) e antes de o bridge ler os ficheiros.
"""
import json
import os
import sys

import gravar

MARCA = "padroes.json"
# Os ficheiros que so existem num Atom que ja correu: se algum esta la, nao e uma instalacao nova.
FICHEIROS_DE_DADOS = ("settings.json", "shoot_log.csv", "devices.json", "iphones.json", "gopros.json", "c3.json",
                      "bmpccs.json", "events.log")

# O que o codigo antigo trazia de fabrica (so se usa para nao tirar nada a um Atom que ja existia).
LEGADO_CANON = {
    "r10": {"name": "R10", "ip": "192.168.4.100", "port": 8080, "https": False},
    "r6m2": {"name": "R6", "ip": "192.168.4.75", "port": 443, "https": True},
}
LEGADO_BMPCC = "CC:86:EC:2F:15:50"
LEGADO_MOBILE = {"iphones": {"iphone1": {"name": "Mobile A", "host": "192.168.4.11", "port": 4444, "enabled": True, "scene": ""}}}


def _escrever(caminho, d):
    tmp = gravar.tmp_unico(caminho)
    with open(tmp, "w") as f:
        json.dump(d, f, indent=1)
        f.flush()
        os.fsync(f.fileno())
    gravar.trocar(tmp, caminho)


def _legado_em_devices(pasta):
    """Poe em devices.json as Canon e a BMPCC que o codigo antigo trazia, onde o operador nao decidiu. True se mudou algo."""
    caminho = os.path.join(pasta, "devices.json")
    try:
        d = gravar.ler_json(caminho)
    except FileNotFoundError:
        d = {}
    except (OSError, ValueError):
        return False                                  # existe e esta ilegivel: nao se escreve por cima (o bridge trata-o ao ler)
    if not isinstance(d, dict):
        return False
    antes = json.dumps(d, sort_keys=True)
    canons = d.get("canons") if isinstance(d.get("canons"), dict) else {}
    guardadas = d.get("saved") if isinstance(d.get("saved"), dict) else {}
    retiradas = [x for x in (d.get("removed") or []) if isinstance(x, str)]
    esquecidas = [x for x in (d.get("forgotten") or []) if isinstance(x, str)]
    for cid, c in LEGADO_CANON.items():
        if cid in canons or cid in guardadas:
            continue                                  # o operador ja a tem (ou guardou-a): fica como esta
        if cid in retiradas:
            retiradas.remove(cid)                     # retirada de origem: passa para as guardadas (de origem ja nao ha)
            if cid in esquecidas:
                esquecidas.remove(cid)                # e esquecida: nao volta
            else:
                guardadas[cid] = dict(c)
        else:
            canons[cid] = dict(c)
    if canons or "canons" in d:
        d["canons"] = canons
    if guardadas or "saved" in d:
        d["saved"] = guardadas
    if retiradas or "removed" in d:
        d["removed"] = retiradas
    if esquecidas or "forgotten" in d:
        d["forgotten"] = esquecidas
    b = d.get("bmpcc") if isinstance(d.get("bmpcc"), dict) else {}
    b.setdefault("address", LEGADO_BMPCC)
    d["bmpcc"] = b
    if json.dumps(d, sort_keys=True) == antes:
        return False
    _escrever(caminho, d)
    return True


def migrar(pasta, log=None):
    """Corre antes de o bridge ler os ficheiros. Devolve "ja" (ja corrida), "novo" (nada escrito) ou "existente" (escreveu o legado)."""
    log = log or (lambda m: print(m, file=sys.stderr, flush=True))
    marca = os.path.join(pasta, MARCA)
    if os.path.exists(marca):
        return "ja"
    existente = any(os.path.exists(os.path.join(pasta, f)) for f in FICHEIROS_DE_DADOS)
    if existente:
        mudou = _legado_em_devices(pasta)
        mobile = os.path.join(pasta, "iphones.json")
        if not os.path.exists(mobile):
            _escrever(mobile, LEGADO_MOBILE)
            mudou = True
        if mudou:
            log("padroes: este Atom ja corria: guardados em devices.json e iphones.json os aparelhos que o codigo antigo trazia de fabrica")
    _escrever(marca, {"versao": 1, "instalacao": "existente" if existente else "nova"})
    return "existente" if existente else "novo"
