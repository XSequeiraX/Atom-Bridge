// texto_utf8.h: texto do SDK da Sony em UTF-8, qualquer que seja o tamanho do carácter.
//
// No Windows o SDK compila-se com UNICODE e os textos que devolve (modelo, tipo de ligacao, IP) sao caracteres largos
// (wchar_t, UTF-16): lidos como `char` davam lixo. Aqui converte-se para UTF-8, que e o que o bridge espera. Serve a char (1 byte:
// ja e texto), char16_t / wchar_t de 2 bytes (UTF-16, com pares substitutos) e char32_t / wchar_t de 4 bytes (UTF-32).
// So cabecalhos da biblioteca standard: testa-se no Linux (texto_utf8_teste.cpp).
#pragma once
#include <cstddef>
#include <cstdint>
#include <string>

namespace texto {

inline void acrescentar(std::string& o, uint32_t cp) {
    if (cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) cp = 0xFFFD;       // invalido: o carácter de substituicao
    if (cp < 0x80) { o += (char)cp; }
    else if (cp < 0x800) { o += (char)(0xC0 | (cp >> 6)); o += (char)(0x80 | (cp & 0x3F)); }
    else if (cp < 0x10000) {
        o += (char)(0xE0 | (cp >> 12)); o += (char)(0x80 | ((cp >> 6) & 0x3F)); o += (char)(0x80 | (cp & 0x3F));
    } else {
        o += (char)(0xF0 | (cp >> 18)); o += (char)(0x80 | ((cp >> 12) & 0x3F));
        o += (char)(0x80 | ((cp >> 6) & 0x3F)); o += (char)(0x80 | (cp & 0x3F));
    }
}

// `p` ate ao 0 final, ou so `n` caracteres (n = (size_t)-1: ate ao 0). p nulo da texto vazio.
template <class C>
std::string utf8(const C* p, size_t n = (size_t)-1) {
    std::string o;
    if (!p) return o;
    size_t i = 0;
    auto fim = [&](size_t k) { return n == (size_t)-1 ? p[k] == 0 : k >= n; };
    if (sizeof(C) == 1) {
        while (!fim(i)) { o += (char)p[i]; i++; }
    } else if (sizeof(C) == 2) {
        while (!fim(i)) {
            uint32_t u = (uint16_t)p[i++];
            if (u >= 0xD800 && u <= 0xDBFF && !fim(i) && (uint16_t)p[i] >= 0xDC00 && (uint16_t)p[i] <= 0xDFFF) {
                u = 0x10000 + ((u - 0xD800) << 10) + ((uint16_t)p[i++] - 0xDC00);   // par substituto
            }
            acrescentar(o, u);
        }
    } else {
        while (!fim(i)) acrescentar(o, (uint32_t)p[i++]);
    }
    return o;
}

}  // namespace texto
