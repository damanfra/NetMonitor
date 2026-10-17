"""Adiciona/atualiza chaves em Localization/Strings.resx mantendo a ordem alfabética.
Uso: python3 tools/resx_set.py Chave "Texto" [Chave2 "Texto2" ...]
"""
import re, sys
from pathlib import Path

RESX = Path(__file__).resolve().parent.parent / "src/NetMonitor/Localization/Strings.resx"


def esc(t):
    return t.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def main(args):
    text = RESX.read_text(encoding="utf-8")
    entries = dict(re.findall(r'<data name="([^"]+)" xml:space="preserve">\s*<value>(.*?)</value>\s*</data>', text, re.S))
    for key, value in zip(args[::2], args[1::2]):
        entries[key] = esc(value.replace("\\n", "\n"))
    head = text[: text.index("  <data ")] if "  <data " in text else text[: text.index("</root>")]
    body = "\n".join(f'  <data name="{k}" xml:space="preserve">\n    <value>{v}</value>\n  </data>' for k, v in sorted(entries.items()))
    RESX.write_text(head + body + "\n</root>\n", encoding="utf-8")


if __name__ == "__main__":
    main(sys.argv[1:])
