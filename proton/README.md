# Proton

O companion do Atom para Windows e Mac: **copia os cartões** das câmaras (vários ao mesmo tempo, com verificação), **cruza os
ficheiros com as takes** que o Atom registou e prepara o material para a montagem (DaVinci Resolve).

C#/.NET 8, num só programa autónomo (não precisa de instalar o .NET). Windows 11 (x64) e macOS (Apple Silicon e Intel).
Funciona **sem rede**: as takes vêm do Atom pela rede (Tailscale) **ou** de um ficheiro exportado para uma pen exFAT.

## Descarregar e instalar

Em **[Releases](https://github.com/XSequeiraX/Atom-Bridge/releases)** (a mais recente: versão 0.0.1): `Proton-<versão>-windows-x64.zip`,
`Proton-<versão>-mac-arm64.zip` (Apple Silicon) ou `Proton-<versão>-mac-x64.zip` (Intel). Não há instalador: descompacta e abre. As
notas de cada versão (os avisos do Windows e do Mac na primeira vez, o que está testado e o que não) estão em `releases/<versão>.md`.

**Fazer uma release:** com o main em dia, `PUBLICO_TAG=proton-vX.Y.Z bash ci/publicar.sh --push` (escreve antes `proton/releases/X.Y.Z.md`).
A tag no repositório público arranca o fluxo `proton-release.yml`: testa nos três sistemas, compila, assina no Mac (assinatura local) e
publica os zips e o `SHA256SUMS.txt`. Sem enviar a tag (alguns ambientes não deixam): no GitHub, **Actions > Proton release > Run
workflow**, no `main`, com a versão (`X.Y.Z`) e **publish** ligado, que cria a tag e a release.

## Estado

| Parte | Estado |
|---|---|
| Núcleo: leitura de WAV/BWF (F6, DJI Mic 3) e MOV/MP4 (timecode, duração, hora), cruzar com as takes (nome → timecode → hora), caminhos por take, CSV do Resolve, relatório do que falta, cópia verificada | feito, com testes nos três sistemas |
| Janela (Avalonia: a mesma no Windows e no Mac) | feita; testada em modo sem ecrã nos três sistemas, **ainda não vista a correr num Windows nem num Mac** |
| Linha de comandos `proton-cli` (`match`, `copy`) | feita |
| Takes do Atom por Tailscale ou por pen exFAT | feito (botão «Takes to USB» no Atom, «Find on the USB stick» no Proton) |
| BRAW: timecode | por validar com ficheiros reais (hoje cruza por nome e por hora do ficheiro) |
| Nome do ficheiro no STOP (Canon, Sony) | por fazer no Atom |
| Metadados do ficheiro para confirmar de que câmara é (dupla verificação) | por fazer |
| Assinatura/notarização no Mac e no Windows | por fazer: o Mac leva só uma assinatura local (sem conta Apple) e pede «abrir mesmo assim»; o Windows mostra o SmartScreen |

## A janela

Quatro passos, com o resultado sempre à vista:

1. **Takes**: o endereço do Atom (`http://atom:8080`, pelo Tailscale), ou **Open file...** (o `.json`/`.csv` exportado), ou
   **Find on the USB stick** (procura `AtomBridge/atom-takes-*.json` nos discos ligados).
2. **Cards**: **Add card...** para cada cartão ou pasta e escolher **de que câmara é** (as câmaras vêm das takes).
3. **Destination**: a pasta das cópias, o modelo das pastas e quantas cópias ao mesmo tempo.
4. **Result**: **Match** mostra, sem copiar, o que casa com quê (`OK`, `CHECK` duvidoso, `MISSING` take sem ficheiro, `NO TAKE`
   ficheiro sem take). **Copy** copia, verifica, organiza por take e escreve `resolve.csv`, `report.txt`, `review.csv` e
   `proton_manifest.csv`.

**A verificação lê a cópia do disco, não da memória.** Cada ficheiro é copiado para um temporário, relido e comparado (xxHash3) com o
que se leu do cartão; só então ganha o nome final. Relido normalmente, o sistema serviria os bytes da cache (a memória) e a
verificação não apanhava um disco de destino que grava mal. Por isso a releitura vai ao disco: no Windows sem cache
(`FILE_FLAG_NO_BUFFERING`), no Mac com `F_NOCACHE` e `F_FULLFSYNC`. Se um destino não deixar (algumas unidades de rede), copia na
mesma, verifica pela memória e diz-o: na janela, na linha de comandos e na coluna `verified` do manifest (`disk` ou `memory`).

**Review shoot log and matches...** (depois do Match) abre o shoot log take a take com a prova de cada ficheiro: timecode do
Atom e do ficheiro e a diferença (em segundos e imagens), a duração da take e do ficheiro, o dia do ficheiro e de onde veio
(user bits, metadados do ficheiro ou data de modificação), como casou (nome, timecode ou só pela hora) e, quando há dúvida,
porquê. Passa a `CHECK` uma diferença de timecode acima de 1 s, uma duração que difere mais de 2 s, um ficheiro de outro dia,
nomes ou timecodes repetidos e uma falha registada no Atom. **Show only what needs checking** esconde o que está certo e
**Save as CSV...** guarda a revisão.

As definições (endereço, destino, modelo) ficam guardadas de um dia para o outro.

## Linha de comandos

```
proton-cli match --takes http://atom:8080 --media /Volumes/CARD_A=BMPCC --media /Volumes/CARD_B=R5 --out saida
proton-cli copy  --takes atom_takes.csv --media D:\=BMPCC --media E:\=R5 --to F:\Dia1 --parallel 4
```

- `--takes`: o endereço do Atom (`http://atom:8080`, pelo Tailscale) ou um `.json` / `.csv` exportado.
- `--media pasta=CAMERA`: a pasta ou o cartão e **de que câmara é** (o nome da câmara no Atom, ou o alias).
- `copy` organiza por `--template` (por omissão `{day}/{session}/T{take:00}/{camera}/{file}`) e põe o que não reconhece em `_unmatched/`.
- Saída: `resolve.csv` (Media Pool → *Import Metadata*), `report.txt` (takes sem ficheiro, ficheiros sem take, dúvidas) e
  `proton_manifest.csv` (o hash de cada cópia).
- Código de saída: 0 tudo certo, 3 há takes ou ficheiros por resolver, 4 falhou alguma cópia, 1 erro.

## Como cruza

Para cada câmara de cada take, por esta ordem: **o nome do ficheiro** que o Atom anotou → **o timecode** de início da take dentro do ficheiro
(serve para áudio contínuo: o DJI Mic dá o ponto onde a take começa dentro do ficheiro) → **a hora** do ficheiro (a mais fraca, marcada
como duvidosa). Um ficheiro de uma take não é reutilizado noutra.

O timecode só diz a hora do dia, por isso o Proton confere também **o dia**: um cartão por formatar pode ter um clip de ontem com o
mesmo timecode de uma take de hoje. A data vem, por esta ordem, dos **user bits** do timecode (SMPTE 309M, lidos como o Atom os lê;
nos WAV com iXML `<UBITS>`, como o F6), do que o ficheiro diz (relógio da câmara) ou da data de modificação. Os MOV/MP4 não guardam
user bits na pista de timecode; o BRAW e o DJI Mic 3 ficam por confirmar com ficheiros reais. Os oito dígitos dos user bits podem vir
pela ordem do LTC ou pela do ecrã: lêem-se as duas, e quando ambas dão datas diferentes desempata a data do ficheiro.

## Construir

```
cd proton
dotnet test Proton.sln
dotnet publish src/Proton.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true     # ou osx-arm64 / osx-x64
packaging/make-mac-app.sh <pasta do publish> <saida>                                              # o Proton.app do Mac
```

O GitHub (`.github/workflows/proton.yml`) testa em Ubuntu, Windows e macOS e gera os programas (artefacto `proton`):
`Proton-windows-x64/Proton.exe`, `Proton-mac-osx-arm64.zip` e `Proton-mac-osx-x64.zip` (o `Proton.app`), e a linha de comandos em `proton-cli-*`.

## Mac

Sem assinatura, o Mac bloqueia o programa descarregado: abre o `Proton.app` com botão direito → Abrir, ou
`xattr -dr com.apple.quarantine Proton.app`. Na primeira vez pede acesso aos discos externos (os cartões). Cartões exFAT leem-se nativamente; o Proton ignora os ficheiros `._*` que o Mac deixa neles.
