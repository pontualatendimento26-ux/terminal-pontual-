#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Jumento Vinicius - ponte entre o Claude Code e o Trello.

Guarda a chave/token em %APPDATA%\\Pontual\\trello.json e fala com a API do
Trello usando somente a biblioteca padrao do Python (sem pip install).

Uso rapido:
    python tools/jumento/trello.py configurar --chave KEY --token TOKEN
    python tools/jumento/trello.py quadros
    python tools/jumento/trello.py padrao --quadro "Pontual" --lista "A fazer"
    python tools/jumento/trello.py card --titulo "Arrumar o botao Atualizar"
    python tools/jumento/trello.py lote --arquivo cards.json
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import unicodedata
import urllib.error
import urllib.parse
import urllib.request

API = "https://api.trello.com/1"
ID_RE = re.compile(r"^[0-9a-fA-F]{24}$")


# ---------------------------------------------------------------- utilidades

def die(msg: str) -> "None":
    print("ERRO: " + msg, file=sys.stderr)
    raise SystemExit(1)


def norm(txt: str) -> str:
    """minusculo, sem acento e sem espaco sobrando - para comparar nomes."""
    txt = unicodedata.normalize("NFKD", str(txt or ""))
    txt = "".join(c for c in txt if not unicodedata.combining(c))
    return " ".join(txt.lower().split())


def config_path() -> str:
    base = os.environ.get("APPDATA") or os.path.expanduser("~")
    return os.path.join(base, "Pontual", "trello.json")


def load_config() -> dict:
    caminho = config_path()
    if not os.path.isfile(caminho):
        return {}
    try:
        with open(caminho, "r", encoding="utf-8") as f:
            return json.load(f) or {}
    except (OSError, ValueError) as e:
        die("nao consegui ler %s (%s)" % (caminho, e))
        return {}


def save_config(cfg: dict) -> None:
    caminho = config_path()
    os.makedirs(os.path.dirname(caminho), exist_ok=True)
    with open(caminho, "w", encoding="utf-8") as f:
        json.dump(cfg, f, ensure_ascii=False, indent=2)


def creds(cfg: dict):
    chave = cfg.get("key") or os.environ.get("TRELLO_KEY")
    token = cfg.get("token") or os.environ.get("TRELLO_TOKEN")
    if not chave or not token:
        die("falta a chave do Trello. Rode: python tools/jumento/trello.py "
            "configurar --chave <KEY> --token <TOKEN>  "
            "(pegue os dois em https://trello.com/power-ups/admin)")
    return chave, token


def call(metodo: str, caminho: str, cfg: dict, params: dict | None = None):
    chave, token = creds(cfg)
    consulta = {"key": chave, "token": token}
    for k, v in (params or {}).items():
        if v is None or v == "" or v == []:
            continue
        if isinstance(v, bool):
            v = "true" if v else "false"
        if isinstance(v, (list, tuple)):
            v = ",".join(str(x) for x in v)
        consulta[k] = v
    url = "%s%s?%s" % (API, caminho, urllib.parse.urlencode(consulta))
    req = urllib.request.Request(url, method=metodo)
    req.add_header("Accept", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=40) as resp:
            corpo = resp.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        detalhe = e.read().decode("utf-8", "replace").strip()[:400]
        if e.code in (401, 403):
            die("o Trello recusou a chave/token (%s). %s" % (e.code, detalhe))
        if e.code == 404:
            die("o Trello nao achou %s (404). %s" % (caminho, detalhe))
        die("erro HTTP %s em %s %s: %s" % (e.code, metodo, caminho, detalhe))
    except urllib.error.URLError as e:
        die("nao consegui falar com o Trello: %s" % (e.reason,))
    if not corpo.strip():
        return {}
    try:
        return json.loads(corpo)
    except ValueError:
        die("resposta inesperada do Trello: %s" % corpo[:200])


def escolher(itens: list, ref: str, oque: str):
    """acha um item por id, por nome igual ou por nome parecido."""
    if ID_RE.match(str(ref or "")):
        for it in itens:
            if it.get("id") == ref:
                return it
    alvo = norm(ref)
    exatos = [it for it in itens if norm(it.get("name")) == alvo]
    if len(exatos) == 1:
        return exatos[0]
    if len(exatos) > 1:
        die("tem mais de %s com o nome %r: %s" % (oque, ref,
            ", ".join(it["id"] for it in exatos)))
    parciais = [it for it in itens
                if alvo and (alvo in norm(it.get("name"))
                             or norm(it.get("name")) in alvo)]
    if len(parciais) == 1:
        return parciais[0]
    nomes = ", ".join(repr(it.get("name")) for it in itens) or "(nenhum)"
    if len(parciais) > 1:
        die("%r combina com mais de %s: %s" % (ref, oque,
            ", ".join(repr(it.get("name")) for it in parciais)))
    die("nao achei %s %r. Disponiveis: %s" % (oque, ref, nomes))


# ------------------------------------------------------------ quadros/listas

def listar_quadros(cfg: dict) -> list:
    return call("GET", "/members/me/boards", cfg,
                {"filter": "open", "fields": "name,url"}) or []


def achar_quadro(cfg: dict, ref: str | None) -> dict:
    ref = ref or cfg.get("board_id") or cfg.get("board_name")
    if not ref:
        die("nao sei em qual quadro mexer. Rode 'quadros' e depois "
            "'padrao --quadro <nome>' ou passe --quadro.")
    return escolher(listar_quadros(cfg), ref, "quadro")


def listar_listas(cfg: dict, quadro_id: str) -> list:
    return call("GET", "/boards/%s/lists" % quadro_id, cfg,
                {"filter": "open", "fields": "name"}) or []


def achar_lista(cfg: dict, quadro: dict, ref: str | None) -> dict:
    ref = ref or cfg.get("list_id") or cfg.get("list_name")
    listas = listar_listas(cfg, quadro["id"])
    if not ref:
        if not listas:
            die("o quadro %r nao tem lista aberta." % quadro.get("name"))
        return listas[0]
    return escolher(listas, ref, "lista")


def listar_etiquetas(cfg: dict, quadro_id: str) -> list:
    etqs = call("GET", "/boards/%s/labels" % quadro_id, cfg,
                {"fields": "name,color"}) or []
    for e in etqs:
        if not e.get("name"):
            e["name"] = e.get("color") or ""
    return etqs


def listar_membros(cfg: dict, quadro_id: str) -> list:
    membros = call("GET", "/boards/%s/members" % quadro_id, cfg,
                   {"fields": "fullName,username"}) or []
    for m in membros:
        m["name"] = m.get("fullName") or m.get("username") or ""
    return membros


def listar_cards(cfg: dict, quadro_id: str) -> list:
    return call("GET", "/boards/%s/cards" % quadro_id, cfg,
                {"fields": "name,idList,url,shortUrl"}) or []


# ------------------------------------------------------------------- criacao

def criar_card(cfg: dict, quadro: dict, dados: dict) -> dict:
    titulo = (dados.get("titulo") or dados.get("name") or "").strip()
    if not titulo:
        die("card sem titulo: %s" % json.dumps(dados, ensure_ascii=False))

    lista = achar_lista(cfg, quadro, dados.get("lista"))

    ids_etq = []
    nomes_etq = dados.get("etiquetas") or []
    if isinstance(nomes_etq, str):
        nomes_etq = [p.strip() for p in nomes_etq.split(",") if p.strip()]
    if nomes_etq:
        disponiveis = listar_etiquetas(cfg, quadro["id"])
        for nome in nomes_etq:
            ids_etq.append(escolher(disponiveis, nome, "etiqueta")["id"])

    ids_membro = []
    nomes_membro = dados.get("membros") or []
    if isinstance(nomes_membro, str):
        nomes_membro = [p.strip() for p in nomes_membro.split(",") if p.strip()]
    if nomes_membro:
        disponiveis = listar_membros(cfg, quadro["id"])
        for nome in nomes_membro:
            ids_membro.append(escolher(disponiveis, nome, "membro")["id"])

    card = call("POST", "/cards", cfg, {
        "idList": lista["id"],
        "name": titulo,
        "desc": dados.get("desc") or dados.get("descricao") or "",
        "due": dados.get("prazo") or dados.get("due"),
        "idLabels": ids_etq,
        "idMembers": ids_membro,
        "pos": dados.get("posicao") or "bottom",
    })

    itens = dados.get("checklist") or []
    if isinstance(itens, str):
        itens = [p.strip() for p in itens.split(",") if p.strip()]
    if itens:
        cl = call("POST", "/cards/%s/checklists" % card["id"], cfg,
                  {"name": dados.get("checklist_nome") or "Passos"})
        for item in itens:
            call("POST", "/checklists/%s/checkItems" % cl["id"], cfg,
                 {"name": str(item), "checked": False})

    card["_lista"] = lista["name"]
    return card


def mostrar_card(card: dict) -> None:
    print("  + %s  [%s]  %s" % (card.get("name"),
                                card.get("_lista", "?"),
                                card.get("shortUrl") or card.get("url") or ""))


# ------------------------------------------------------------------ comandos

def cmd_configurar(args, cfg):
    if args.chave:
        cfg["key"] = args.chave.strip()
    if args.token:
        cfg["token"] = args.token.strip()
    save_config(cfg)
    creds(cfg)
    eu = call("GET", "/members/me", cfg, {"fields": "username,fullName"})
    print("Chave guardada em %s" % config_path())
    print("Conectado no Trello como %s (@%s)" % (eu.get("fullName"),
                                                 eu.get("username")))
    print("Agora rode: python tools/jumento/trello.py quadros")


def cmd_status(args, cfg):
    print("Config: %s" % config_path())
    if not cfg:
        print("  (nada configurado ainda)")
        return
    token = cfg.get("token") or ""
    print("  chave : %s" % (("..." + cfg["key"][-4:]) if cfg.get("key") else "-"))
    print("  token : %s" % (("..." + token[-4:]) if token else "-"))
    print("  quadro padrao: %s" % (cfg.get("board_name") or cfg.get("board_id") or "-"))
    print("  lista padrao : %s" % (cfg.get("list_name") or cfg.get("list_id") or "-"))


def cmd_quadros(args, cfg):
    quadros = listar_quadros(cfg)
    if not quadros:
        print("Nenhum quadro aberto nessa conta.")
        return
    for q in quadros:
        print("%s  %s  %s" % (q["id"], q["name"], q.get("url", "")))


def cmd_listas(args, cfg):
    quadro = achar_quadro(cfg, args.quadro)
    print("Quadro: %s" % quadro["name"])
    for l in listar_listas(cfg, quadro["id"]):
        print("  %s  %s" % (l["id"], l["name"]))


def cmd_etiquetas(args, cfg):
    quadro = achar_quadro(cfg, args.quadro)
    print("Quadro: %s" % quadro["name"])
    for e in listar_etiquetas(cfg, quadro["id"]):
        print("  %s  %-20s (%s)" % (e["id"], e.get("name"), e.get("color")))


def cmd_membros(args, cfg):
    quadro = achar_quadro(cfg, args.quadro)
    print("Quadro: %s" % quadro["name"])
    for m in listar_membros(cfg, quadro["id"]):
        print("  %s  %s (@%s)" % (m["id"], m.get("fullName"), m.get("username")))


def cmd_padrao(args, cfg):
    if args.quadro:
        quadro = achar_quadro(cfg, args.quadro)
        cfg["board_id"] = quadro["id"]
        cfg["board_name"] = quadro["name"]
        cfg.pop("list_id", None)
        cfg.pop("list_name", None)
    quadro = achar_quadro(cfg, None)
    if args.lista:
        lista = achar_lista(cfg, quadro, args.lista)
        cfg["list_id"] = lista["id"]
        cfg["list_name"] = lista["name"]
    save_config(cfg)
    print("Padrao salvo: quadro %r / lista %r" % (
        cfg.get("board_name"), cfg.get("list_name") or "(a primeira da esquerda)"))


def cmd_card(args, cfg):
    quadro = achar_quadro(cfg, args.quadro)
    card = criar_card(cfg, quadro, {
        "titulo": args.titulo,
        "desc": args.desc,
        "lista": args.lista,
        "prazo": args.prazo,
        "etiquetas": args.etiquetas,
        "membros": args.membros,
        "checklist": args.checklist,
        "posicao": args.posicao,
    })
    print("Criado em %s:" % quadro["name"])
    mostrar_card(card)


def cmd_lote(args, cfg):
    if args.arquivo == "-":
        bruto = sys.stdin.read()
    else:
        with open(args.arquivo, "r", encoding="utf-8-sig") as f:
            bruto = f.read()
    try:
        dados = json.loads(bruto)
    except ValueError as e:
        die("o arquivo nao e um JSON valido: %s" % e)
    if isinstance(dados, dict):
        dados = dados.get("cards") or [dados]
    if not isinstance(dados, list) or not dados:
        die("esperava uma lista de cards no JSON.")
    quadro = achar_quadro(cfg, args.quadro)
    print("Criando %d card(s) em %s:" % (len(dados), quadro["name"]))
    for item in dados:
        if isinstance(item, str):
            item = {"titulo": item}
        if args.lista and not item.get("lista"):
            item["lista"] = args.lista
        mostrar_card(criar_card(cfg, quadro, item))


def cmd_ver(args, cfg):
    quadro = achar_quadro(cfg, args.quadro)
    listas = {l["id"]: l["name"] for l in listar_listas(cfg, quadro["id"])}
    alvo = norm(args.lista) if args.lista else None
    print("Quadro: %s" % quadro["name"])
    for c in listar_cards(cfg, quadro["id"]):
        nome_lista = listas.get(c.get("idList"), "?")
        if alvo and alvo not in norm(nome_lista):
            continue
        print("  %s  [%s]  %s" % (c["id"], nome_lista, c["name"]))


def _achar_card(cfg, quadro, ref):
    return escolher(listar_cards(cfg, quadro["id"]), ref, "card")


def cmd_mover(args, cfg):
    quadro = achar_quadro(cfg, args.quadro)
    card = _achar_card(cfg, quadro, args.card)
    lista = achar_lista(cfg, quadro, args.para)
    call("PUT", "/cards/%s" % card["id"], cfg,
         {"idList": lista["id"], "pos": args.posicao or "bottom"})
    print("Movido: %r -> lista %r" % (card["name"], lista["name"]))


def cmd_comentar(args, cfg):
    quadro = achar_quadro(cfg, args.quadro)
    card = _achar_card(cfg, quadro, args.card)
    call("POST", "/cards/%s/actions/comments" % card["id"], cfg,
         {"text": args.texto})
    print("Comentario adicionado em %r" % card["name"])


def cmd_arquivar(args, cfg):
    quadro = achar_quadro(cfg, args.quadro)
    card = _achar_card(cfg, quadro, args.card)
    call("PUT", "/cards/%s" % card["id"], cfg, {"closed": True})
    print("Arquivado: %r" % card["name"])


# ---------------------------------------------------------------------- main

def main(argv=None):
    p = argparse.ArgumentParser(
        prog="trello.py",
        description="Jumento Vinicius: cria e mexe em cards do Trello.")
    sub = p.add_subparsers(dest="cmd", required=True)

    def add(nome, func, ajuda, quadro=True):
        sp = sub.add_parser(nome, help=ajuda)
        sp.set_defaults(func=func)
        if quadro:
            sp.add_argument("--quadro", help="id ou nome do quadro")
        return sp

    sp = sub.add_parser("configurar", help="guarda a chave e o token")
    sp.set_defaults(func=cmd_configurar)
    sp.add_argument("--chave", required=True)
    sp.add_argument("--token", required=True)

    sp = sub.add_parser("status", help="mostra o que esta configurado")
    sp.set_defaults(func=cmd_status)

    add("quadros", cmd_quadros, "lista os quadros da conta", quadro=False)
    add("listas", cmd_listas, "lista as colunas do quadro")
    add("etiquetas", cmd_etiquetas, "lista as etiquetas do quadro")
    add("membros", cmd_membros, "lista os membros do quadro")

    sp = add("padrao", cmd_padrao, "define quadro/lista padrao")
    sp.add_argument("--lista")

    sp = add("card", cmd_card, "cria UM card")
    sp.add_argument("--titulo", required=True)
    sp.add_argument("--desc", default="")
    sp.add_argument("--lista")
    sp.add_argument("--prazo", help="data ISO, ex 2026-12-01T18:00:00Z")
    sp.add_argument("--etiquetas", help="nomes separados por virgula")
    sp.add_argument("--membros", help="nomes/usuarios separados por virgula")
    sp.add_argument("--checklist", help="itens separados por virgula")
    sp.add_argument("--posicao", default="bottom", help="top ou bottom")

    sp = add("lote", cmd_lote, "cria varios cards a partir de um JSON")
    sp.add_argument("--arquivo", required=True, help="caminho do .json ou - para stdin")
    sp.add_argument("--lista", help="lista padrao para os cards do arquivo")

    sp = add("ver", cmd_ver, "lista os cards do quadro")
    sp.add_argument("--lista", help="filtra por lista")

    sp = add("mover", cmd_mover, "move um card para outra lista")
    sp.add_argument("--card", required=True)
    sp.add_argument("--para", required=True)
    sp.add_argument("--posicao")

    sp = add("comentar", cmd_comentar, "comenta em um card")
    sp.add_argument("--card", required=True)
    sp.add_argument("--texto", required=True)

    sp = add("arquivar", cmd_arquivar, "arquiva (conclui) um card")
    sp.add_argument("--card", required=True)

    args = p.parse_args(argv)
    cfg = load_config()
    args.func(args, cfg)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
