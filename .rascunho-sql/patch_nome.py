import io, re, sys
root = r"f:/Projetos/Terminal-Pontual/"
files = [
 "src/server/Pontual.Server/Api/CrmEndpoints.cs",
 "src/server/Pontual.Server/Api/NeuralEndpoints.cs",
 "src/server/Pontual.Server/Api/SiapeEndpoints.cs",
 "src/server/Pontual.Server/Assistant/SiapeMargin.cs",
 "src/server/Pontual.Server/Data/Entities.cs",
 "src/server/Pontual.Server/Program.cs",
 "src/server/Pontual.Server/wwwroot/central/neural/aprovar.js",
 "src/server/Pontual.Server/wwwroot/central/neural/index.html",
 "src/server/Pontual.Server/wwwroot/central/neural/neural.css",
 "src/server/Pontual.Server/wwwroot/central/neural/neural.js",
 "src/server/Pontual.Server/wwwroot/central/neural/siape.js",
 "tests/server/Pontual.Server.Tests/Api/SiapeTests.cs",
 ".rascunho-central-neural/mapa-teste/real.html",
 ".rascunho-central-neural/siape-teste/teste.html",
]
total = 0
for f in files:
    p = root + f
    s = io.open(p, encoding="utf-8", newline="").read()
    o = s
    # specific phrases first
    s = s.replace("Valor liberado para consignado neste mês (margem liberada): ", "Valor liberado para consignado neste mês: ")
    s = s.replace("tem um valor liberado para crédito consignado (margem liberada): é isso", "tem um valor liberado para crédito consignado: é isso")
    s = s.replace("O assunto é a margem liberada:", "O assunto é o valor liberado:")
    s = s.replace("conta que ele está com valor liberado (margem liberada) e convida", "conta que ele está com valor liberado e convida")
    s = s.replace("MODO MARGEM LIBERADA (SIAPE)", "MODO VALOR LIBERADO (SIAPE)")
    s = s.replace('"MODO MARGEM LIBERADA"', '"MODO VALOR LIBERADO"')
    s = s.replace("Últimas mensagens de margem", "Últimas mensagens de valor liberado")
    s = s.replace("para os servidores com margem nova", "para os servidores com valor liberado")
    s = s.replace("lê a base de margens do SIAPE, a IA escreve para quem ganhou margem", "lê a base do SIAPE, a IA escreve para quem está com valor liberado acima do mínimo")
    s = s.replace("A base de margens no SQL Server da Kamila", "A base do SIAPE no SQL Server da Kamila")
    s = s.replace("Base de margens do SIAPE (SQL)", "Base do SIAPE (SQL)")
    s = s.replace("lê a base de margens do SIAPE", "lê a base do SIAPE")
    s = s.replace("Mensagem de margem liberada", "Mensagem de valor liberado")
    s = s.replace("mensagem de margem liberada", "mensagem de valor liberado")
    s = s.replace("mensagem(ns) de margem liberada", "mensagem(ns) de valor liberado")
    s = s.replace("busca de margem liberada", "busca de valor liberado")
    s = s.replace("busca mensal de margem liberada", "busca mensal de valor liberado")
    s = s.replace("salvou a margem liberada do SIAPE", "salvou o valor liberado do SIAPE")
    s = s.replace("A margem liberada não usa o chat", "O valor liberado não usa o chat")
    s = s.replace("o modo margem liberada", "o modo valor liberado")
    s = s.replace("Margem liberada do SIAPE salva.", "Valor liberado do SIAPE salvo.")
    # generic remaining
    s = s.replace("Margem liberada", "Valor liberado").replace("margem liberada", "valor liberado").replace("MARGEM LIBERADA", "VALOR LIBERADO")
    n = sum(1 for _ in re.finditer("alor liberado", s)) - sum(1 for _ in re.finditer("alor liberado", o))
    if s != o:
        io.open(p, "w", encoding="utf-8", newline="").write(s)
        total += n
        print(f"{f}: +{n}")
    assert not re.search("margem liberada", s, re.I), f
print("total", total)
