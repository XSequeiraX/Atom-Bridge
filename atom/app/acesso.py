"""Acesso a pagina e a API por PIN, para quem nao esta no Tailscale nem no proprio Atom (2026-10-14).

Quem esta no Atom (127.0.0.1: a consola, o servico USB) e na rede privada Tailscale (100.64.0.0/10,
fd7a:115c:a1e0::/48) entra como antes. Os outros (os iPads e telemoveis na rede das camaras, o Wi-Fi do local) tem
de entrar com o PIN numa pagina propria (/login). Ideia tirada do DumbAssets (github.com/DumbWareio/dumbassets):
  - PIN de comprimento variavel (4 a 12 digitos); guardado so como PBKDF2-SHA256 com sal (pin.json, 0600)
  - comparacao em tempo constante (hmac.compare_digest)
  - bloqueio depois de 5 tentativas erradas (15 min por endereco; com 20 erradas de quaisquer enderecos em 15 min, o
    login fecha para todos durante 15 min)
  - sessao num cookie aleatorio (HttpOnly, SameSite=Strict), guardada so no Atom; o PIN nunca vai para o cliente
  - limite de pedidos: 1 tentativa de PIN por segundo por endereco; 30 pedidos por segundo para quem nao e de confianca
Sem PIN definido, quem nao e de confianca nao entra (define-se na consola: GLOBAL SETTINGS > Web PIN).

Pedidos que mudam coisas (POST...) vindos de um browser tem de vir da propria pagina: um Origin de outro sitio e
recusado (uma pagina qualquer aberta no portatil ou no iPad nao consegue mandar reiniciar o Atom).
"""
import hashlib
import hmac
import getpass
import ipaddress
import json
import os
import secrets
import sys
import threading
import time
from urllib.parse import urlsplit

AQUI = os.path.dirname(os.path.abspath(__file__))
PIN_PATH = os.environ.get("BRIDGE_PIN_PATH") or os.path.join(AQUI, "pin.json")   # (o instalador define-o: o PIN fica no sitio final antes do codigo la estar)
COOKIE = "bridge_session"
SESSAO_S = 12 * 3600            # um dia de filmagem
ITERACOES = 200000
TENTATIVAS = 5
BLOQUEIO_S = 15 * 60
GLOBAL_MAX = 20
PEDIDOS_POR_S = 30
REDES_CONFIANCA = [ipaddress.ip_network(n) for n in
                   ("127.0.0.0/8", "::1/128", "100.64.0.0/10", "fd7a:115c:a1e0::/48")]
# O que um cliente sem sessao pode pedir: a pagina do PIN e o que ela usa.
LIVRES = ("/login", "/api/login", "/api/logout", "/fonts/",
          "/favicon.ico", "/icon-192.png", "/icon-512.png", "/manifest.json")   # nada de privado: o login tambem os pede

_lock = threading.Lock()
_sessoes = {}                   # token -> (expira, ip)
_falhas = {}                    # ip -> [horas das tentativas erradas]
_falhas_globais = []
_ultimo_login = {}              # ip -> hora da ultima tentativa
_pedidos = {}                   # ip -> [inicio da janela de 1 s, contagem]


def de_confianca(ip):
    try:
        a = ipaddress.ip_address((ip or "").split("%")[0])
    except ValueError:
        return False
    if getattr(a, "ipv4_mapped", None):
        a = a.ipv4_mapped
    return any(a in n for n in REDES_CONFIANCA)


# ---- PIN -------------------------------------------------------------------------------------------------------------
def _ler():
    try:
        with open(PIN_PATH) as f:
            d = json.load(f)
        return d if isinstance(d, dict) and d.get("hash") and d.get("salt") else None
    except (OSError, ValueError):
        return None


def tem_pin():
    return _ler() is not None


def _hash(pin, salt, it=ITERACOES):
    return hashlib.pbkdf2_hmac("sha256", pin.encode(), bytes.fromhex(salt), it).hex()


def definir_pin(pin):
    """Define (ou, com pin vazio/None, apaga) o PIN. (ok, erro). Fecha todas as sessoes."""
    if pin is None or pin == "":
        try:
            os.remove(PIN_PATH)
        except OSError:
            pass
        with _lock:
            _sessoes.clear()
        return True, None
    if not (pin.isdigit() and 4 <= len(pin) <= 12):
        return False, "the PIN must be 4 to 12 digits"
    salt = secrets.token_hex(16)
    d = {"salt": salt, "iterations": ITERACOES, "hash": _hash(pin, salt)}
    tmp = PIN_PATH + ".tmp"
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as f:
        json.dump(d, f)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, PIN_PATH)
    with _lock:
        _sessoes.clear()
    return True, None


def terminal_de_verdade(f):
    """Ha mesmo alguem num terminal neste ficheiro (stdin / stdout)? isatty() chega no Linux; no Windows o NUL (`< NUL`, um servico
    sem consola) tambem diz que e um terminal, e o getpass ficava eternamente a espera de uma tecla: la confirma-se que e uma consola
    de verdade (GetConsoleMode so funciona num handle de consola)."""
    try:
        if f is None or not f.isatty():
            return False
        if sys.platform == "win32":
            import ctypes
            import msvcrt
            modo = ctypes.c_uint()
            return bool(ctypes.windll.kernel32.GetConsoleMode(msvcrt.get_osfhandle(f.fileno()), ctypes.byref(modo)))
        return True
    except (OSError, ValueError, AttributeError):
        return False


PEDIR_PIN = ("No web PIN is set. Without one, phones and iPads on the cameras' Wi-Fi (or any other network) cannot open the\n"
             "page; this computer (and Tailscale) always can. Choose a PIN of 4 to 12 digits.")


def pedir_pin(obrigatorio=False, entrada=None, saida=print):
    """Pede o PIN da pagina (sem eco, duas vezes) e define-o. True se ficou definido. `obrigatorio`: Enter nao salta, pergunta ate
    haver um PIN (so Ctrl+C / Ctrl+D desistem: False). Sem `obrigatorio`, Enter segue sem PIN (True, sem PIN definido).
    `entrada`/`saida`: para o teste."""
    entrada = entrada or getpass.getpass
    saida(PEDIR_PIN)
    while True:
        try:
            p1 = entrada("PIN (4 to 12 digits%s): " % ("" if obrigatorio else ", Enter = no PIN"))
        except (EOFError, KeyboardInterrupt):
            saida("")
            return False
        if not p1:
            if obrigatorio:
                saida("A PIN is required.")
                continue
            saida("Going on without a PIN (set one later: console > GLOBAL SETTINGS > Web PIN).")
            return True
        if not (p1.isdigit() and 4 <= len(p1) <= 12):
            saida("The PIN must be 4 to 12 digits.")
            continue
        try:
            outro = entrada("The same PIN again: ")
        except (EOFError, KeyboardInterrupt):
            saida("")
            return False
        if outro != p1:
            saida("The two PINs differ: try again.")
            continue
        ok, err = definir_pin(p1)
        saida("Web PIN set." if ok else "Could not save the PIN: %s" % err)
        if ok:
            return True


def pedir_pin_no_arranque(entrada=None, saida=print, interativo=None, obrigatorio=None):
    """Pede o PIN da pagina ao arrancar o bridge, se ainda nao ha nenhum. So num terminal (sem terminal, como um servico do
    systemd, nao pergunta nada). No Windows e obrigatorio (nao ha instalador que o peca de outra forma); no Linux Enter segue
    sem PIN (o instalador ja o pediu). BRIDGE_PIN_PROMPT=0 desliga. True se perguntou.
    `entrada`/`saida`/`interativo`/`obrigatorio`: para o teste (o PIN escreve-se sem eco, com getpass)."""
    if tem_pin() or os.environ.get("BRIDGE_PIN_PROMPT") == "0":
        return False
    if interativo is None:
        interativo = terminal_de_verdade(sys.stdin) and terminal_de_verdade(sys.stdout)
    if not interativo:
        return False
    if obrigatorio is None:
        obrigatorio = sys.platform == "win32"
    pedir_pin(obrigatorio=obrigatorio, entrada=entrada, saida=saida)
    return True


def definir_do_ambiente_ou_perguntar():
    """Para o instalador: define o PIN com WEB_PIN (4 a 12 digitos) ou pergunta, sempre obrigatorio. Sai com 0 se ficou definido."""
    if tem_pin():
        return 0
    pin = os.environ.get("WEB_PIN")
    if pin:
        ok, err = definir_pin(pin)
        print("Web PIN set." if ok else "ERROR: could not set the web PIN: %s" % err)
        return 0 if ok else 1
    if not terminal_de_verdade(sys.stdin):
        print("No terminal to ask for the web PIN: set WEB_PIN=<4 to 12 digits> or run this in a terminal")
        return 1
    return 0 if pedir_pin(obrigatorio=True) else 1


def _pin_certo(pin):
    d = _ler()
    if d is None or not isinstance(pin, str) or not pin.isdigit() or len(pin) > 12:
        # Mesmo trabalho com um PIN invalido: o tempo da resposta nao diz nada
        _hash("0000", "00" * 16)
        return False
    return hmac.compare_digest(_hash(pin, d["salt"], int(d.get("iterations") or ITERACOES)), d["hash"])


# ---- login e sessoes -------------------------------------------------------------------------------------------------
def bloqueado(ip, agora=None):
    """Segundos que faltam do bloqueio deste endereco (ou do login para todos), 0 se nao esta bloqueado."""
    agora = agora or time.time()
    with _lock:
        _falhas_globais[:] = [t for t in _falhas_globais if agora - t < BLOQUEIO_S]
        if len(_falhas_globais) >= GLOBAL_MAX:
            return int(BLOQUEIO_S - (agora - _falhas_globais[0])) + 1
        f = [t for t in _falhas.get(ip, []) if agora - t < BLOQUEIO_S]
        if f:
            _falhas[ip] = f
        else:
            _falhas.pop(ip, None)              # nao ficam enderecos sem falhas a ocupar memoria
        if len(f) >= TENTATIVAS:
            return int(BLOQUEIO_S - (agora - f[0])) + 1
    return 0


def login(ip, pin):
    """(token ou None, erro, codigo HTTP)."""
    agora = time.time()
    if not tem_pin():
        return None, "no PIN set on the Atom (console: GLOBAL SETTINGS > Web PIN)", 403
    falta = bloqueado(ip, agora)
    if falta:
        return None, "too many wrong PINs: try again in %d min" % ((falta + 59) // 60), 429
    with _lock:
        if agora - _ultimo_login.get(ip, 0) < 1.0:
            return None, "too fast: wait a second", 429
        _ultimo_login[ip] = agora
        if len(_ultimo_login) > 500:           # nao cresce sem fim
            for k in [k for k, v in _ultimo_login.items() if agora - v > 60]:
                del _ultimo_login[k]
    if not _pin_certo(pin):
        with _lock:
            _falhas.setdefault(ip, []).append(agora)
            _falhas_globais.append(agora)
            if len(_falhas) > 500:                  # nao cresce sem fim (enderecos que falharam e nao voltaram)
                for k in [k for k, v in _falhas.items() if agora - v[-1] > BLOQUEIO_S]:
                    del _falhas[k]
            resta = TENTATIVAS - len(_falhas[ip])
        return None, ("wrong PIN" + (" (%d left)" % resta if resta > 0 else "")), 401
    token = secrets.token_urlsafe(32)
    with _lock:
        _falhas.pop(ip, None)
        for t in [t for t, (exp, _ip) in _sessoes.items() if exp < agora]:
            del _sessoes[t]
        _sessoes[token] = (agora + SESSAO_S, ip)
    return token, None, 200


def logout(token):
    with _lock:
        _sessoes.pop(token or "", None)


def sessao_valida(token):
    if not token:
        return False
    with _lock:
        s = _sessoes.get(token)
        if not s:
            return False
        if s[0] < time.time():
            del _sessoes[token]
            return False
    return True


def limite(ip):
    """False se este endereco passou o limite de pedidos por segundo."""
    agora = time.time()
    with _lock:
        jan = _pedidos.get(ip)
        if not jan or agora - jan[0] >= 1.0:
            _pedidos[ip] = [agora, 1]
            if len(_pedidos) > 500:                 # nao cresce sem fim
                for k in [k for k, v in _pedidos.items() if agora - v[0] > 10]:
                    del _pedidos[k]
            return True
        jan[1] += 1
        return jan[1] <= PEDIDOS_POR_S


def origem_ok(origin, host):
    """Um POST de um browser tem de vir da propria pagina (o Origin igual ao Host). Sem Origin (a consola, curl,
    os servicos do Atom) passa."""
    if not origin:
        return True
    if origin == "null":
        return False
    try:
        return urlsplit(origin).netloc.lower() == (host or "").lower()
    except ValueError:
        return False


LOGIN_PAGE = r"""<!doctype html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Camera Bridge</title>
<style>
body{margin:0;background:#111;color:#eee;font:16px system-ui,sans-serif;display:flex;min-height:100vh;align-items:center;justify-content:center}
form{background:#1c1c1c;padding:28px 24px;border-radius:12px;width:min(320px,90vw);text-align:center}
h1{font-size:18px;margin:0 0 18px;letter-spacing:.08em}
input{width:100%;box-sizing:border-box;font-size:28px;letter-spacing:.3em;text-align:center;padding:10px;border-radius:8px;border:1px solid #444;background:#000;color:#fff}
button{margin-top:16px;width:100%;padding:12px;font-size:16px;border:0;border-radius:8px;background:#2a7;color:#fff}
#m{min-height:1.4em;margin-top:12px;color:#f66;font-size:14px}
</style></head><body>
<form id="f" autocomplete="off"><h1>CAMERA BRIDGE</h1>
<input id="p" type="password" inputmode="numeric" pattern="[0-9]*" minlength="4" maxlength="12" placeholder="PIN" autofocus>
<button>ENTER</button><div id="m"></div></form>
<script>
// So para dentro da propria pagina: um unico '/' no inicio, sem '\\' (o browser le-o como '/'), sem caracteres de controlo
// (tab e nova linha sao tirados ao URL), e o URL resolvido tem de ficar na mesma origem.
function destino(n) {
  try {
    if (!/^\/(?![\/\\])/.test(n) || /[\\\u0000-\u001f\u007f]/.test(n)) return '/';
    const u = new URL(n, location.origin);
    // normalizar '/.//x' ou '/a/..//x' deixa '//x' (host); colapsar as barras iniciais
    return u.origin === location.origin ? '/' + u.pathname.replace(/^\/+/, '') + u.search + u.hash : '/';
  } catch (err) { return '/'; }
}
document.getElementById('f').onsubmit = e => {
  e.preventDefault();
  const p = document.getElementById('p'), m = document.getElementById('m');
  fetch('/api/login', {method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({pin: p.value})})
    .then(r => r.json().then(d => ({ok: r.ok, d})))
    .then(({ok, d}) => {
      p.value = '';
      if (!ok) { m.textContent = d.error || 'refused'; return; }
      const n = new URLSearchParams(location.search).get('next') || '/';
      location.replace(destino(n));
    })
    .catch(() => { m.textContent = 'no answer'; });
};
</script></body></html>"""


if __name__ == "__main__":
    # python acesso.py definir  -> define o PIN (WEB_PIN ou pergunta, obrigatorio); sai com 0 se ha PIN
    # python acesso.py tem      -> sai com 0 se ha PIN, 1 se nao
    comando = sys.argv[1] if len(sys.argv) > 1 else ""
    if comando == "definir":
        sys.exit(definir_do_ambiente_ou_perguntar())
    if comando == "tem":
        sys.exit(0 if tem_pin() else 1)
    sys.exit("usage: acesso.py definir | tem")
