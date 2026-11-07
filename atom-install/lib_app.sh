#!/bin/bash
# Instala o codigo do repositorio (atom/app) em $OPT, com copia de seguranca e reposicao (2026-10-13).
# O repositorio e a fonte unica do codigo: o instalador copia-o em vez de aplicar patches. A configuracao e os
# dados do operador (settings.json, shoot_log.csv, c3.json, ...) nao estao em atom/app e nunca se tocam.
#
#   instalar_app <pasta-da-app> <opt>   verifica, guarda uma copia do que corre agora (BK) e copia a app
#   reverter_app <opt>                  repoe o CODIGO da copia (BK) e reinicia os servicos; os dados do operador ficam
#   esperar_stop [texto]                espera pelo STOP se ha uma take a decorrer (NO_WAIT_REC=1 salta)
# Devolve 0 se instalou; nada alterado se a verificacao falhar.

PORT="${PORT:-8080}"

# O que o bridge e os servicos escrevem em runtime (dados e configuracao do operador, chaves, registos, copias .bak/.tmp):
# nunca se repoe a partir da copia, que e de antes de o operador os mudar. Lista a manter com o copia.py (FICHEIROS) e com
# o bridge-repor-codigo.sh. O nome e relativo a $opt.
e_dado() {
    case "$1" in
        *.csv|*.log) return 0 ;;                     # shoot_log.csv, events.log e os arquivos com data
        settings.json|devices.json|usb_map.json|sftp.json|sftp_known_hosts|c3.json) return 0 ;;
        bmpccs.json|ap_modo.json|iphones.json|gopros.json|sonys.json|termos.json|gw_fw.json|gw_fw_pendente.json|profiles.json) return 0 ;;
        pin.json|ap.conf|repo.path|version.json|take_aberto.json|wifi.json|gw_token) return 0 ;;
        escrito.json|*/escrito.json|*.bak|*.bak-*|*.tmp|*.falhou|__pycache__/*|*/__pycache__/*) return 0 ;;
    esac
    return 1
}

# O PIN da pagina web: quem nao esta no Atom nem no Tailscale so entra com ele (acesso.py). Sem PIN nenhum telemovel nem iPad na rede
# das cameras abre a pagina. E OBRIGATORIO numa instalacao nova: pede-se logo no inicio (antes dos pacotes, para falhar depressa) e
# so se acaba se houver PIN. WEB_PIN=... (4 a 12 digitos) define-o sem perguntar; num terminal pergunta (sem eco, duas vezes, e Enter
# nao salta). Sem terminal nem WEB_PIN: uma instalacao nova PARA (return 1); uma actualizacao (o botao, o atualizar_atom.sh) avisa e
# segue, para nao a partir. Um PIN que ja existe nunca se pergunta nem se muda.
# O PIN recolhido fica num ficheiro temporario ($PIN_NOVO, so o hash, 0600): $opt so se cria no passo de copiar o codigo (uma pasta
# que ja la estivesse fazia da instalacao nova uma "actualizacao" com copia de seguranca). pin_instalar poe-no no sitio no fim.
pin_da_pagina() {
    local opt="${1:-$OPT}"
    local app="${2:-$opt}" nova="${3:-0}"
    PIN_NOVO=""
    [ -e "$opt/pin.json" ] && return 0
    [ -f "$app/acesso.py" ] || return 0
    local tmp
    tmp="$(mktemp -u /tmp/bridge-pin.XXXXXX)"
    if BRIDGE_PIN_PATH="$tmp" python3 "$app/acesso.py" definir; then
        PIN_NOVO="$tmp"
        return 0
    fi
    rm -f "$tmp" "$tmp.tmp"
    if [ "$nova" = 1 ]; then
        echo "ERROR: the web PIN is required for a new install." >&2
        return 1
    fi
    echo "NOTE: no web PIN is set: phones and iPads on the cameras' Wi-Fi cannot open the page. Set one in the console (GLOBAL SETTINGS > Web PIN) or run the installer in a terminal / with WEB_PIN=..."
    return 0
}

# Os nomes fixos das duas interfaces Wi-Fi: UP_NAME (o radio interno, o uplink) e AP_NAME (o dongle AR9271 do AP das cameras).
# Por ordem: o que vier no ambiente (UP_NAME=... AP_NAME=...), o que ja esta no ap.conf (uma actualizacao nunca os muda nem
# pergunta), e numa instalacao nova num terminal pergunta-se (Enter fica com o sugerido). Sem terminal: os de sempre.
# Um nome valido: 1 a 15 caracteres (limite do kernel), comeca por letra, so letras, digitos, _ e -; nao wlanN nem ethN (os nomes
# que o kernel da sozinho e que podem chocar no arranque); os dois diferentes.
nome_iface_ok() {
    case "$1" in ''|lo|wlan[0-9]*|eth[0-9]*) return 1 ;; esac
    [ "${#1}" -le 15 ] && printf '%s' "$1" | grep -Eq '^[A-Za-z][A-Za-z0-9_-]*$'
}
nomes_interfaces() {
    local opt="${1:-$OPT}" nova="${2:-0}" conf v
    conf="$opt/ap.conf"
    if [ -z "${UP_NAME:-}" ] && [ -f "$conf" ]; then UP_NAME="$(sed -n 's/^UP_IF=//p' "$conf" | head -n1)"; fi
    if [ -z "${AP_NAME:-}" ] && [ -f "$conf" ]; then AP_NAME="$(sed -n 's/^AP_IF=//p' "$conf" | head -n1)"; fi
    if [ "$nova" = 1 ] && [ -t 0 ]; then
        while [ -z "${UP_NAME:-}" ]; do
            read -rp "Name for the internal Wi-Fi (the uplink, internet) [ps-uplink]: " v
            v="${v:-ps-uplink}"
            if nome_iface_ok "$v"; then UP_NAME="$v"; else echo "  1 to 15 characters: letters, digits, _ and -, starting with a letter (not wlanN/ethN)"; fi
        done
        while [ -z "${AP_NAME:-}" ]; do
            read -rp "Name for the USB dongle (the cameras' AP) [ps-control]: " v
            v="${v:-ps-control}"
            if ! nome_iface_ok "$v"; then echo "  1 to 15 characters: letters, digits, _ and -, starting with a letter (not wlanN/ethN)"
            elif [ "$v" = "$UP_NAME" ]; then echo "  it must be different from the uplink name ($UP_NAME)"
            else AP_NAME="$v"; fi
        done
    fi
    UP_NAME="${UP_NAME:-ps-uplink}"
    AP_NAME="${AP_NAME:-ps-control}"
    nome_iface_ok "$UP_NAME" || { echo "ERROR: invalid uplink interface name '$UP_NAME'" >&2; return 1; }
    nome_iface_ok "$AP_NAME" || { echo "ERROR: invalid AP interface name '$AP_NAME'" >&2; return 1; }
    [ "$UP_NAME" != "$AP_NAME" ] || { echo "ERROR: the two interfaces need different names ($UP_NAME)" >&2; return 1; }
    return 0
}

# As portas Ethernet (fisicas: com dispositivo, tipo 1, sem Wi-Fi) e o nome fixo de cada uma. Numa instalacao nova num terminal
# lista-as e pergunta um nome por porta (Enter fica com o actual). O resultado fica em ETH_NOMES, uma linha "actual caminho novo"
# por porta a renomear; o passo 3 escreve os .link (20-bridge-eth-<caminho>.link, os mesmos que o bridge escreve ao renomear
# depois de instalado: atom/app/ethernet.py). Numa actualizacao nada se pergunta: os .link que ja la estao ficam.
eth_path() { udevadm info -q property -p "${SYS_NET_DIR:-/sys/class/net}/$1" 2>/dev/null | sed -n 's/^ID_PATH=//p' | head -n1; }
eth_portas() {
    local d
    for d in "${SYS_NET_DIR:-/sys/class/net}"/*; do
        [ -e "$d/device" ] && [ ! -d "$d/wireless" ] && [ "$(cat "$d/type" 2>/dev/null)" = 1 ] && echo "${d##*/}"
    done
    return 0
}
nomes_ethernet() {
    local nova="${1:-0}" n path v usados lista d estado
    ETH_NOMES=""
    [ "$nova" = 1 ] && [ -t 0 ] || return 0
    lista="$(eth_portas)"
    [ -n "$lista" ] || { echo "no Ethernet port found"; return 0; }
    echo "Ethernet ports:"
    for n in $lista; do
        d="${SYS_NET_DIR:-/sys/class/net}/$n"
        estado=down; [ "$(cat "$d/carrier" 2>/dev/null)" = 1 ] && estado=up
        echo "  $n   $estado   $(basename "$(readlink -f "$d/device/driver" 2>/dev/null)")   $(cat "$d/address" 2>/dev/null)"
    done
    usados=" ${UP_NAME:-} ${AP_NAME:-} $(echo $lista) "
    for n in $lista; do
        path="$(eth_path "$n")"
        if [ -z "$path" ]; then echo "  $n: no hardware path, it keeps its name"; continue; fi
        while :; do
            read -rp "Name for the Ethernet port $n [$n]: " v
            v="${v:-$n}"
            [ "$v" = "$n" ] && break
            if ! nome_iface_ok "$v"; then echo "  1 to 15 characters: letters, digits, _ and -, starting with a letter (not wlanN/ethN)"; continue; fi
            case "$usados" in *" $v "*) echo "  $v is already used by another interface"; continue ;; esac
            ETH_NOMES="${ETH_NOMES}$n $path $v
"
            usados="$usados$v "
            break
        done
    done
    return 0
}
# O .link de uma porta Ethernet: caminho, nome. Escreve so se mudou; devolve 0 se escreveu.
eth_link() {
    local dir="${LINK_DIR:-/etc/systemd/network}" f novo
    f="$dir/20-bridge-eth-$(printf '%s' "$1" | tr -c 'A-Za-z0-9' '-').link"
    novo="$(printf '# Escrito pelo Camera Bridge: nome fixo da porta Ethernet (pelo caminho do hardware).\n[Match]\nPath=%s\nType=ether\n\n[Link]\nName=%s' "$1" "$2")"
    [ "$(cat "$f" 2>/dev/null)" = "$novo" ] && return 1
    mkdir -p "$dir"
    printf '%s\n' "$novo" > "$f"
    echo "written $f ($2)"
    return 0
}

# Os avisos de licenca (de todos os SDK e afins: licencas.py) aceitam-se UMA vez, na primeira instalacao, e depois nada volta a
# perguntar (nem a consola, nem a pagina, nem as actualizacoes). Numa instalacao nova sao obrigatorios: ACCEPT_LICENSES=yes aceita
# sem perguntar; num terminal mostram-se e pede-se "yes"; sem terminal nem ACCEPT_LICENSES a instalacao PARA (return 1). Uma
# actualizacao de um Atom que ja os aceitou nao faz nada; uma sem aceite (de antes disto) pergunta num terminal e, sem ele, avisa
# e segue (as Sony ficam por acrescentar ate se aceitar). Como o PIN, o aceite fica num ficheiro temporario ($TERMOS_NOVO) e o
# passo 7 poe-no em $opt (termos_instalar).
termos_da_instalacao() {
    local opt="${1:-$OPT}" app="${2:-$opt}" nova="${3:-0}" tmp r
    TERMOS_NOVO=""
    [ -s "$opt/termos.json" ] && return 0
    [ -f "$app/licencas.py" ] || return 0
    tmp="$(mktemp -u /tmp/bridge-termos.XXXXXX)"
    if [ "${ACCEPT_LICENSES:-}" = yes ]; then
        BRIDGE_TERMOS_PATH="$tmp" python3 "$app/licencas.py" aceitar >/dev/null && TERMOS_NOVO="$tmp"
        echo "license notices accepted (ACCEPT_LICENSES=yes)"
        return 0
    fi
    if [ -t 0 ]; then
        echo
        python3 "$app/licencas.py" texto
        echo
        while :; do
            read -rp "Type yes to accept these notices (no = stop the installation): " r
            case "$r" in
                yes|YES|Yes) BRIDGE_TERMOS_PATH="$tmp" python3 "$app/licencas.py" aceitar >/dev/null && TERMOS_NOVO="$tmp"; return 0 ;;
                no|NO|No) echo "ERROR: the license notices were not accepted." >&2; return 1 ;;
            esac
        done
    fi
    if [ "$nova" = 1 ]; then
        echo "ERROR: the license notices must be accepted on a new install." >&2
        return 1
    fi
    echo "NOTE: the license notices were never accepted on this Atom: no Sony can be added until they are. Run the installer in a terminal (or with ACCEPT_LICENSES=yes)."
    return 0
}

# Poe o aceite recolhido por termos_da_instalacao no sitio final (depois de o codigo estar em $opt).
termos_instalar() {
    local opt="${1:-$OPT}"
    [ -n "${TERMOS_NOVO:-}" ] && [ -f "$TERMOS_NOVO" ] || return 0
    [ -s "$opt/termos.json" ] || install -m 644 "$TERMOS_NOVO" "$opt/termos.json"
    rm -f "$TERMOS_NOVO"
    TERMOS_NOVO=""
    echo "license notices: acceptance saved in $opt/termos.json"
}

# Poe o PIN recolhido por pin_da_pagina no sitio final (depois de o codigo estar em $opt).
pin_instalar() {
    local opt="${1:-$OPT}"
    [ -n "${PIN_NOVO:-}" ] && [ -f "$PIN_NOVO" ] || return 0
    [ -e "$opt/pin.json" ] || install -m 600 "$PIN_NOVO" "$opt/pin.json"
    rm -f "$PIN_NOVO"
    PIN_NOVO=""
    echo "web PIN saved in $opt/pin.json"
}

gravar_agora() {
    curl -fs -m 5 "http://localhost:$PORT/api/status" 2>/dev/null \
        | python3 -c 'import json,sys; sys.exit(0 if json.load(sys.stdin).get("anything_recording") else 1)' 2>/dev/null
}

# Reiniciar o bridge a meio de uma take corta-a: espera-se pelo STOP (o bridge diz "anything_recording" no status; um
# bridge antigo que nao o diz conta como parado). NO_WAIT_REC=1 reinicia na mesma.
esperar_stop() {
    local oque="${1:-restarting the bridge}"
    if [ -z "${NO_WAIT_REC:-}" ] && gravar_agora; then
        echo "recording now: $oque after the STOP (waiting; NO_WAIT_REC=1 to skip)"
        while gravar_agora; do sleep 5; done
        echo "stopped: $oque"
    fi
    return 0
}

instalar_app() {
    local app="$1" opt="$2" f parcial mx m
    BK=""
    APP_SRC=""
    [ -f "$app/bridge.py" ] || { echo "atom/app has no bridge.py"; return 1; }
    # verificacao: todo o python da app tem de ser valido antes de se tocar em nada (ast: nao escreve ficheiros)
    for f in "$app"/*.py; do
        python3 -c 'import ast,sys; ast.parse(open(sys.argv[1], encoding="utf-8").read())' "$f" 2>/dev/null \
            || { echo "ERROR: $f is not valid python: nothing changed"; return 2; }
    done
    # a copia do que corre agora, para repor se o novo nao arrancar (ficam as 3 ultimas). Faz-se para um nome que NAO e
    # .bak-* e so depois se renomeia: uma copia a meio (disco cheio, corte de energia) nunca pode ser escolhida como "a
    # ultima copia boa" (bridge-repor-codigo.sh).
    rm -rf "$opt".parcial-* 2>/dev/null || true
    if [ -d "$opt" ]; then
        parcial="$opt.parcial-$(date +%Y%m%d-%H%M%S)"
        if ! cp -a "$opt" "$parcial" || ! mv "$parcial" "${parcial/.parcial-/.bak-}"; then
            rm -rf "$parcial" "${parcial/.parcial-/.bak-}"
            echo "ERROR: could not save the copy of $opt (disk full?): nothing changed"
            return 3
        fi
        BK="${parcial/.parcial-/.bak-}"
    fi
    APP_SRC="$app"
    mkdir -p "$opt"
    # --remove-destination: apaga o ficheiro antigo antes de criar o novo. Sem isso o cp escreve POR CIMA e um programa que
    # esta a correr (o sony_ctl, que o bridge mantem a correr) da "Text file busy"; assim o processo antigo continua com a
    # sua versao ate o bridge reiniciar, e o ficheiro novo ja fica no sitio.
    cp -a --remove-destination "$app"/. "$opt"/ || { echo "ERROR: copying the app failed"; return 4; }
    # Poda: a copia nova ($BK) nunca sai; das outras ficam as 2 mais recentes pela data de modificacao (ls -dt). Pelo nome
    # (a data esta nele) um relogio atrasado fazia a copia nova ficar a mais velha e ser apagada. O cp -a guarda a data da
    # origem, por isso marca-se a copia nova com a hora de agora, e nunca antes da mais recente das outras (relogio atrasado):
    # fica sempre a mais recente tambem para o bridge-repor-codigo.sh, que repoe a primeira de ls -dt.
    if [ -n "$BK" ] && [ -d "$BK" ]; then
        touch "$BK" 2>/dev/null || true
        mx=0
        for f in "$opt".bak-*; do
            [ -d "$f" ] || continue
            [ "$f" = "$BK" ] && continue
            m=$(stat -c %Y "$f" 2>/dev/null || echo 0)
            [ "$m" -gt "$mx" ] && mx="$m"
        done
        [ "$(stat -c %Y "$BK" 2>/dev/null || echo 0)" -gt "$mx" ] || touch -d "@$((mx + 1))" "$BK" 2>/dev/null || true
    fi
    ls -dt "$opt".bak-* 2>/dev/null | grep -vxF -- "$BK" | tail -n +3 | xargs -r rm -rf
    return 0
}

reverter_app() {
    # Repoe os ficheiros de CODIGO da copia (os da app, o que o instalador copiou) e tira os que o codigo novo acrescentou;
    # os dados do operador escritos desde a copia (shoot log, events, settings, usb/sftp/c3...) ficam como estao.
    # Antes espera pelo STOP, como o passo 7: nao se corta uma take para repor codigo. Reinicia os servicos.
    local opt="$1" rel d falhas=0
    [ -n "${BK:-}" ] && [ -d "$BK" ] || { echo "no copy to restore"; return 1; }
    [ -n "${APP_SRC:-}" ] && [ -d "$APP_SRC" ] || { echo "no app folder to compare with: cannot restore"; return 1; }
    esperar_stop "restoring the previous code"
    systemctl stop camera-bridge bridge-usb 2>/dev/null || true
    while IFS= read -r -d '' rel; do
        rel="${rel#./}"
        e_dado "$rel" && continue
        if [ -e "$BK/$rel" ]; then
            d="$opt/$(dirname "$rel")"
            mkdir -p "$d" && cp -a "$BK/$rel" "$opt/$rel.rev" && mv -f "$opt/$rel.rev" "$opt/$rel" \
                || { echo "ERROR: could not restore $rel"; rm -f "$opt/$rel.rev"; falhas=1; }
        else
            rm -f "$opt/$rel"                 # acrescentado pelo codigo novo
        fi
    done < <(cd "$APP_SRC" && find . -type f -print0)
    systemctl restart camera-bridge
    for u in bridge-usb bridge-brilho; do       # so os que estao ligados (a consola pode te-los desligado)
        systemctl is-enabled --quiet "$u" 2>/dev/null && systemctl restart "$u" 2>/dev/null
    done
    [ "$falhas" = 0 ]
}
