p='src/server/Pontual.Server/Api/CrmEndpoints.cs'
s=open(p,encoding='utf-8').read()
a=s.index('            var list = await crm.ProbeAsync(access, "/api/chat/contacts?page=1')
b=s.index('            var history = "";')
new='''            const string Base = "/api/chat/contacts?page=1&busca=&botId=todos&vendedores=&tags=&ordem=desc";
            var list = await crm.ProbeAsync(access, Base + "&status=todos&etapas=&periodo=todos&dataInicio=&dataFim=", ct);
            var variants = new StringBuilder();
            foreach (var (name, query) in new[]
            {
                ("recebidas", "&status=recebidas&etapas=&periodo=todos&dataInicio=&dataFim="),
                ("recebidas15", $"&status=recebidas&etapas=&periodo=custom&dataInicio={today.AddDays(-15):yyyy-MM-dd}&dataFim={today:yyyy-MM-dd}"),
                ("etapaPerdido", "&status=todos&etapas=10&periodo=todos&dataInicio=&dataFim="),
            })
            {
                var answer = await crm.ProbeAsync(access, Base + query, ct);
                variants.Append("\n--- ").Append(name).Append(' ').Append(answer.Length).Append('\n').Append(answer.Length <= 6000 ? answer : answer[..6000]);
            }

'''
s=s[:a]+new+s[b:]
old='{Cut(structure)}";'
assert old in s
s=s.replace(old,'{Cut(structure)}\n=== VARIANTES\n{variants}";')
open(p,'w',encoding='utf-8',newline='').write(s)
