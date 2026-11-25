import sqlite3,re,sys
c=sqlite3.connect('file:dist/Central-do-TI/data/pontual.db?mode=ro',uri=True)
r=c.execute("select Value from TextSettings where Key='crm.lostProbe'").fetchone()
if not r: print('SEM SONDAGEM AINDA'); raise SystemExit
n=int(sys.argv[1]) if len(sys.argv)>1 else 2600
parts=re.split(r'\n=== (\w+)\n',r[0])
print(parts[0])
for i in range(1,len(parts),2):
    print('#####',parts[i],len(parts[i+1])); print(parts[i+1][:n]); print()
