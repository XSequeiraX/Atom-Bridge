"""Verificacao do repositorio, igual no GitHub (workflow Verificar) e no portatil: python3 ci/verificar.py

1. Todo o Python compila e o pyflakes nao encontra nomes indefinidos nem erros (avisos de estilo nao contam).
2. api_check: regras das rotas, clientes que chamam rotas que existem, docs/API.md em dia.
3. O JavaScript de todas as paginas do bridge tem sintaxe valida (node --check).
4. A consola arranca e responde no simulador (ci/consola_sim.py), nos cenarios do dia a dia.
5. Sony: o sony_ctl do repositorio saiu do sony_ctl.cpp actual e arranca com as bibliotecas da Sony ao lado; a frota
   (sony.py) com o simulador do ajudante (sony_sim.py), incluindo os termos da Sony antes da primeira camara.
Sai com 1 ao primeiro grupo que falhe, a dizer o que falhou.
"""
import glob
import os
import re
import shutil
import subprocess
import sys
import tempfile

RAIZ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(RAIZ, "ci"))

PYTHON = sorted(glob.glob(os.path.join(RAIZ, "atom", "app", "*.py"))) + [os.path.join(RAIZ, "bridge-ctl.py")] \
    + sorted(glob.glob(os.path.join(RAIZ, "firmware", "*.py"))) + sorted(glob.glob(os.path.join(RAIZ, "ci", "*.py")))
# Avisos que nao sao erros: importacoes sem uso (ha as opcionais), f-strings sem campos, nomes redefinidos de proposito.
IGNORAR = ("imported but unused", "f-string is missing placeholders", "redefinition of unused", "unable to detect undefined names")

# (nome, tamanho, cenario, teclas, textos que tem de haver no ecra)
CONSOLA = [
    ("dashboard", "100x40", "normal", [], ["REC  VIA  DEVICE", "< STATE >", "GATEWAYS"]),
    ("dashboard 80x24", "80x24", "normal", [], ["DEVICE", "STATE"]),
    ("vista IMAGE", "100x40", "normal", ["\\eOC"], ["< IMAGE >", "SETTINGS"]),
    ("falhas", "100x40", "falhas", [], ["offline", "FAULT"]),
    ("a gravar", "100x40", "rec", [], ["REC  00:03:1", "take 7", "cameras"]),
    ("rodape", "100x40", "normal", [], ["SPACE target", "ESC menu", "F1 help"]),
    ("menu DEVICES", "100x40", "normal", ["\\e", "2"], ["DEVICES", "MOBILE", "Add mobile"], ["Add iPhone", "IPHONE"]),
    ("menu DEVICES Sony", "100x50", "normal", ["\\e", "2"], ["SONY", "Add a Sony", "Sony SDK and search"]),
    ("linha Sony", "100x40", "normal", [], ["A7 IV"]),
    # O nome do device aparece inteiro no dashboard (ate 12 caracteres), sem "iPh"/"GP"/"BM".
    ("nomes inteiros", "100x40", "normal", [], ["iPhone Cabin", "GoPro 1"], ["iPh ", " GP 1"]),
    # As Canon so gravam em modo video: uma em modo foto aparece como "not video" (nao como "ready") e nao ha nada de fotos.
    ("Canon em modo foto", "100x40", "normal", [], ["not video"], ["PHOTO", "shots"]),
    ("AP externo na Ethernet", "100x40", "apext", [], ["EXT AP (Ethernet enp3s0)", "CAM-AP"], ["channel 6"]),
    ("ajuda", "100x40", "normal", ["?"], ["HELP", "Keys"]),
    # Issue #35: o nome ou o alias novo aparece logo no menu aberto (antes so depois de sair e voltar a entrar)
    ("alias novo no menu", "100x40", "normal", ["\\d", "\\d", "\\d", "\\r", "6", "\x7f" * 8 + "B-cam", "\\r", "!wait:2"],
     ["Alias  B-cam"], ["Alias  A-cam"]),
    ("nome novo no Config", "100x40", "normal", ["\\d", "\\d", "\\d", "\\r", "5", "2", "\x7f" * 20 + "iPhone Two", "\\r", "!wait:2"],
     ["Config  iPhone Two", "Rename  iPhone Two"], ["iPhone Cabine"]),
    ("nome novo no More", "100x40", "normal", ["\\d"] * 6 + ["\\r", "5", "2", "\x7f" * 20 + "FX3 B", "\\r", "!wait:2"],
     ["More  FX3 B"], ["More  A7 IV"]),
    ("dados da take", "100x40", "fim", ["!wait:4"], ["TAKE 7 DONE"]),
    # Os avisos de licenca: sem aceitar nao se entra; a consola so carrega depois do "y".
    # O PIN da pagina pede-se ao arrancar a consola, se nao ha nenhum (depois dos avisos de licenca); "n" segue, "y" define-o
    ("PIN: pergunta ao arrancar", "100x30", "sempin", [], ["No web PIN is set", "Set a PIN now?"], ["GATEWAYS"]),
    ("PIN: nao agora", "100x40", "sempin", ["n", "!wait:2"], ["GATEWAYS", "REC  VIA"], ["Set a PIN now?"]),
    ("PIN: define", "100x40", "sempin", ["y", "4821\\r", "4821\\r", "!wait:2"], ["Web PIN set"]),
    ("PIN: ja ha, nao pergunta", "100x40", "normal", [], ["GATEWAYS"], ["No web PIN is set"]),
    ("avisos de licenca", "100x30", "termos", [], ["LICENSES AND NOTICES"], ["GATEWAYS"]),
    ("avisos aceites", "100x40", "termos", ["\\r", "y", "!wait:2"], ["GATEWAYS", "REC  VIA"], ["Accept these notices"]),
    ("avisos recusados", "100x30", "termos", ["\\r", "n", "!wait:2"], ["Accept these notices"], ["GATEWAYS", "REC  VIA"]),
]


def grupo(nome, erros):
    print(("OK    " if not erros else "FALHA ") + nome)
    for e in erros[:40]:
        print("      " + e)
    return not erros


def python():
    erros = []
    for f in PYTHON:
        r = subprocess.run([sys.executable, "-m", "py_compile", f], capture_output=True, text=True)
        if r.returncode:
            erros.append(r.stderr.strip().splitlines()[-1])
    r = subprocess.run([sys.executable, "-m", "pyflakes"] + PYTHON, capture_output=True, text=True)
    if "No module named pyflakes" in r.stderr:
        return erros + ["pyflakes nao instalado (pip install pyflakes)"]
    erros += [os.path.relpath(x, RAIZ) for x in r.stdout.splitlines() if x.strip() and not any(i in x for i in IGNORAR)]
    return erros


def api():
    r = subprocess.run([sys.executable, os.path.join(RAIZ, "atom", "app", "api_check.py")], capture_output=True, text=True)
    return [] if r.returncode == 0 else [x for x in (r.stdout + r.stderr).splitlines() if x.strip()]


def javascript():
    erros = []
    for f in sorted(glob.glob(os.path.join(RAIZ, "atom", "app", "*.html"))):
        js = "\n".join(re.findall(r"<script>(.*?)</script>", open(f, encoding="utf-8").read(), re.S))
        if not js.strip():
            continue
        with tempfile.NamedTemporaryFile("w", suffix=".js", delete=False) as t:
            t.write(js)
        try:
            r = subprocess.run(["node", "--check", t.name], capture_output=True, text=True)
        except FileNotFoundError:
            return ["node nao instalado"]
        finally:
            os.unlink(t.name)
        if r.returncode:
            erros.append("%s: %s" % (os.path.basename(f), " ".join(r.stderr.split())[:300]))
    return erros


def consola():
    import consola_sim
    erros = []
    for nome, tam, cen, teclas, esperado, *ausente in CONSOLA:
        c, l = (int(x) for x in tam.split("x"))
        ecra = "\n".join(consola_sim.correr(c, l, cen, teclas))
        for mau in ("SCREEN ERROR", "Traceback", "error:"):
            if mau in ecra:
                erros.append("%s: '%s' no ecra" % (nome, mau))
        for t in esperado:
            if t not in ecra:
                erros.append("%s: falta '%s'" % (nome, t))
        for t in (ausente[0] if ausente else ()):
            if t in ecra:
                erros.append("%s: nao devia haver '%s'" % (nome, t))
        if any(e.startswith(nome + ":") for e in erros):
            erros.append("ecra de '%s':\n%s" % (nome, ecra))
    return erros


def sony():
    """A frota Sony contra o simulador do ajudante: ligar, REC/STOP confirmados, timecode, erros, retirar e repor."""
    import time
    app = os.path.join(RAIZ, "atom", "app")
    sys.path.insert(0, app)
    import sony as so
    erros = []
    tmp = tempfile.mkdtemp(prefix="sony-")
    f = so.Fleet(lambda m: None, os.path.join(tmp, "sonys.json"), comando=[sys.executable, os.path.join(app, "sony_sim.py")])
    f.start()

    def espera(cond, t=6.0):
        fim = time.time() + t
        while time.time() < fim:
            if cond():
                return True
            time.sleep(0.1)
        return False
    if not espera(f.disponivel):
        return ["o simulador do ajudante nao arrancou"]
    for args, quer in ((("x", "ILCE-7M4", "ip", "300.1.1.1"), "host"), (("x", "nada", "ip", "1.2.3.4"), "model"),
                       (("x", "ILME-FX6", "ip", "1.2.3.4"), "SSH"), (("x", "ILME-FX6", "usb", None, "ABC"), "USB")):
        ok, err = f.add(*args)
        if ok or quer not in str(err):
            erros.append("add%r devia ser recusado por %s: %r" % (args, quer, err))
    f.termos_ok = lambda: False
    ok, err = f.add("A7 IV", "ILCE-7M4", "ip", "192.168.4.80")
    if ok or err != so.TERMOS_FALTA:
        erros.append("uma Sony entrou sem os avisos de licenca aceites: %r" % err)
    f.termos_ok = lambda: True
    ok, cid = f.add("A7 IV", "ILCE-7M4", "ip", "192.168.4.80")
    ok2, mau = f.add(None, "ILCE-1", "ip", "192.168.4.99")
    if not ok or not ok2:
        return erros + ["add falhou: %s %s" % (cid, mau)]
    cam = f.get(cid)
    if not espera(cam.connected):
        return erros + ["a Sony do simulador nao ligou"]
    if not espera(lambda: f.get(mau).error):
        erros.append("a camara que nao responde nao tem erro")
    ok, err = cam.set_running(True)
    if not ok or not espera(cam.running):
        erros.append("REC nao confirmado: %s" % err)
    if f.remove(cid)[0]:
        erros.append("retirou-se uma Sony a gravar")
    if cam.set_timecode("01:00:00:00")[0]:
        erros.append("escreveu-se o timecode a gravar")
    ok, err = cam.set_running(False)
    if not ok or not espera(lambda: not cam.running()):
        erros.append("STOP nao confirmado: %s" % err)
    if not cam.set_timecode("01:00:00:00")[0]:
        erros.append("o timecode parado foi recusado")
    st = cam.status()
    if st.get("battery") != 87 or st.get("iso") != "800" or st.get("fnumber") != "2.8" or st.get("shutter") != "1/50":
        erros.append("estado mal lido: %r" % {k: st.get(k) for k in ("battery", "iso", "fnumber", "shutter")})
    if not f.remove(cid)[0] or cid in f.ids() or cid not in f.removed_list():
        erros.append("remove nao a pos na lista dos guardados")
    ok, novo = f.restore(cid)
    if not ok or novo != cid or not espera(f.get(cid).connected):
        erros.append("restore falhou: %s" % novo)
    return erros + sony_ctl()


def iphone_obturador():
    """O angulo do obturador do iPhone (app Blackmagic Camera) mostra-se com 1 casa decimal, truncado como a app; um valor
    impossivel da None e fica no registo."""
    import time
    app = os.path.join(RAIZ, "atom", "app")
    sys.path.insert(0, app)
    import bmcam
    erros, logs = [], []
    tmp = tempfile.mkdtemp(prefix="iph-")
    f = bmcam.Fleet(logs.append, os.path.join(tmp, "iphones.json"))
    ok, cid = f.add("iPhone", "192.168.4.50", 4444)
    cam = f.get(cid)
    cam._ok_at = cam._st_at = time.time()
    # O angulo do obturador: a app manda todas as casas (272.2727...); mostra-se com 1 casa, truncado como a app (272.2).
    for v, quer in ((272.2727272727273, 272.2), (272.72727272727275, 272.7), ("272,2", 272.2), (180.0, 180), (172.8, 172.8),
                    (0, None), (361, None), (None, None), ("x", None), (272272727272727272727272, None)):
        if bmcam.shutter_normal(v) != quer:
            erros.append("shutter_normal(%r) = %r, devia ser %r" % (v, bmcam.shutter_normal(v), quer))
    cam._set("shutter", {"shutterAngle": 272.2727272727273})
    if cam.status().get("shutter") != 272.2:
        erros.append("o estado devia dar 272.2 para 272.2727...: %r" % cam.status().get("shutter"))
    return erros


def ap_externo():
    """O modo da rede das cameras (dongle | AP externo na Ethernet): ap_modo.py com um nmcli de mentira. Confirma que o perfil
    que sai desce ANTES de o outro subir, que o perfil da Ethernet e partilhado em 192.168.4.1/24, que uma falha volta ao
    dongle sem mudar o modo guardado, e que a regra "sem partilha" segue a porta do modo."""
    app = os.path.join(RAIZ, "atom", "app")
    sys.path.insert(0, app)
    import ap_modo
    erros = []
    tmp = tempfile.mkdtemp(prefix="apmodo-")
    ap_modo.CAMINHO = os.path.join(tmp, "ap_modo.json")
    cmds, estado = [], {"perfis": ["PS_Control"], "falha_eth": False, "nmcli": True}

    def falso(cmd, timeout=40):
        cmds.append(list(cmd))
        ok = subprocess.CompletedProcess(cmd, 0, "", "")
        if cmd[0] != "nmcli":
            return ok
        if not estado["nmcli"]:
            return subprocess.CompletedProcess(cmd, 127, "", "nmcli is not installed")
        a = [x for x in cmd if x != "-w" and x != "25"][1:]
        if a[:1] == ["-v"]:
            return ok
        if a[:4] == ["-t", "-f", "DEVICE,TYPE,STATE", "device"]:
            return subprocess.CompletedProcess(cmd, 0, "ps-uplink:wifi:connected\nps-control:wifi:connected\n"
                                               "enp3s0:ethernet:disconnected\ndocker0:ethernet:unmanaged\nlo:loopback:unmanaged\n", "")
        if a[:4] == ["-t", "-f", "NAME", "connection"]:
            return subprocess.CompletedProcess(cmd, 0, "\n".join(estado["perfis"]) + "\n", "")
        if a[:1] == ["connection"] and a[1:2] == ["add"]:
            estado["perfis"].append("PS_Control_Eth")
        if a[:3] == ["connection", "up", "PS_Control_Eth"] and estado["falha_eth"]:
            return subprocess.CompletedProcess(cmd, 4, "", "Error: Connection activation failed: carrier not detected")
        return ok

    def pos(prefixo):
        prefixo = [x for x in prefixo if x != "nmcli"]
        return next((i for i, c in enumerate(cmds)
                     if [x for x in c if x not in ("nmcli", "-w", "25")][:len(prefixo)] == prefixo), -1)

    ap_modo._correr = falso
    if ap_modo.modo() != "dongle" or ap_modo.externo():
        erros.append("sem ficheiro o modo devia ser dongle: %r" % ap_modo.modo())
    if [e["iface"] for e in ap_modo.ethernets()] != ["enp3s0"]:
        erros.append("so a enp3s0 e Ethernet fisica (sem docker0, sem Wi-Fi): %r" % ap_modo.ethernets())
    ok, msg = ap_modo.aplicar("external", ap_con="PS_Control", ap_if="ps-control")
    if not ok or ap_modo.modo() != "external" or ap_modo.eth_guardada() != "enp3s0":
        erros.append("aplicar(external) devia dar certo e guardar a enp3s0: %r %r" % (ok, msg))
    add = next((c for c in cmds if "add" in c and "PS_Control_Eth" in c), None)
    if not add or "shared" not in add or "192.168.4.1/24" not in add or "enp3s0" not in add:
        erros.append("o perfil da Ethernet devia ser partilhado em 192.168.4.1/24 na enp3s0: %r" % add)
    descer, subir = pos(["nmcli", "connection", "down", "PS_Control"]), pos(["connection", "up", "PS_Control_Eth"])
    if descer < 0 or subir < 0 or descer > subir:
        erros.append("o AP do dongle devia descer ANTES de a Ethernet subir (descer %d, subir %d)" % (descer, subir))
    if pos(["connection", "modify", "PS_Control", "connection.autoconnect", "no"]) < 0:
        erros.append("o AP do dongle devia ficar sem autoconnect no modo externo")
    if ap_modo.iface_ap("ps-control") != "enp3s0":
        erros.append("iface_ap no modo externo devia ser a enp3s0: %r" % ap_modo.iface_ap("ps-control"))
    # de volta ao dongle
    del cmds[:]
    ok, msg = ap_modo.aplicar("dongle", ap_con="PS_Control", ap_if="ps-control")
    if not ok or ap_modo.modo() != "dongle" or ap_modo.iface_ap("ps-control") != "ps-control":
        erros.append("aplicar(dongle) devia dar certo: %r %r" % (ok, msg))
    d1, u1 = pos(["connection", "down", "PS_Control_Eth"]), pos(["connection", "up", "PS_Control", "ifname", "ps-control"])
    if d1 < 0 or u1 < 0 or d1 > u1:
        erros.append("a Ethernet devia descer ANTES de o dongle subir (%d, %d)" % (d1, u1))
    if pos(["connection", "modify", "PS_Control_Eth", "connection.autoconnect", "no"]) < 0:
        erros.append("a Ethernet devia ficar sem autoconnect no modo dongle")
    # falha ao subir a Ethernet (sem cabo): o modo nao muda e o dongle volta
    estado["falha_eth"] = True
    del cmds[:]
    ok, msg = ap_modo.aplicar("external", ap_con="PS_Control", ap_if="ps-control")
    if ok or ap_modo.modo() != "dongle" or "cable" not in msg:
        erros.append("sem cabo devia falhar e deixar o modo dongle: %r %r %r" % (ok, msg, ap_modo.modo()))
    if pos(["connection", "up", "PS_Control", "ifname", "ps-control"]) < 0:
        erros.append("depois de falhar devia voltar a subir o AP do dongle")
    estado["falha_eth"] = False
    for novo, eth, quer in (("outro", None, "unknown mode"), ("external", "eth9", "not an Ethernet port")):
        ok, msg = ap_modo.aplicar(novo, eth=eth)
        if ok or quer not in msg:
            erros.append("aplicar(%r, %r) devia recusar com %r: %r %r" % (novo, eth, quer, ok, msg))
    estado["nmcli"] = False
    ok, msg = ap_modo.aplicar("external")
    if ok or "NetworkManager" not in msg or ap_modo.modo() != "dongle":
        erros.append("sem nmcli devia recusar e deixar o modo: %r %r" % (ok, msg))
    # os clientes da porta: so os vizinhos vivos
    neigh = ("192.168.4.20 lladdr AA:BB:CC:00:00:01 REACHABLE\n192.168.4.21 lladdr aa:bb:cc:00:00:02 STALE\n"
             "192.168.4.22 lladdr aa:bb:cc:00:00:03 FAILED\n192.168.4.23  FAILED\n")
    ap_modo._correr = lambda cmd, timeout=40: subprocess.CompletedProcess(cmd, 0, neigh, "")
    if ap_modo.clientes("enp3s0") != ["aa:bb:cc:00:00:01", "aa:bb:cc:00:00:02"]:
        erros.append("clientes: so REACHABLE/STALE contam: %r" % ap_modo.clientes("enp3s0"))
    # a regra "sem partilha" segue a porta do modo (o script le /opt/camera-bridge: trocam-se os caminhos)
    sh = open(os.path.join(RAIZ, "atom-install", "system", "bridge-ap-nonat.sh")).read()
    conf, modof = os.path.join(tmp, "ap.conf"), os.path.join(tmp, "ap_modo.json")
    sh = sh.replace("/opt/camera-bridge/ap.conf", conf).replace("/opt/camera-bridge/ap_modo.json", modof)
    sh = sh.replace('case "$1" in', 'echo "IF=$AP_IF"; exit 0\ncase "$1" in', 1)
    open(conf, "w").write("AP_CON=Rig\nAP_IF=ps-control\n")
    script = os.path.join(tmp, "nonat.sh")
    open(script, "w").write(sh)
    for conteudo, quer in (None, "ps-control"), ('{"modo": "dongle", "eth": "enp3s0"}', "ps-control"), \
            ('{"modo": "external", "eth": "enp3s0"}', "enp3s0"):
        if conteudo is None:
            if os.path.exists(modof):
                os.remove(modof)
        else:
            open(modof, "w").write(conteudo)
        r = subprocess.run(["sh", script, "start"], capture_output=True, text=True)
        if r.stdout.strip() != "IF=" + quer:
            erros.append("bridge-ap-nonat com %r devia usar %s: %r %r" % (conteudo, quer, r.stdout, r.stderr))
    shutil.rmtree(tmp, ignore_errors=True)
    return erros


def nomes_interfaces():
    """O instalador pede os nomes das interfaces Wi-Fi numa instalacao nova (Enter fica com ps-uplink / ps-control), recusa os
    invalidos e repetidos, e numa actualizacao fica com os do ap.conf sem perguntar."""
    import pty
    erros = []
    lib = os.path.join(RAIZ, "atom-install", "lib_app.sh")
    tmp = tempfile.mkdtemp(prefix="ifnomes-")

    def corre(nova, env=None, entrada=None):
        cmd = ['bash', '-c', '. "$0"; nomes_interfaces "$1" "$2"; rc=$?; echo "RES=$rc|$UP_NAME|$AP_NAME"', lib, tmp, str(nova)]
        e = dict(os.environ, **(env or {}))
        e.pop("UP_NAME", None) if not (env and "UP_NAME" in env) else None
        e.pop("AP_NAME", None) if not (env and "AP_NAME" in env) else None
        if entrada is None:
            r = subprocess.run(cmd, stdin=subprocess.DEVNULL, capture_output=True, text=True, env=e, timeout=20)
            out = r.stdout + r.stderr
        else:
            mestre, escravo = pty.openpty()
            p = subprocess.Popen(cmd, stdin=escravo, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, env=e)
            os.close(escravo)
            os.write(mestre, entrada.encode())
            try:
                out, _ = p.communicate(timeout=20)
            except subprocess.TimeoutExpired:
                p.kill()
                out = "TIMEOUT"
            os.close(mestre)
        m = re.search(r"RES=(\d+)\|([^|]*)\|(\S*)", out)
        return (m.groups() if m else ("?", "?", "?")), out

    conf = os.path.join(tmp, "ap.conf")
    # instalacao nova num terminal: Enter no primeiro, um invalido e um repetido no segundo, depois um bom
    (rc, up, ap), out = corre(1, entrada="\nwlan0\nps-uplink\ncamap\n")
    if (rc, up, ap) != ("0", "ps-uplink", "camap"):
        erros.append("instalacao nova com perguntas: %r %r" % ((rc, up, ap), out[-400:]))
    elif "not wlanN" not in out or "different from the uplink" not in out:
        erros.append("devia explicar porque recusa wlan0 e o repetido: %r" % out[-400:])
    # sem terminal: os de sempre, sem perguntar
    (rc, up, ap), out = corre(1)
    if (rc, up, ap) != ("0", "ps-uplink", "ps-control") or "Name for" in out:
        erros.append("sem terminal devia ficar com ps-uplink/ps-control sem perguntar: %r" % ((rc, up, ap),))
    # do ambiente
    if corre(1, env={"UP_NAME": "net-in", "AP_NAME": "cams"})[0] != ("0", "net-in", "cams"):
        erros.append("UP_NAME/AP_NAME do ambiente deviam valer")
    for mau in ({"UP_NAME": "wlan0"}, {"AP_NAME": "nome-com-mais-de-15"}, {"UP_NAME": "x y"}, {"UP_NAME": "a", "AP_NAME": "a"}):
        if corre(1, env=mau)[0][0] == "0":
            erros.append("devia recusar %r" % mau)
    # actualizacao: os do ap.conf, sem perguntar mesmo num terminal
    open(conf, "w").write("AP_CON=Rig\nAP_IF=meu-ap\nUP_IF=minha-net\n")
    (rc, up, ap), out = corre(0, entrada="\n\n")
    if (rc, up, ap) != ("0", "minha-net", "meu-ap") or "Name for" in out:
        erros.append("actualizacao devia ficar com os do ap.conf sem perguntar: %r %r" % ((rc, up, ap), out[-300:]))
    shutil.rmtree(tmp, ignore_errors=True)
    return erros


def erros_curtos():
    """Os erros de rede chegam curtos a consola, a pagina e ao registo (issue #34), com as mensagens reais das bibliotecas;
    a copia do bridge-ctl.py da o mesmo que o atom/app/curto.py."""
    import importlib.util
    app = os.path.join(RAIZ, "atom", "app")
    sys.path.insert(0, app)
    import curto as c
    spec = importlib.util.spec_from_file_location("bridge_ctl_curto", os.path.join(RAIZ, "bridge-ctl.py"))
    ctl = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(ctl)
    casos = [
        ("HTTPConnectionPool(host='10.255.255.1', port=8080): Max retries exceeded with url: /x (Caused by "
         "ConnectTimeoutError(<urllib3.connection.HTTPConnection object at 0x7f3a2c1d0>, 'Connection to 10.255.255.1 "
         "timed out. (connect timeout=1)'))", "no answer (timeout)"),
        ("Connect failed: HTTPConnectionPool(host='127.0.0.1', port=8771): Read timed out. (read timeout=1)",
         "Connect failed: no answer (timeout)"),
        ("<urlopen error timed out>", "no answer (timeout)"),
        ("<urlopen error [Errno 111] Connection refused>", "connection refused"),
        ("ConnectTimeout: HTTPConnectionPool(host='192.168.4.50', port=4444): Max retries exceeded with url: "
         "/control/api/v1/transports/0/record", "no answer (timeout)"),
        ("HTTPConnectionPool(host='127.0.0.1', port=1): Max retries exceeded with url: /x (Caused by NewConnectionError("
         "'<urllib3.connection.HTTPConnection object at 0x7f>: Failed to establish a new connection: [Errno 111] "
         "Connection refused'))", "connection refused"),
        ("OSError: [Errno 113] No route to host", "unreachable"),
        ("<urlopen error [Errno -2] Name or service not known>", "name not found"),
        ("Record stop failed: ('Connection aborted.', RemoteDisconnected('Remote end closed connection without "
         "response'))", "Record stop failed: connection dropped"),
        ("Set ISO failed: 404 Client Error: Not Found for url: http://192.168.4.20:8080/ccapi/ver100/shooting/settings/iso",
         "Set ISO failed: HTTP 404 Not Found"),
        ("Connect failed: HTTPError: HTTP Error 403: Forbidden", "Connect failed: HTTP 403 Forbidden"),
        # O que nao e erro de rede fica como esta
        ("REC: canon1 skipped \u2014 not in video mode", "REC: canon1 skipped \u2014 not in video mode"),
        ("Shoot log write failed: [Errno 28] No space left on device", "Shoot log write failed: [Errno 28] No space left on device"),
        ("REC ALL: iPhone did not confirm (timeout)", "REC ALL: iPhone did not confirm (timeout)"),
    ]
    erros = []
    for txt, quer in casos:
        for nome, f in (("curto.py", c.curto), ("bridge-ctl.py", ctl.curto)):
            v = f(txt)
            if v != quer:
                erros.append("%s: %r -> %r, devia ser %r" % (nome, txt[:60], v, quer))
    return erros


def instalador():
    """instalar_app (atom-install/lib_app.sh) copia a app por cima de uma instalacao com um programa A CORRER (o sony_ctl,
    que o bridge mantem em execucao): nao pode falhar com "Text file busy", e o processo antigo segue com a sua versao."""
    import shutil
    import time
    t = tempfile.mkdtemp(prefix="instalador-")
    erros, proc = [], None
    try:
        app, opt = os.path.join(t, "app"), os.path.join(t, "opt")
        for d in (app, opt):
            os.makedirs(os.path.join(d, "sony"))
        for d in (app, opt):
            shutil.copy("/bin/sleep", os.path.join(d, "sony", "sony_ctl"))
        open(os.path.join(app, "bridge.py"), "w").write("import nucleo\n")
        proc = subprocess.Popen([os.path.join(opt, "sony", "sony_ctl"), "30"])
        time.sleep(0.3)
        with open(os.path.join(app, "sony", "sony_ctl"), "ab") as f:      # o programa novo e diferente do que corre
            f.write(b"\0nova versao")
        r = subprocess.run(["bash", "-c", 'source "$1"; NO_WAIT_REC=1 instalar_app "$2" "$3"', "x",
                            os.path.join(RAIZ, "atom-install", "lib_app.sh"), app, opt], capture_output=True, text=True)
        if r.returncode:
            erros.append("instalar_app falhou (rc %d) com um programa a correr: %s" % (r.returncode, (r.stdout + r.stderr)[-300:]))
        elif open(os.path.join(opt, "sony", "sony_ctl"), "rb").read() != open(os.path.join(app, "sony", "sony_ctl"), "rb").read():
            erros.append("o programa novo nao ficou instalado")
        elif proc.poll() is not None:
            erros.append("o processo antigo foi morto pela copia")
    finally:
        if proc:
            proc.kill()
            proc.wait()
        shutil.rmtree(t, ignore_errors=True)
    return erros


def sony_windows():
    """O que e do Windows no sony_ctl.cpp, testado no Linux: o conversor de texto UTF-16 -> UTF-8 (texto_utf8.h), a vista Linux do
    codigo (fonte_hash.py) e a sintaxe do que so compila no Windows."""
    pasta = os.path.join(RAIZ, "atom", "app", "sony")
    erros = []
    tmp = tempfile.mkdtemp(prefix="sony-win-")
    exe = os.path.join(tmp, "utf8")
    r = subprocess.run(["g++", "-std=c++17", "-Wall", "-Wextra", os.path.join(pasta, "texto_utf8_teste.cpp"), "-o", exe],
                       capture_output=True, text=True, cwd=pasta)
    if r.returncode:
        return ["texto_utf8_teste.cpp nao compilou:\n" + r.stderr[-600:]]
    r = subprocess.run([exe], capture_output=True, text=True)
    if r.returncode or r.stdout.strip() != "ok":
        erros.append("texto_utf8_teste falhou: " + (r.stdout + r.stderr)[-400:])
    sys.path.insert(0, pasta)
    import fonte_hash
    # a vista Linux tira os blocos do Windows e as suas diretivas, e mais nada
    amostra = "a\n#ifdef _WIN32\nw1\n#else\nl1\n#endif\nb\n#ifndef _WIN32\nl2\n#else\nw2\n#endif\nc\n"
    if fonte_hash.vista_linux(amostra) != "a\nl1\nb\nl2\nc\n":
        erros.append("fonte_hash.vista_linux: %r" % fonte_hash.vista_linux(amostra))
    for mau in ("#ifdef _WIN32\n#if X\n#endif\n#endif\n", "#ifdef _WIN32\nx\n"):
        try:
            fonte_hash.vista_linux(mau)
            erros.append("fonte_hash devia recusar %r" % mau)
        except ValueError:
            pass
    shutil.rmtree(tmp, ignore_errors=True)
    return erros


def pin_arranque():
    """O PIN da pagina pede-se ao arrancar (acesso.pedir_pin_no_arranque) e numa instalacao nova (pin_da_pagina do instalador)."""
    import shutil
    import stat
    sys.path.insert(0, os.path.join(RAIZ, "atom", "app"))
    import acesso
    erros = []
    tmp = tempfile.mkdtemp(prefix="pin-")
    acesso.PIN_PATH = os.path.join(tmp, "pin.json")
    antigo = os.environ.pop("BRIDGE_PIN_PROMPT", None)

    def corre(respostas, **kw):
        it, falas = iter(respostas), []
        r = acesso.pedir_pin_no_arranque(entrada=lambda _p: next(it), saida=falas.append, **kw)
        return r, " ".join(falas)

    def sem_pin():
        if acesso.tem_pin():
            os.remove(acesso.PIN_PATH)

    try:
        # bridge.py num terminal, sem PIN: pede duas vezes e define; a sessao antiga cai
        r, _ = corre(["4821", "4821"], interativo=True)
        if not (r and acesso.tem_pin() and acesso._pin_certo("4821")):
            erros.append("com 4821 duas vezes devia definir o PIN")
        # ja ha PIN: nao pergunta
        r, _ = corre([], interativo=True)
        if r:
            erros.append("com PIN definido nao devia perguntar")
        # curto, depois diferente, depois certo
        sem_pin()
        r, falas = corre(["12", "123456", "654321", "123456", "123456"], interativo=True)
        if not (acesso.tem_pin() and acesso._pin_certo("123456")) or "4 to 12" not in falas or "differ" not in falas:
            erros.append("PIN curto e PIN diferente: devia recusar e acabar a definir o 123456 (%r)" % falas)
        # Enter: segue sem PIN
        sem_pin()
        r, falas = corre([""], interativo=True)
        if not r or acesso.tem_pin() or "without a PIN" not in falas:
            erros.append("Enter devia seguir sem PIN")
        # obrigatorio (instalacao): Enter nao salta, pergunta ate haver PIN; Ctrl+D / Ctrl+C desistem sem PIN
        sem_pin()
        it = iter(["", "", "4821", "4821"])
        falas = []
        r = acesso.pedir_pin(obrigatorio=True, entrada=lambda _p: next(it), saida=falas.append)
        if not (r and acesso.tem_pin() and acesso._pin_certo("4821")) or " ".join(falas).count("A PIN is required") != 2:
            erros.append("obrigatorio: Enter devia ser recusado duas vezes e acabar com o PIN 4821 (%r)" % falas)
        sem_pin()

        def desiste(_p):
            raise EOFError
        if acesso.pedir_pin(obrigatorio=True, entrada=desiste, saida=lambda _t: None) or acesso.tem_pin():
            erros.append("obrigatorio: Ctrl+D devia desistir sem PIN")
        # sem terminal (servico do systemd): nao pergunta
        r, _ = corre([], interativo=False)
        if r or acesso.tem_pin():
            erros.append("sem terminal nao devia perguntar nem definir")
        # BRIDGE_PIN_PROMPT=0
        os.environ["BRIDGE_PIN_PROMPT"] = "0"
        r, _ = corre([], interativo=True)
        os.environ.pop("BRIDGE_PIN_PROMPT")
        if r:
            erros.append("BRIDGE_PIN_PROMPT=0 devia desligar o pedido")

        # O instalador (lib_app.sh: pin_da_pagina / pin_instalar). O PIN e obrigatorio numa instalacao nova.
        app = os.path.join(tmp, "app")
        opt = os.path.join(tmp, "opt")
        os.makedirs(app)
        os.makedirs(opt)
        shutil.copy(os.path.join(RAIZ, "atom", "app", "acesso.py"), app)
        lib = os.path.join(RAIZ, "atom-install", "lib_app.sh")
        pin = os.path.join(opt, "pin.json")

        def instalador(nova, web_pin=None):
            env = {k: v for k, v in os.environ.items() if k not in ("WEB_PIN", "BRIDGE_PIN_PATH")}
            if web_pin is not None:
                env["WEB_PIN"] = web_pin
            r = subprocess.run(["bash", "-c", '. "$1"; pin_da_pagina "$2" "$3" "$4"; echo "rc=$? novo=${PIN_NOVO:+sim}"; pin_instalar "$2"',
                                "_", lib, opt, app, nova], env=env, capture_output=True, text=True, stdin=subprocess.DEVNULL, timeout=60)
            return r.stdout + r.stderr
        out = instalador("1")
        if os.path.exists(pin) or "rc=1" not in out or "required" not in out:
            erros.append("instalacao nova sem terminal nem WEB_PIN: devia parar (rc=1), sem PIN: %r" % out[-220:])
        out = instalador("0")
        if os.path.exists(pin) or "rc=0" not in out or "no web PIN is set" not in out:
            erros.append("actualizacao sem terminal nem WEB_PIN: devia avisar e seguir (rc=0), sem PIN: %r" % out[-220:])
        out = instalador("1", "12")
        if os.path.exists(pin) or "rc=1" not in out:
            erros.append("instalacao nova com WEB_PIN invalido: devia parar (rc=1): %r" % out[-220:])
        out = instalador("1", "987654")
        if "rc=0" not in out or "novo=sim" not in out or not os.path.exists(pin):
            erros.append("instalacao nova com WEB_PIN=987654: devia recolher o PIN e pin_instalar poe-lo em opt: %r" % out[-220:])
        else:
            if stat.S_IMODE(os.stat(pin).st_mode) != 0o600:
                erros.append("o pin.json devia ficar 0600")
            acesso.PIN_PATH = pin
            if not acesso._pin_certo("987654"):
                erros.append("o pin.json do instalador nao valida o 987654")
            out = instalador("1", "111111")        # ja ha PIN: nao se pergunta nem se muda (uma actualizacao)
            if "novo=sim" in out or not acesso._pin_certo("987654"):
                erros.append("o instalador mudou um PIN que ja existia: %r" % out[-220:])
        sobras = [f for f in os.listdir("/tmp") if f.startswith("bridge-pin.")]
        if sobras:
            erros.append("ficaram ficheiros temporarios do PIN em /tmp: %s" % sobras[:3])
    finally:
        if antigo is not None:
            os.environ["BRIDGE_PIN_PROMPT"] = antigo
        shutil.rmtree(tmp, ignore_errors=True)
    return erros


def sony_ctl():
    """O sony_ctl do repositorio: corresponde ao sony_ctl.cpp (sony_ctl.fonte) e arranca (SDK, modelos) neste Linux x86_64."""
    import json
    import platform
    pasta = os.path.join(RAIZ, "atom", "app", "sony")
    erros = []
    try:
        fonte = open(os.path.join(pasta, "sony_ctl.fonte")).read().split()[0]
    except (OSError, IndexError):
        return ["falta atom/app/sony/sony_ctl.fonte: recompilar com sony/compilar.sh"]
    sys.path.insert(0, pasta)
    import fonte_hash
    with open(os.path.join(pasta, "sony_ctl.cpp"), encoding="utf-8", newline="") as f:
        if fonte != fonte_hash.hash_de(f.read(), "linux"):   # a vista Linux: sem os blocos _WIN32 (fonte_hash.py)
            erros.append("o sony_ctl.cpp mudou e o sony_ctl nao foi recompilado (sony/compilar.sh <SDK> atom/app/sony)")
    if platform.machine() != "x86_64":
        return erros
    try:
        r = subprocess.run([os.path.join(pasta, "sony_ctl")], input="models\nquit\n", capture_output=True, text=True,
                           timeout=30, cwd=pasta)
    except (OSError, subprocess.TimeoutExpired) as e:
        return erros + ["o sony_ctl nao correu: %s" % e]
    evs = [json.loads(l) for l in r.stdout.splitlines() if l.startswith("{")]
    pronto = next((e for e in evs if e.get("ev") == "ready"), None)
    modelos = next((e.get("models") for e in evs if e.get("ev") == "models"), None) or []
    if not pronto or "ILCE-7M4" not in modelos:
        erros.append("o sony_ctl nao arrancou bem (rc %s): %s %s" % (r.returncode, r.stdout[-300:], r.stderr[-300:]))
    return erros


if __name__ == "__main__":
    ok = True
    for nome, f in (("Python (compila, pyflakes)", python), ("API (api_check)", api),
                    ("JavaScript das paginas", javascript), ("Consola no simulador", consola),
                    ("Sony com o simulador do ajudante", sony), ("Sony no Windows: conversor de texto e vista Linux", sony_windows), ("PIN da pagina ao arrancar e no instalador", pin_arranque), ("Instalador: copia com um programa a correr", instalador),
                    ("iPhone: obturador com 1 casa decimal", iphone_obturador),
                    ("Erros de rede curtos (#34)", erros_curtos),
                    ("Modo do AP das cameras: dongle ou Ethernet externa", ap_externo),
                    ("Instalador: nomes das interfaces Wi-Fi", nomes_interfaces)):
        ok = grupo(nome, f()) and ok
    sys.exit(0 if ok else 1)
