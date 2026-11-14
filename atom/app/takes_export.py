"""O shoot log agrupado por take e por camara, para o Proton (o companion do Windows) e para quem quiser cruzar as takes com o
material copiado. Formato `atom-takes/1`; ver proton/CONTRATO.md.

O shoot log tem uma linha por evento (start, stop, nota, marca...). Aqui juntam-se por take: uma take e um REC do grupo, e cada
camara que gravou nela traz o inicio, o fim, o timecode e o nome do ficheiro (quando se sabe). So le linhas ja escritas
(dicionarios com as colunas do shoot log): nao mexe no ficheiro nem no estado do bridge.
"""
import csv
import io
import re
import time
from datetime import datetime

FORMATO = "atom-takes/1"
_FMT = "%Y-%m-%d %H:%M:%S"
_TC = re.compile(r"(?<!\d)(\d{2}:\d{2}:\d{2}[:;]\d{2})(?!\d)")
GAP_S = 6 * 3600                      # a mesma numeracao 6 h depois ja e outra take (a numeracao recomeca noutro dia)
NAO_CAMARAS = ("MARK", "SLATE", "SYSTEM")
DEFINICOES = ("av", "tv", "iso", "shutter_mode", "movie_quality")
# fps no fim (repetido em cada linha): sem ele quem le o CSV nao converte os timecodes em segundos a 24/30 fps.
CSV_COLUNAS = ["day", "session", "take", "camera", "alias", "start", "stop", "tc_start", "tc_stop", "clip", "fault", "verdict",
               "notes", "fps"]


def _tc(texto):
    m = _TC.search(texto or "")
    return m.group(1).replace(";", ":") if m else None


def _hora(txt):
    try:
        return datetime.strptime(str(txt or "").strip(), _FMT)
    except ValueError:
        return None


def _grupos(linhas):
    """[[linhas de uma take]] pela ordem em que aparecem. Mesma (sessao, numero) com mais de GAP_S de intervalo = outra take."""
    abertos, saida = {}, []
    for r in linhas:
        take = str(r.get("take") or "").strip()
        ts = _hora(r.get("timestamp"))
        if not take.isdigit() or ts is None:
            continue
        chave = (str(r.get("name") or ""), int(take))
        g = abertos.get(chave)
        if g is None or (ts - g["fim"]).total_seconds() > GAP_S:
            g = {"chave": chave, "linhas": [], "fim": ts}
            abertos[chave] = g
            saida.append(g)
        g["linhas"].append((ts, r))
        g["fim"] = ts
    return saida


def _take(g):
    sessao, numero = g["chave"]
    cams, notas, verdict = {}, [], None
    for ts, r in g["linhas"]:
        fonte, acao, nota = str(r.get("source") or ""), str(r.get("action") or ""), str(r.get("note") or "")
        if fonte not in NAO_CAMARAS and acao in ("start", "stop", "fault"):
            c = cams.setdefault(fonte, {"source": fonte, "alias": str(r.get("alias") or ""), "start": None, "stop": None,
                                        "tc_start": None, "tc_stop": None, "clip": "", "fault": False, "settings": {}})
            if r.get("alias"):
                c["alias"] = str(r["alias"])
            if acao == "start" and c["start"] is None:
                c["start"], c["tc_start"] = ts.strftime(_FMT), _tc(nota)
                c["settings"] = {k: str(r.get(k) or "") for k in DEFINICOES if r.get(k)}
            elif acao == "stop":
                c["stop"], c["tc_stop"] = ts.strftime(_FMT), _tc(nota)
                if r.get("clip"):
                    c["clip"] = str(r["clip"])
            elif acao == "fault":
                c["fault"] = True
                if c["stop"] is None:
                    c["stop"] = ts.strftime(_FMT)
        elif acao in ("good", "no-good"):
            verdict = "good" if acao == "good" else "no-good"
            notas.append({"at": ts.strftime(_FMT), "kind": acao, "tc": _tc(nota), "text": nota})
        elif acao in ("note", "mark", "slate"):
            notas.append({"at": ts.strftime(_FMT), "kind": acao, "tc": _tc(nota), "text": nota})
    lista = sorted(cams.values(), key=lambda c: (c["start"] or "", c["source"]))
    inicios = [c["start"] for c in lista if c["start"]]
    fins = [c["stop"] for c in lista if c["stop"]]
    aberta = any(c["start"] and not c["stop"] for c in lista)
    inicio = min(inicios) if inicios else g["linhas"][0][0].strftime(_FMT)
    primeiro = next((c for c in lista if c["start"] == inicio), None)
    return {"day": inicio[:10], "session": sessao, "take": numero, "start": inicio,
            "end": None if aberta or not fins else max(fins), "open": aberta, "verdict": verdict,
            "tc_start": primeiro["tc_start"] if primeiro else None, "cameras": lista, "notes": notas}


def exportar(linhas, day=None, atom="", fps=25):
    """O dicionario `atom-takes/1`. `linhas`: as linhas do shoot log (dicionarios). `day` (AAAA-MM-DD): so as takes desse dia.
    `fps`: as imagens por segundo do timecode do Atom (para o Proton converter os timecodes em segundos)."""
    takes = [_take(g) for g in _grupos(linhas)]
    takes = [t for t in takes if t["cameras"] and (not day or t["day"] == day)]
    takes.sort(key=lambda t: (t["start"], t["take"]))
    return {"format": FORMATO, "exported": time.strftime(_FMT), "atom": atom, "timezone": "local time of the Atom",
            "fps": fps, "takes": takes}


def para_csv(exportado):
    """Uma linha por take e por camara (as colunas CSV_COLUNAS)."""
    fps = exportado.get("fps")
    saida = io.StringIO()
    w = csv.writer(saida, lineterminator="\n")
    w.writerow(CSV_COLUNAS)
    for t in exportado["takes"]:
        notas = " / ".join(n["text"] for n in t["notes"] if n.get("text"))
        for c in t["cameras"]:
            w.writerow([t["day"], t["session"], t["take"], c["source"], c["alias"], c["start"] or "", c["stop"] or "",
                        c["tc_start"] or "", c["tc_stop"] or "", c["clip"], "1" if c["fault"] else "", t["verdict"] or "", notas,
                        "" if fps is None else ("%g" % fps)])
    return saida.getvalue()
