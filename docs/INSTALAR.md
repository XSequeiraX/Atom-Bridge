# Instalar o Atom-Bridge

Tutorial passo a passo para pôr o **Atom** a funcionar: o portátil Debian que corre o bridge, a consola e a página web, e serve a rede Wi-Fi das câmaras. No fim tens a consola no ecrã do Atom e a página aberta no telemóvel ou no iPad.

Demora cerca de 20 a 30 minutos, quase todos a descarregar pacotes. O instalador pode correr-se outra vez sem estragar nada: se algo falhar, corrige-se e repete-se.

> O Proton (a aplicação de Windows e Mac para copiar os cartões) tem o seu próprio guia, em [`proton/README.md`](../proton/README.md) e nas notas de cada [release](https://github.com/XSequeiraX/Atom-Bridge/releases).

## O que precisas

| | |
|---|---|
| **O Atom** | O portátil onde o bridge corre. O instalador foi feito e testado num **Toshiba NB500 (Atom) com Debian 13**. Outro PC com Debian 13 deve servir, mas não foi testado. |
| **Quem serve a rede das câmaras** | **Uma de duas:** um dongle USB com chip **Atheros AR9271** (driver `ath9k_htc`), que o Atom usa como ponto de acesso; **ou** um router/AP ligado por **cabo Ethernet** ao Atom (ver [«Com um router por Ethernet»](#com-um-router-por-ethernet-em-vez-do-dongle)). Com o dongle, liga-o **antes de instalar** (passo 4). |
| **Internet durante a instalação** | Cabo Ethernet, ou o Wi-Fi interno do portátil. Os pacotes descarregam-se do Debian e do GitHub. Depois de instalado, o Atom trabalha sem internet. |
| **O nome da rede das câmaras** (e a palavra-passe, só com dongle) | Se as câmaras já têm uma rede guardada, usa **exatamente o mesmo nome**. Num rig novo, escolhe agora (nome até 32 caracteres, palavra-passe de 8 ou mais) e põe depois essa rede nas câmaras. Com um router por Ethernet é o nome do Wi-Fi que o router emite. |
| **Um PIN para a página web** | 4 a 12 dígitos. É o que os telemóveis e iPads na rede das câmaras usam para abrir a página. |

## 1. Instalar o Debian 13

Instala o Debian 13 no Atom, de preferência **sem ambiente gráfico** (o Atom usa só a consola de texto). Na escolha de programas, deixa só **servidor SSH** e **utilitários standard do sistema**.

O instalador do Atom precisa de `sudo` e de `git`. Se o teu Debian ainda não os tem, no Atom:

```bash
su -
apt update && apt install -y sudo git
usermod -aG sudo O-TEU-UTILIZADOR      # o utilizador com que vais instalar
exit
```

Sai e volta a entrar com esse utilizador para o `sudo` valer. Daqui em diante podes fazer tudo no próprio Atom ou por SSH (`ssh utilizador@endereço-do-atom`).

## 2. Ligar o Atom à internet

O cabo Ethernet é o mais simples. Confirma:

```bash
ping -c 2 github.com
```

Se responder, segue. Com Wi-Fi: `sudo nmtui` > *Activate a connection*.

## 3. Descarregar o Atom-Bridge

O repositório é público e não pede login. **Não apagues esta pasta depois**: é dela que o Atom se atualiza.

```bash
cd ~
git clone https://github.com/XSequeiraX/Atom-Bridge
cd Atom-Bridge
```

## 4. Ligar o dongle do AP (só se usas o dongle)

Liga o dongle AR9271 numa porta USB do Atom **antes de instalar**. Sem ele o instalador acaba na mesma, mas avisa e **não cria a rede das câmaras** (resolve-se ligando o dongle e voltando a correr o instalador, passo 5).

**Se vais usar um router por Ethernet, salta este passo.** Não precisas de dongle.

## 5. Correr o instalador

```bash
sudo bash atom-install/install_atom.sh
```

O instalador faz perguntas pela ordem seguinte. Lê-as com atenção:

| Pergunta | O que responder |
|---|---|
| **Avisos de licença** (SDK da Sony, da Canon e outros) | Lê e escreve `yes`. É obrigatório e pergunta-se **uma só vez**: depois nada volta a perguntar. |
| **PIN da página web** | 4 a 12 dígitos. Guarda-o: é o que os telemóveis e iPads usam. |
| **Nome fixo de cada interface Wi-Fi** | **Enter** deixa o nome que o sistema lhe dá (recomendado). Só pergunta pelas interfaces que existem: sem dongle não pergunta pelo dongle. |
| **Nome de cada porta Ethernet** | **Enter** deixa como está. Dá nomes só se tiveres mais de uma e quiseres distingui-las. |
| **Nome (SSID) do AP das câmaras** | O nome da rede das câmaras (até 32 caracteres). Pergunta-se sempre: com um router por Ethernet, é o nome do Wi-Fi do router. |
| **Palavra-passe do AP** | **Só se o dongle estiver ligado**, para criar a rede dele (8 ou mais caracteres; não aparece enquanto escreves). Sem dongle não pergunta. |

Depois instala os pacotes e o código (sete passos, numerados no ecrã) e no fim mostra o resumo. Se o bridge novo não arrancar, o próprio instalador **repõe o código anterior**.

### Sem perguntas (para scripts)

Todas as respostas podem vir do ambiente:

```bash
sudo ACCEPT_LICENSES=yes WEB_PIN=123456 AP_SSID='NomeDaRede' AP_PSK='palavra-passe' \
     bash atom-install/install_atom.sh
```

## 6. Reiniciar

Numa instalação nova os nomes das interfaces só valem depois de reiniciar (o instalador avisa):

```bash
sudo reboot
```

## 7. Primeiro arranque

No ecrã do Atom aparece um **menu de arranque** (consola do bridge, Wi-Fi, definições de rede, linha de comandos). Sem tocar em nada, ao fim de 10 segundos abre a **consola do bridge**: um ecrã de carregamento e depois o dashboard.

Para a configuração inicial: carrega em **Esc** e escolhe **WIZARD**. O assistente percorre, por ordem: o Atom, o Wi-Fi, o relógio, o timecode (TC-1 e LTC), as câmaras, o F6, o DJI Mic 3, as GoPro, as Sony, os relógios das câmaras, as definições da BMPCC e da Canon, os alvos de REC, o shoot log e uma **take de teste**. Cada passo pode saltar-se e retomar-se mais tarde.

### Abrir a página web

| De onde | Endereço |
|---|---|
| No próprio Atom | `http://localhost:8080` |
| Telemóvel ou iPad ligado à rede das câmaras | `http://192.168.4.1:8080`, e o **PIN** |
| De fora, por [Tailscale](https://tailscale.com/download/linux) (opcional, instala-se à parte) | `http://<nome ou IP do Atom na Tailscale>:8080`, **sem PIN** |

No iPhone e no iPad, *Partilhar > Adicionar ao ecrã principal* deixa a página como uma aplicação.

## Com um router por Ethernet, em vez do dongle

O Atom pode servir as câmaras por um router (ou AirPort Express, ou outro AP) ligado por cabo, sem dongle. Faz-se **depois de instalar**, na consola:

1. **No router:** põe-no em **modo bridge / ponto de acesso, com o DHCP desligado**. Quem dá os endereços é o Atom (`192.168.4.1`, rede `192.168.4.0/24`), e dois DHCP na mesma rede estragam tudo. Emite o Wi-Fi com **o mesmo nome e a mesma palavra-passe** que as câmaras têm guardados. Liga o cabo de uma porta **LAN** do router (não a WAN) à porta Ethernet do Atom.
2. **No instalador:** responde ao nome da rede como acima; sem dongle, não pede a palavra-passe e avisa «no AR9271 dongle plugged in», o que é normal.
3. **Na consola:** *Esc > DEVICES > WIRELESS >* **Settings** da rede das câmaras *> Cameras' AP: dongle (switch to external, Ethernet)*. Passa para o modo externo (`EXT AP` no dashboard). Só aparece se o Atom tiver uma porta Ethernet.

A internet do Atom continua a vir do Wi-Fi interno ou de outro cabo; a rede das câmaras não partilha internet. Podes voltar ao dongle no mesmo menu.

## 8. Ligar as câmaras

Uma instalação nova **não traz nenhum aparelho nem gateway configurado**: tudo se acrescenta tu, em *Esc > DEVICES* na consola (ou nos botões **+ ADD** da página). Os gateways e as câmaras que se anunciam na rede aparecem sugeridos.

- **Canon:** o CCAPI vem desligado de fábrica. Ativa-o **uma vez em cada câmara** com a *CCAPI Activation Tool* da Canon, por USB, num Windows ou Mac. Depois a câmara liga-se ao Atom por Wi-Fi.
- **Sony, telemóveis, GoPro, BMPCC, F6:** cada uma tem o seu passo no assistente (e a lista de aparelhos suportados está no [README](../README.md#aparelhos-suportados)).
- A rede das câmaras é a do passo 5: as câmaras têm de a ter guardada, com o mesmo nome e a mesma palavra-passe.
- Os **gateways** (C3 e S3) configuram-se sempre pelo Atom, por USB: consola, *Devices > Set up / update a gateway by USB*.

## Atualizar

Na consola: **Esc > GLOBAL SETTINGS > Update from GitHub**. Ou por linha de comandos, na pasta do clone:

```bash
bash atualizar_atom.sh
```

O Atom atualiza-se do repositório público, sem login. Se uma gravação estiver a decorrer, **espera pelo STOP** antes de reiniciar o bridge. Reinicia o bridge e desliga o F6 (volta a ligá-lo com *FRC-8 connect*). A configuração do rig e o shoot log **nunca são tocados**. Para seguir o registo: `journalctl -u bridge-atualizar -f`.

O kernel **não** é atualizado automaticamente (o instalador fixa-o de propósito: uma versão do kernel deixou o dongle do AP sem subir a interface). Os restantes pacotes atualizam-se pelo terminal, quando quiseres.

## Se algo correr mal

| Sintoma | O que fazer |
|---|---|
| O instalador diz que falta o nome do AP, a palavra-passe ou o PIN | Correu sem terminal. Corre-o num terminal, ou com `ACCEPT_LICENSES`, `WEB_PIN`, `AP_SSID` e `AP_PSK` (ver acima). |
| Aviso *no AR9271 dongle plugged in* | Liga o dongle e corre `sudo bash atom-install/install_atom.sh` outra vez. |
| A rede das câmaras não aparece, depois de instalar | Reinicia o Atom (os nomes das interfaces só valem depois). Se persistir: `nmcli connection up NomeDaRede` (o nome que deste ao AP), e na consola *Esc > DEVICES > WIRELESS* mostra o estado. |
| *The bridge is not answering yet* no fim | `journalctl -u camera-bridge -n 40`. O instalador já tentou repor o código anterior; o resultado vem no resumo. |
| Telemóvel ou iPad não abre a página | Está na rede das câmaras? Usa `http://192.168.4.1:8080` e o PIN. Para o mudar: consola, *GLOBAL SETTINGS > Web PIN*. |
| Faltou um pacote | `sudo FORCE_PKGS=1 bash atom-install/install_atom.sh` volta a rever todos. |
| Sem internet durante a instalação | O bridge instala-se na mesma; só o MicroPython do C3 (para gravar um C3 em branco) fica por descarregar, e o instalador avisa. |

## Para saber mais

- O que cada ficheiro faz: [`atom-install/LEIA-ME.txt`](../atom-install/LEIA-ME.txt) e [`atom/LEIA-ME.txt`](../atom/LEIA-ME.txt).
- A API do bridge: [`docs/API.md`](API.md).
- Firmware dos gateways, GoPro, F6 e modo de treino: [README](../README.md).
