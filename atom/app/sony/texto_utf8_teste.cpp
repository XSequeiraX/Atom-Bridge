// Teste do texto_utf8.h (corre no Linux, no ci/verificar.py): g++ -std=c++17 texto_utf8_teste.cpp && ./a.out
#include <cstdio>
#include <cstring>
#include "texto_utf8.h"

static int erros = 0;
static void igual(const char* nome, const std::string& obtido, const std::string& quer) {
    if (obtido != quer) { printf("ERRO %s: obtido '%s', devia ser '%s'\n", nome, obtido.c_str(), quer.c_str()); erros++; }
}

int main() {
    igual("char", texto::utf8("ILCE-7M4"), "ILCE-7M4");
    igual("nulo", texto::utf8((const char16_t*)nullptr), "");
    igual("utf16 ascii", texto::utf8(u"ILCE-7M4"), "ILCE-7M4");
    igual("utf16 acentos", texto::utf8(u"Câmara Sony"), "C\xC3\xA2mara Sony");
    igual("utf16 euro", texto::utf8(u"5€"), "5\xE2\x82\xAC");
    igual("utf16 par substituto", texto::utf8(u"\U0001F3A5"), "\xF0\x9F\x8E\xA5");
    igual("utf16 substituto solto", texto::utf8(u"a\xD800" "b"), "a\xEF\xBF\xBD" "b");
    igual("utf16 com tamanho", texto::utf8(u"192.168.4.80XXXX", 12), "192.168.4.80");
    igual("utf16 vazio", texto::utf8(u""), "");
    igual("utf32 ascii", texto::utf8(U"A7 IV"), "A7 IV");
    igual("utf32 emoji", texto::utf8(U"\U0001F3A5"), "\xF0\x9F\x8E\xA5");
    igual("utf32 invalido", texto::utf8(U"\x110000"), "\xEF\xBF\xBD");
    igual("wchar_t", texto::utf8(L"USB"), "USB");
    // Um IP so tem ASCII: nao muda nada
    const char16_t ip[] = {'1', '9', '2', '.', '1', '6', '8', '.', '4', '.', '8', '0', 0};
    igual("ip", texto::utf8(ip, 12), "192.168.4.80");
    if (!erros) printf("ok\n");
    return erros ? 1 : 0;
}
