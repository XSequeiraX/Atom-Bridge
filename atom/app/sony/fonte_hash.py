#!/usr/bin/env python3
"""O hash do sony_ctl.cpp para o sony_ctl.fonte: python3 fonte_hash.py [linux|windows] [ficheiro]

O sony_ctl.cpp serve ao Linux e ao Windows. O que e so do Windows fica entre `#ifdef _WIN32` ... (`#else`) ... `#endif`
(ou `#ifndef _WIN32`). O sony_ctl de Linux guardado no repositorio foi compilado sem esses blocos, por isso o hash que o
CI compara com o sony_ctl.fonte e o da *vista Linux* do codigo: o ficheiro sem os blocos do Windows (e sem as linhas de
`#ifdef`/`#else`/`#endif` desses blocos). Acrescentar codigo do Windows nao desactualiza o binario de Linux; mudar o resto
desactualiza-o, e o CI avisa. A vista Windows e o ficheiro inteiro.
"""
import hashlib
import os
import re
import sys

_ABRE = re.compile(r"^\s*#\s*(ifdef|ifndef)\s+_WIN32\s*$")
_SE = re.compile(r"^\s*#\s*(if|ifdef|ifndef)\b")
_SENAO = re.compile(r"^\s*#\s*else\b")
_FIM = re.compile(r"^\s*#\s*endif\b")


def vista_linux(texto):
    """O texto do ficheiro como o Linux o compila: os blocos do _WIN32 sem o seu codigo e sem as suas diretivas."""
    saida, em_bloco, windows_ate_else, fundo = [], False, False, 0
    for linha in texto.splitlines(keepends=True):
        if not em_bloco:
            m = _ABRE.match(linha)
            if m:
                em_bloco, fundo = True, 0
                # #ifdef _WIN32: o codigo antes do #else e do Windows; #ifndef _WIN32: o codigo antes do #else e do Linux
                windows_ate_else = m.group(1) == "ifdef"
                antes_do_else = True
                continue
            saida.append(linha)
            continue
        if _SE.match(linha):
            raise ValueError("#if dentro de um bloco _WIN32 nao e suportado: %r" % linha.strip())
        if _SENAO.match(linha):
            antes_do_else = False
            continue
        if _FIM.match(linha):
            em_bloco = False
            continue
        e_windows = windows_ate_else == antes_do_else
        if not e_windows:
            saida.append(linha)
    if em_bloco:
        raise ValueError("#ifdef _WIN32 sem #endif")
    return "".join(saida)


def hash_de(texto, sistema):
    if sistema == "linux":
        texto = vista_linux(texto)
    return hashlib.sha256(texto.encode("utf-8")).hexdigest()


if __name__ == "__main__":
    sistema = sys.argv[1] if len(sys.argv) > 1 else "linux"
    caminho = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "sony_ctl.cpp")
    with open(caminho, encoding="utf-8", newline="") as f:
        print(hash_de(f.read(), sistema))
