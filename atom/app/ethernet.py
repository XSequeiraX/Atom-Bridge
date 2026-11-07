"""As portas Ethernet do Atom: quais ha, em que estado, para que servem, e o nome fixo de cada uma.

Um nome fixo e um ficheiro .link do systemd (/etc/systemd/network/20-bridge-eth-<caminho>.link) que casa a porta pelo caminho
do hardware (ID_PATH: nao muda com o MAC nem com a ordem em que as placas aparecem) e lhe da o nome. O instalador escreve-os
numa instalacao nova (pergunta um nome por porta) e renomear() escreve-os depois de instalado.
renomear() aplica o nome logo (ip link set name), sem esperar por um reinicio: os perfis do NetworkManager presos ao nome antigo
passam ao novo antes, e o do AP externo (ap_modo.json) tambem. A porta cai uns segundos enquanto muda de nome.
Nada aqui corre sem root; os testes trocam _correr e os caminhos.
"""
import json
import os
import re
import subprocess

SYS_NET = "/sys/class/net"
LINK_DIR = "/etc/systemd/network"
PREFIXO = "20-bridge-eth-"
_NOME = re.compile(r"[A-Za-z][A-Za-z0-9_-]{0,14}")


def _correr(cmd, timeout=20):
    try:
        return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    except FileNotFoundError:
        return subprocess.CompletedProcess(cmd, 127, "", "%s is not installed" % cmd[0])
    except subprocess.TimeoutExpired:
        return subprocess.CompletedProcess(cmd, 124, "", "%s did not answer in %d s" % (cmd[0], timeout))
    except OSError as e:
        return subprocess.CompletedProcess(cmd, 126, "", "%s: %s" % (cmd[0], e))


def _ler(caminho):
    try:
        with open(caminho) as f:
            return f.read().strip()
    except OSError:
        return None


def nome_ok(nome):
    """As mesmas regras do instalador (nome_iface_ok): 1 a 15 caracteres, comeca por letra, letras/digitos/_/-, nao wlanN/ethN."""
    if not isinstance(nome, str) or not _NOME.fullmatch(nome) or nome == "lo":
        return False
    return not re.fullmatch(r"(wlan|eth)\d.*", nome)


def portas():
    """Os nomes das portas Ethernet fisicas: tem dispositivo (as virtuais nao), tipo 1 (ARPHRD_ETHER) e nao sao Wi-Fi."""
    out = []
    try:
        nomes = sorted(os.listdir(SYS_NET))
    except OSError:
        return out
    for n in nomes:
        base = os.path.join(SYS_NET, n)
        if not os.path.exists(os.path.join(base, "device")) or os.path.isdir(os.path.join(base, "wireless")):
            continue
        if _ler(os.path.join(base, "type")) != "1":
            continue
        out.append(n)
    return out


def caminho(iface):
    """O ID_PATH do udev da porta (ex. pci-0000:02:00.0), ou None."""
    r = _correr(["udevadm", "info", "-q", "property", "-p", os.path.join(SYS_NET, iface)], timeout=10)
    for linha in r.stdout.splitlines():
        if linha.startswith("ID_PATH="):
            return linha[8:].strip() or None
    return None


def ficheiro_link(path):
    return os.path.join(LINK_DIR, PREFIXO + re.sub(r"[^A-Za-z0-9]", "-", path) + ".link")


def nome_fixo(path):
    """O nome que o .link desta porta lhe da (o que fica depois de um reinicio), ou None se nao ha .link nosso."""
    txt = _ler(ficheiro_link(path)) if path else None
    m = re.search(r"^Name=(\S+)", txt or "", re.M)
    return m.group(1) if m else None


def _ip(iface):
    r = _correr(["ip", "-4", "-o", "addr", "show", "dev", iface], timeout=6)
    m = re.search(r"inet (\d+\.\d+\.\d+\.\d+)", r.stdout)
    return m.group(1) if m else None


def _rota_padrao():
    r = _correr(["ip", "-4", "route", "show", "default"], timeout=6)
    m = re.search(r"\bdev (\S+)", r.stdout)
    return m.group(1) if m else None


def estado(ap_eth=None):
    """[{iface, mac, driver, link (bool), speed (Mb/s ou None), ip, role, fixed}] por porta. role: "cameras' AP" (a porta do
    AP externo), "internet" (a da rota por omissao) ou None. fixed: o nome que o .link lhe da (None se nao ha)."""
    padrao = _rota_padrao()
    out = []
    for n in portas():
        base = os.path.join(SYS_NET, n)
        drv = os.path.basename(os.path.realpath(os.path.join(base, "device", "driver"))) \
            if os.path.exists(os.path.join(base, "device", "driver")) else None
        vel = _ler(os.path.join(base, "speed"))
        try:
            vel = int(vel) if vel and int(vel) > 0 else None
        except ValueError:
            vel = None
        link = _ler(os.path.join(base, "carrier")) == "1"
        papel = "cameras' AP" if ap_eth and n == ap_eth else ("internet" if padrao == n else None)
        out.append({"iface": n, "mac": _ler(os.path.join(base, "address")), "driver": drv, "link": link,
                    "speed": vel if link else None, "ip": _ip(n), "role": papel, "fixed": nome_fixo(caminho(n))})
    return out


def _escrever_link(path, nome):
    os.makedirs(LINK_DIR, exist_ok=True)
    destino = ficheiro_link(path)
    tmp = destino + ".tmp"
    with open(tmp, "w") as f:
        f.write("# Escrito pelo Camera Bridge: nome fixo da porta Ethernet (pelo caminho do hardware).\n"
                "[Match]\nPath=%s\nType=ether\n\n[Link]\nName=%s\n" % (path, nome))
    os.replace(tmp, destino)
    return destino


def _perfis_presos(iface):
    """Os UUID dos perfis do NetworkManager presos a esta interface (connection.interface-name)."""
    r = _correr(["nmcli", "-g", "UUID", "connection", "show"], timeout=10)
    out = []
    for u in r.stdout.split():
        v = _correr(["nmcli", "-g", "connection.interface-name", "connection", "show", u], timeout=10)
        if v.returncode == 0 and v.stdout.strip() == iface:
            out.append(u)
    return out


def renomear(atual, novo, ap_modo_path=None):
    """Da o nome `novo` a porta `atual`, ja e para sempre. (True, mensagem) ou (False, motivo). Recusar a gravar e de quem chama."""
    if atual not in portas():
        return False, "%s is not an Ethernet port of this machine" % atual
    if not nome_ok(novo):
        return False, "the name must be 1 to 15 characters: letters, digits, _ and -, starting with a letter (not wlanN/ethN)"
    if novo == atual:
        return True, "%s already has that name" % atual
    if os.path.exists(os.path.join(SYS_NET, novo)):
        return False, "there is already an interface called %s" % novo
    path = caminho(atual)
    if not path:
        return False, "no hardware path for %s: it cannot get a fixed name" % atual
    try:
        _escrever_link(path, novo)
    except OSError as e:
        return False, "could not write the name file: %s" % e
    perfis = _perfis_presos(atual)
    for u in perfis:
        _correr(["nmcli", "connection", "modify", u, "connection.interface-name", novo], timeout=20)
    # Ja, sem reiniciar: a porta tem de estar em baixo para mudar de nome.
    passos = [["ip", "link", "set", "dev", atual, "down"], ["ip", "link", "set", "dev", atual, "name", novo],
              ["ip", "link", "set", "dev", novo, "up"]]
    ja = True
    for i, cmd in enumerate(passos):
        r = _correr(cmd)
        if r.returncode != 0 and i < 2:
            # Nao deu para mudar ja (porta ocupada): volta a subir com o nome antigo; o .link e os perfis ja estao com o
            # novo, por isso fica tudo certo no proximo arranque.
            _correr(["ip", "link", "set", "dev", atual, "up"])
            ja = False
            break
    if ap_modo_path:
        try:
            with open(ap_modo_path) as f:
                d = json.load(f)
            if isinstance(d, dict) and d.get("eth") == atual:
                d["eth"] = novo
                with open(ap_modo_path + ".tmp", "w") as f:
                    json.dump(d, f)
                os.replace(ap_modo_path + ".tmp", ap_modo_path)
        except (OSError, ValueError):
            pass
    if not ja:
        return True, "%s will be called %s after a restart of the Atom" % (atual, novo)
    return True, "%s is now %s" % (atual, novo)
