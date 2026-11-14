# O que o Atom exporta para o Proton (`atom-takes/1`)

O Atom agrupa o shoot log por take e por câmara. Duas rotas, o mesmo conteúdo (`?day=AAAA-MM-DD` limita a um dia):

- `GET /api/log/takes` → JSON
- `GET /api/log/takes.csv` → CSV (uma linha por take e câmara)

Pela rede (Tailscale não pede PIN) ou como ficheiro exportado para uma pen (o Proton lê os dois). As horas são **as do relógio do
Atom, sem fuso** (`AAAA-MM-DD HH:MM:SS`); o timecode é `HH:MM:SS:FF` ao `fps` do export.

## JSON

```json
{
  "format": "atom-takes/1",
  "exported": "2026-11-09 18:00:00",
  "atom": "atom",
  "timezone": "local time of the Atom",
  "fps": 25,
  "takes": [
    {
      "day": "2026-11-09", "session": "Dia 1", "take": 1,
      "start": "2026-11-09 10:00:00", "end": "2026-11-09 10:01:02", "open": false,
      "verdict": "good", "tc_start": "10:00:00:00",
      "cameras": [
        { "source": "R5", "alias": "B", "start": "...", "stop": "...", "tc_start": "10:00:01:00", "tc_stop": "10:01:01:00",
          "clip": "MVI_0001.MP4", "fault": false, "settings": { "av": "4.0" } }
      ],
      "notes": [ { "at": "...", "kind": "good|no-good|note|mark|slate", "tc": "10:01:05:00", "text": "TC 10:01:05:00 | boa" } ]
    }
  ]
}
```

- `source` é o nome da câmara no Atom (BMPCC, F6, R5...); `alias` o nome curto que o operador lhe deu (pode vir vazio).
- `clip` só vem quando a câmara diz o nome do ficheiro (hoje os telemóveis); senão fica vazio e o Proton cruza pelo timecode e pela hora.
- `open: true` e `end: null`: a take ainda não acabou (uma câmara não parou).
- `verdict`: `good`, `no-good` ou `null`.

## CSV

`day,session,take,camera,alias,start,stop,tc_start,tc_stop,clip,fault,verdict,notes,fps`

O `fps` (o mesmo em todas as linhas) é o do timecode do Atom. Um CSV sem essa coluna (de antes dela) lê-se a 25 fps.

## Mudar o formato

O `format` leva a versão. Uma mudança que parte o Proton passa a `atom-takes/2`. O Atom e o Proton partilham um ficheiro de exemplo
(`tests/Proton.Tests/Fixtures/`) e o CI dos dois lados verifica que continuam a bater.
