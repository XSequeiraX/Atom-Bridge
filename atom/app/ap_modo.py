"""Quem serve a rede das cameras: o dongle (ps-control) ou um AP externo ligado por Ethernet.

Dois modos, nunca os dois ao mesmo tempo (dois DHCP em 192.168.4.0/24 e o mesmo 192.168.4.1 em duas interfaces fazia as
cameras ligarem-se a um ou ao outro ao acaso):
  dongle    o dongle AR9271 e o AP (perfil AP_CON do NetworkManager, "shared", 192.168.4.1/24). E o de sempre.
  external  o AP e um aparelho externo (um AirPort Express em modo bridge, um router com o DHCP desligado...) numa porta
            Ethernet do Atom. O Atom fica com 192.168.4.1/24 nessa porta e e ele que da o DHCP (perfil PS_Control_Eth,
            "shared"): as reservas das Canon (dnsmasq-shared.d) valem la tambem. O AP externo tem de emitir o mesmo nome
            e palavra-passe do ps-control: as cameras e os gateways ESP32 ja os tem guardados.
O modo fica em ap_modo.json (ao lado do ap.conf; sobrevive as actualizacoes). Cada perfil so tem autoconnect no modo a que
pertence, por isso o modo escolhido e o que volta a subir depois de um reinicio.
Nada aqui corre sem o NetworkManager: sem nmcli, aplicar() diz que nao pode e nao muda o modo.
"""
import json
import os
import re
import subprocess

AQUI = os.path.dirname(os.path.abspath(__file__))
CAMINHO = os.environ.get("BRIDGE_AP_MODO_PATH") or os.path.join(AQUI, "ap_modo.json")
PERFIL_ETH = "PS_Control_Eth"
ENDERECO = "192.168.4.1/24"
MODOS = ("dongle", "external")
_IFACE = re.compile(r"[A-Za-z0-9._-]{1,15}")


def _correr(cmd, timeout=40):
    """subprocess.run sem excepcoes (um programa em falta ou um tempo esgotado voltam como falha). Os testes trocam-no."""
    try:
        return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    except FileNotFoundError:
        return subprocess.CompletedProcess(cmd, 127, "", "%s is not installed" % cmd[0])
    except subprocess.TimeoutExpired:
        return subprocess.CompletedProcess(cmd, 124, "", "%s did not answer in %d s" % (cmd[0], timeout))
    except OSError as e:
        return subprocess.CompletedProcess(cmd, 126, "", "%s: %s" % (cmd[0], e))


def _ler():
    try:
        with open(CAMINHO) as f:
            d = json.load(f)
        return d if isinstance(d, dict) else {}
    except (OSError, ValueError):
        return {}


def modo():
    """"dongle" (por omissao) ou "external"."""
    m = _ler().get("modo")
    return m if m in MODOS else "dongle"


def externo():
    return modo() == "external"


def eth_guardada():
    e = _ler().get("eth")
    return e if isinstance(e, str) and _IFACE.fullmatch(e) else None


def _guardar(m, eth):
    tmp = CAMINHO + ".tmp"
    with open(tmp, "w") as f:
        json.dump({"modo": m, "eth": eth}, f)
    os.replace(tmp, CAMINHO)


def ethernets():
    """As interfaces Ethernet fisicas que o NetworkManager conhece: [{iface, state}] (ligadas primeiro). Fora as virtuais
    (docker, veth, bridges, tailscale): essas o nmcli nao da como "ethernet" real, mas filtra-se por precaucao."""
    r = _correr(["nmcli", "-t", "-f", "DEVICE,TYPE,STATE", "device"], timeout=10)
    if r.returncode != 0:
        return []
    out = []
    for linha in r.stdout.splitlines():
        p = linha.split(":")
        if len(p) >= 3 and p[1] == "ethernet" and _IFACE.fullmatch(p[0]) \
                and not p[0].startswith(("veth", "docker", "br-", "virbr", "tailscale")):
            out.append({"iface": p[0], "state": p[2]})
    return sorted(out, key=lambda e: (not e["state"].startswith("connected"), e["iface"]))


def eth_iface():
    """A porta Ethernet do AP externo: a guardada se ainda existir, senao a primeira (ligada primeiro). None se nao ha."""
    todas = [e["iface"] for e in ethernets()]
    g = eth_guardada()
    if g and g in todas:
        return g
    return todas[0] if todas else None


def iface_ap(dongle):
    """A interface que serve as cameras agora: a Ethernet no modo externo, senao o dongle (o nome que se passa)."""
    if externo():
        return eth_guardada() or eth_iface() or dongle
    return dongle


def _nm(*args, timeout=40):
    return _correr(["nmcli", "-w", "25"] + list(args), timeout=timeout)


def _erro(r):
    return (r.stderr or r.stdout or "failed (rc=%s)" % r.returncode).strip().splitlines()[-1]


def _perfil_existe(nome):
    r = _correr(["nmcli", "-t", "-f", "NAME", "connection", "show"], timeout=10)
    return r.returncode == 0 and nome in [l.replace("\\:", ":") for l in r.stdout.splitlines()]


def _garantir_perfil_eth(eth):
    """Cria (ou corrige) o perfil da Ethernet: partilhado, 192.168.4.1/24, prioridade alta, sem IPv6."""
    prop = ["connection.interface-name", eth, "ipv4.method", "shared", "ipv4.addresses", ENDERECO,
            "ipv6.method", "ignore", "connection.autoconnect-priority", "100"]
    if _perfil_existe(PERFIL_ETH):
        r = _nm("connection", "modify", PERFIL_ETH, *prop)
    else:
        r = _nm("connection", "add", "type", "ethernet", "con-name", PERFIL_ETH, "ifname", eth, "autoconnect", "no",
                "ipv4.method", "shared", "ipv4.addresses", ENDERECO, "ipv6.method", "ignore",
                "connection.autoconnect-priority", "100")
    return None if r.returncode == 0 else "could not set up the Ethernet profile: " + _erro(r)


def aplicar(novo, eth=None, ap_con="PS_Control", ap_if="ps-control"):
    """Passa para o modo `novo`. (True, mensagem) ou (False, motivo); se falha, o modo guardado nao muda. Recusar-se a
    gravar e de quem chama. O perfil que sai fica sem autoconnect e em baixo ANTES de o outro subir: nunca os dois."""
    if novo not in MODOS:
        return False, "unknown mode (dongle or external)"
    if _correr(["nmcli", "-v"], timeout=10).returncode != 0:
        return False, "NetworkManager (nmcli) is not available"
    if novo == "external":
        porta = eth or eth_iface()
        if not porta or not _IFACE.fullmatch(porta):
            return False, "no Ethernet port found for the external AP"
        if porta not in [e["iface"] for e in ethernets()]:
            return False, "%s is not an Ethernet port of this machine" % porta
        erro = _garantir_perfil_eth(porta)
        if erro:
            return False, erro
        _nm("connection", "modify", ap_con, "connection.autoconnect", "no")
        _nm("connection", "down", ap_con)                           # (se o AP do dongle nao estava de pe, falha: nao importa)
        _nm("connection", "modify", PERFIL_ETH, "connection.autoconnect", "yes")
        r = _nm("connection", "up", PERFIL_ETH, "ifname", porta)
        if r.returncode != 0:
            _nm("connection", "modify", PERFIL_ETH, "connection.autoconnect", "no")     # volta atras: o dongle e o seguro
            _nm("connection", "modify", ap_con, "connection.autoconnect", "yes")
            _nm("connection", "up", ap_con, "ifname", ap_if)
            return False, "the Ethernet port did not come up (is the cable plugged in?): " + _erro(r)
        _guardar("external", porta)
        return True, "external AP on %s (%s)" % (porta, ENDERECO)
    # dongle
    if _perfil_existe(PERFIL_ETH):
        _nm("connection", "modify", PERFIL_ETH, "connection.autoconnect", "no")
        _nm("connection", "down", PERFIL_ETH)
    _nm("connection", "modify", ap_con, "connection.autoconnect", "yes")
    r = _nm("connection", "up", ap_con, "ifname", ap_if)
    if r.returncode != 0:
        # O AP do dongle nao subiu (dongle fora?). O modo muda na mesma: ao ligar o dongle o autoconnect trata do resto.
        _guardar("dongle", eth or eth_guardada())
        return True, "dongle mode saved, but the AP did not come up now (is the dongle plugged in?): " + _erro(r)
    _guardar("dongle", eth or eth_guardada())
    return True, "dongle AP %s on %s" % (ap_con, ap_if)


def clientes(iface):
    """Os MAC ligados a porta Ethernet do AP externo: [mac] da tabela de vizinhos (nao ha "station dump" num cabo). So os
    que respondem ou responderam ha pouco (REACHABLE/STALE/DELAY/PROBE), nao os FAILED/INCOMPLETE."""
    r = _correr(["ip", "neigh", "show", "dev", iface], timeout=6)
    macs = set()
    for linha in r.stdout.splitlines():
        p = linha.split()
        if "lladdr" in p and p[-1] in ("REACHABLE", "STALE", "DELAY", "PROBE"):
            macs.add(p[p.index("lladdr") + 1].lower())
    return sorted(macs)


def no_ar(iface):
    """True se a porta tem cabo e o 192.168.4.1 la posto (o perfil subiu)."""
    try:
        with open("/sys/class/net/%s/carrier" % iface) as f:
            if f.read().strip() != "1":
                return False
    except OSError:
        return False
    r = _correr(["ip", "-4", "-o", "addr", "show", "dev", iface], timeout=6)
    return ENDERECO.split("/")[0] + "/" in r.stdout
